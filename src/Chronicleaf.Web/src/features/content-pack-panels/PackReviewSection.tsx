import { useCallback, useEffect, useState } from 'react';

import type { ContentAsset } from '../grounded-markdown-drafts/types';
import {
  approveAsset,
  editAsset,
  readReview,
} from '../markdown-review-export/api';
import { ReviewApiError, type ContentReview } from '../markdown-review-export/types';
import { readVariant } from '../content-asset-variants/api';
import { attemptPackPublish, downloadPack, savePackDownload } from './api';
import { PackApiError, type PackFormat } from './types';

/**
 * The shared review surface of one derived pack
 * (CONTENT-PACK-PANELS-001/002/003).
 *
 * It reads the pack and its review from the delivered routes, previews the
 * stored bytes as inert characters, and offers exactly the four things the
 * contracts allow: a human edit appending a revision, a press-only approval
 * bound to the head revision, an exact-byte download behind a live approval,
 * and a publish-attempt whose only honest answer is the server's own
 * `publisher_unsupported`. Nothing here fires on render except the two
 * reads; every mutation leaves from its own explicit control.
 */
export function PackReviewSection({
  assetId,
  format,
  namespace,
  packName,
  archived,
  preview,
}: {
  /** The derived pack asset to review. Fixed by the picker that opened this section. */
  readonly assetId: string;
  /** The export/refusal route suffix this pack answers on. */
  readonly format: PackFormat;
  /** Testid scope, one per hosting panel. */
  readonly namespace: string;
  /** The pack's display name, for copy the server did not write. */
  readonly packName: string;
  /** An archived milestone's packs are kept and cannot be added to, so no edit/approve/attempt control is offered. */
  readonly archived: boolean;
  /** The pack-specific inert preview of the stored asset. */
  readonly preview: (asset: ContentAsset) => React.ReactNode;
}) {
  const [asset, setAsset] = useState<ContentAsset | null>(null);
  const [review, setReview] = useState<ContentReview | null>(null);
  const [reading, setReading] = useState(true);
  const [readFailure, setReadFailure] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [editBody, setEditBody] = useState('');
  const [editTitle, setEditTitle] = useState('');
  const [editFailure, setEditFailure] = useState<ReviewApiError | null>(null);
  const [approving, setApproving] = useState(false);
  const [approveFailure, setApproveFailure] = useState<ReviewApiError | null>(null);
  const [downloading, setDownloading] = useState(false);
  const [downloadFailure, setDownloadFailure] = useState<PackApiError | null>(null);
  const [refusing, setRefusing] = useState(false);
  const [refusal, setRefusal] = useState<PackApiError | null>(null);

  const load = useCallback(async () => {
    setReading(true);
    setReadFailure(null);

    try {
      const [read, state] = await Promise.all([readVariant(assetId), readReview(assetId)]);
      setAsset(read);
      setReview(state);
      setEditBody(read.bodyMarkdown ?? '');
      setEditTitle(read.title ?? '');
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : `The ${packName} could not be read.`,
      );
    } finally {
      setReading(false);
    }
  }, [assetId, packName]);

  useEffect(() => {
    void load();
  }, [load]);

  const edit = useCallback(async () => {
    if (asset?.currentRevision == null) {
      return;
    }

    setEditing(true);
    setEditFailure(null);

    try {
      await editAsset(assetId, asset.currentRevision, {
        bodyMarkdown: editBody,
        title: editTitle.trim() === '' ? null : editTitle.trim(),
      });
      await load();
    } catch (thrown) {
      setEditFailure(asReviewError(thrown));
    } finally {
      setEditing(false);
    }
  }, [asset, assetId, editBody, editTitle, load]);

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
      const { fileName, bytes } = await downloadPack(assetId, format, null);
      savePackDownload(fileName, bytes);
    } catch (thrown) {
      setDownloadFailure(asPackError(thrown));
    } finally {
      setDownloading(false);
    }
  }, [assetId, format]);

  const attemptPublish = useCallback(async () => {
    setRefusing(true);
    setRefusal(null);

    try {
      await attemptPackPublish(assetId, format);
    } catch (thrown) {
      // The refusal IS the representation: a 422 publisher_unsupported is the
      // server's honest answer, and it is shown verbatim rather than mapped.
      setRefusal(asPackError(thrown));
    } finally {
      setRefusing(false);
    }
  }, [assetId, format]);

  if (reading) {
    return <p data-testid={`${namespace}-review-loading`}>Reading the {packName}…</p>;
  }

  if (readFailure !== null || asset === null) {
    return (
      <p role="alert" data-testid={`${namespace}-review-failure`}>
        {readFailure ?? `The ${packName} could not be read.`}
      </p>
    );
  }

  const approved = review?.reviewStatus === 'approved' && review.approval !== null;

  return (
    <article data-testid={`${namespace}-review`}>
      <p data-testid={`${namespace}-review-revision`}>Revision {asset.currentRevision}</p>
      <p data-testid={`${namespace}-review-status`}>{review?.reviewStatus ?? 'draft'}</p>
      {asset.bodyHash ? (
        <p data-testid={`${namespace}-review-hash`}>Body {asset.bodyHash}</p>
      ) : null}
      {asset.voiceRevision !== null || asset.voiceFingerprint !== null ? (
        <p data-testid={`${namespace}-review-voice`}>
          Written under voice revision {asset.voiceRevision ?? 'unknown'}
          {asset.voiceFingerprint ? ` (${asset.voiceFingerprint.slice(0, 12)}…)` : ''}.
        </p>
      ) : null}

      <div data-testid={`${namespace}-review-preview`}>{preview(asset)}</div>

      {archived ? null : (
        <div>
          <label className="form__label">
            Edit the {packName} text
          <textarea
            className="form__input"
            value={editBody}
            onChange={(event) => setEditBody(event.target.value)}
            data-testid={`${namespace}-edit-body`}
            disabled={editing}
          />
        </label>
        <label className="form__label">
          Edit the {packName} title
          <input
            type="text"
            className="form__input"
            value={editTitle}
            onChange={(event) => setEditTitle(event.target.value)}
            data-testid={`${namespace}-edit-title`}
            disabled={editing}
          />
        </label>
        <button
          type="button"
          onClick={() => void edit()}
          disabled={editing}
          data-testid={`${namespace}-edit-save`}
        >
          {editing ? 'Saving…' : 'Save a new revision'}
        </button>
        {editFailure !== null ? (
          <p role="alert" data-testid={`${namespace}-edit-failure`}>
            {editFailure.code}: {editFailure.message}
          </p>
        ) : null}
        </div>
      )}

      {archived ? null : (
      <div>
        <button
          type="button"
          onClick={() => void approve()}
          disabled={approving}
          data-testid={`${namespace}-approve`}
        >
          {approving ? 'Approving…' : 'Approve this revision'}
        </button>
        {approveFailure !== null ? (
          <p role="alert" data-testid={`${namespace}-approve-failure`}>
            {approveFailure.code}: {approveFailure.message}
          </p>
        ) : null}
        {!approved ? (
          <p data-testid={`${namespace}-download-unavailable`}>
            The download needs a live approval for this revision. Approve it first.
          </p>
        ) : null}
      </div>
      )}

      {approved ? (
        <div>
          <button
            type="button"
            onClick={() => void download()}
            disabled={downloading}
            data-testid={`${namespace}-download`}
          >
            {downloading ? 'Downloading…' : `Download the ${packName}`}
          </button>
          {downloadFailure !== null ? (
            <p role="alert" data-testid={`${namespace}-download-failure`}>
              {downloadFailure.code}: {downloadFailure.message}
            </p>
          ) : null}
        </div>
      ) : null}

      {archived ? null : (
      <div>
        <button
          type="button"
          onClick={() => void attemptPublish()}
          disabled={refusing}
          data-testid={`${namespace}-publish-attempt`}
        >
          {refusing ? 'Asking…' : 'Attempt native publish'}
        </button>
        {refusal !== null ? (
          <p role="alert" data-testid={`${namespace}-publish-refusal`}>
            {refusal.code}: {refusal.message}
          </p>
        ) : null}
      </div>
      )}
    </article>
  );
}

function asPackError(thrown: unknown): PackApiError {
  if (thrown instanceof PackApiError) {
    return thrown;
  }

  return new PackApiError(
    {
      code: 'internal_error',
      message: thrown instanceof Error ? thrown.message : 'The pack request could not be completed.',
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
