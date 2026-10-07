import { useCallback, useEffect, useState } from 'react';

import type { ContentAsset } from '../grounded-markdown-drafts/types';
import { approveAsset, readReview } from '../markdown-review-export/api';
import { ReviewApiError, type ContentReview } from '../markdown-review-export/types';
import { readVariant } from '../content-asset-variants/api';
import {
  attemptVideoPublish,
  downloadScript,
  saveScriptDownload,
} from './api';
import {
  VideoStoryApiError,
  formatTimestamp,
  parseStoryboard,
} from './types';

/**
 * The shared review surface of one scripted video story
 * (VIDEO-STORY-PANEL-002).
 *
 * It reads the script and its review from the delivered routes, previews
 * the stored shot order as inert characters with the served pictures beside
 * them, and offers exactly the three things the contracts allow: a
 * press-only approval bound to the head revision, an exact-byte download
 * whose refusals render verbatim, and a publish-attempt whose only honest
 * answer is the server's own `publisher_unsupported`. Nothing here fires on
 * render except the two reads; every mutation leaves from its own explicit
 * control. There is no edit control: shots are immutable and re-scripting
 * through the generate route is the update path.
 */
export function VideoStoryReviewSection({
  assetId,
  mediaBaseUrl,
  archived,
  reloadToken,
}: {
  /** The scripted story asset to review. Fixed by the discovery read that opened this section. */
  readonly assetId: string;
  /** The delivered media route the depicted pictures are served from. */
  readonly mediaBaseUrl: string;
  /** An archived milestone's scripts are kept and cannot be added to, so no approve/attempt control is offered. */
  readonly archived: boolean;
  /** Bumped by the composer after it scripts a newer revision, so this section re-reads rather than showing the superseded preview. */
  readonly reloadToken: number;
}) {
  const [asset, setAsset] = useState<ContentAsset | null>(null);
  const [review, setReview] = useState<ContentReview | null>(null);
  const [reading, setReading] = useState(true);
  const [readFailure, setReadFailure] = useState<string | null>(null);
  const [approving, setApproving] = useState(false);
  const [approveFailure, setApproveFailure] = useState<ReviewApiError | null>(null);
  const [downloading, setDownloading] = useState(false);
  const [downloadFailure, setDownloadFailure] = useState<VideoStoryApiError | null>(null);
  const [refusing, setRefusing] = useState(false);
  const [refusal, setRefusal] = useState<VideoStoryApiError | null>(null);

  const load = useCallback(async () => {
    setReading(true);
    setReadFailure(null);

    try {
      const [read, state] = await Promise.all([readVariant(assetId), readReview(assetId)]);
      setAsset(read);
      setReview(state);
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The video story could not be read.',
      );
    } finally {
      setReading(false);
    }
  }, [assetId, reloadToken]);

  useEffect(() => {
    void load();
  }, [load]);

  const approve = useCallback(async () => {
    if (asset?.currentRevision == null || asset.evidenceRevision == null) {
      return;
    }

    setApproving(true);
    setApproveFailure(null);

    try {
      await approveAsset(assetId, {
        contentRevision: asset.currentRevision,
        evidenceRevision: asset.evidenceRevision,
      });
      const state = await readReview(assetId);
      setReview(state);
    } catch (thrown) {
      setApproveFailure(asReviewError(thrown));
    } finally {
      setApproving(false);
    }
  }, [asset, assetId]);

  const download = useCallback(async () => {
    setDownloading(true);
    setDownloadFailure(null);

    try {
      const { fileName, bytes } = await downloadScript(assetId, null);
      saveScriptDownload(fileName, bytes);
    } catch (thrown) {
      setDownloadFailure(asStoryError(thrown));
    } finally {
      setDownloading(false);
    }
  }, [assetId]);

  const attemptPublish = useCallback(async () => {
    setRefusing(true);
    setRefusal(null);

    try {
      await attemptVideoPublish(assetId);
    } catch (thrown) {
      // The refusal IS the representation: a 422 publisher_unsupported is the
      // server's honest answer, and it is shown verbatim rather than mapped.
      setRefusal(asStoryError(thrown));
    } finally {
      setRefusing(false);
    }
  }, [assetId]);

  if (reading) {
    return <p data-testid="video-story-review-loading">Reading the video story…</p>;
  }

  if (readFailure !== null || asset === null) {
    return (
      <p role="alert" data-testid="video-story-review-failure">
        {readFailure ?? 'The video story could not be read.'}
      </p>
    );
  }

  const approved = review?.reviewStatus === 'approved' && review.approval !== null;
  const storyboard = parseStoryboard(asset.bodyMarkdown);

  return (
    <article data-testid="video-story-review">
      <p data-testid="video-story-review-revision">Revision {asset.currentRevision}</p>
      <p data-testid="video-story-review-status">{review?.reviewStatus ?? 'draft'}</p>
      {asset.bodyHash ? (
        <p data-testid="video-story-review-hash">Body {asset.bodyHash}</p>
      ) : null}
      {asset.voiceRevision !== null || asset.voiceFingerprint !== null ? (
        <p data-testid="video-story-review-voice">
          Written under voice revision {asset.voiceRevision ?? 'unknown'}
          {asset.voiceFingerprint ? ` (${asset.voiceFingerprint.slice(0, 12)}…)` : ''}.
        </p>
      ) : null}

      {storyboard === null ? (
        <p role="alert" data-testid="video-story-review-unreadable">
          The stored script could not be read as a storyboard, so no shot order is shown.
        </p>
      ) : (
        <div data-testid="video-story-review-preview">
          <p data-testid="video-story-review-duration">
            {storyboard.durationSeconds}-second storyboard: {storyboard.shots.length}{' '}
            {storyboard.shots.length === 1 ? 'shot' : 'shots'}. Scripts only — no footage
            is claimed and none is attached.
          </p>
          <ol>
            {storyboard.shots.map((shot, index) => (
              <li key={`${shot.screenshotMediaId}-${shot.startMs}`} data-testid={`video-story-review-shot-${index}`}>
                <img
                  src={`${mediaBaseUrl}/${shot.screenshotMediaId}`}
                  alt=""
                  data-testid={`video-story-review-shot-${index}-image`}
                />
                <p data-testid={`video-story-review-shot-${index}-timing`}>
                  Shot {index + 1}: {formatTimestamp(shot.startMs)} to{' '}
                  {formatTimestamp(shot.endMs)}, {shot.transition}.
                </p>
                <p data-testid={`video-story-review-shot-${index}-caption`}>{shot.caption}</p>
                <p data-testid={`video-story-review-shot-${index}-narration`}>{shot.narration}</p>
                <p data-testid={`video-story-review-shot-${index}-evidence`}>
                  Evidence: {shot.sourceEvidenceIds.join(', ')}.
                </p>
              </li>
            ))}
          </ol>
          {storyboard.limitations.length > 0 ? (
            <div data-testid="video-story-review-limitations">
              <h4>Known limitations</h4>
              <ul>
                {storyboard.limitations.map((limitation) => (
                  <li key={limitation}>{limitation}</li>
                ))}
              </ul>
            </div>
          ) : null}
        </div>
      )}

      {archived ? null : (
        <div>
          <button
            type="button"
            onClick={() => void approve()}
            disabled={approving}
            data-testid="video-story-approve"
          >
            {approving ? 'Approving…' : 'Approve this revision'}
          </button>
          {approveFailure !== null ? (
            <p role="alert" data-testid="video-story-approve-failure">
              {approveFailure.code}: {approveFailure.message}
            </p>
          ) : null}
          {!approved ? (
            <p data-testid="video-story-download-gated">
              The download needs a live approval for this revision. Approve it first — an
              earlier attempt is refused by the server rather than served.
            </p>
          ) : null}
        </div>
      )}

      <div>
        <button
          type="button"
          onClick={() => void download()}
          disabled={downloading}
          data-testid="video-story-download"
        >
          {downloading ? 'Downloading…' : 'Download the storyboard package'}
        </button>
        {downloadFailure !== null ? (
          <p role="alert" data-testid="video-story-download-failure">
            {downloadFailure.code}: {downloadFailure.message}
          </p>
        ) : null}
      </div>

      {archived ? null : (
        <div>
          <button
            type="button"
            onClick={() => void attemptPublish()}
            disabled={refusing}
            data-testid="video-story-publish-attempt"
          >
            {refusing ? 'Asking…' : 'Attempt native video publish'}
          </button>
          {refusal !== null ? (
            <p role="alert" data-testid="video-story-publish-refusal">
              {refusal.code}: {refusal.message}
            </p>
          ) : null}
        </div>
      )}
    </article>
  );
}

function asStoryError(thrown: unknown): VideoStoryApiError {
  if (thrown instanceof VideoStoryApiError) {
    return thrown;
  }

  return new VideoStoryApiError(
    {
      code: 'internal_error',
      message: thrown instanceof Error ? thrown.message : 'The video story request could not be completed.',
      traceId: '',
    },
    0,
  );
}

function asReviewError(thrown: unknown): ReviewApiError {
  if (thrown instanceof ReviewApiError) {
    return thrown;
  }

  return new ReviewApiError(
    {
      code: 'internal_error',
      message: thrown instanceof Error ? thrown.message : 'The review request could not be completed.',
      traceId: '',
    },
    0,
  );
}
