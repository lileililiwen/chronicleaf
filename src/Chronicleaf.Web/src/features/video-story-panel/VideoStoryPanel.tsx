import { useCallback, useEffect, useState } from 'react';

import { readDraft, readEvidenceHead } from '../grounded-markdown-drafts/api';
import type { ContentAsset } from '../grounded-markdown-drafts/types';
import { readReview } from '../markdown-review-export/api';
import { listEvidence } from '../milestone-evidence/api';
import type { EvidenceItem } from '../milestone-evidence/types';
import { listCurated } from '../screenshot-curation/api';
import { readVoiceProfile } from '../voice-profiles/api';
import { generateScript, readScript } from './api';
import {
  VIDEO_STORY_DURATIONS,
  VIDEO_STORY_MAX_SHOTS,
  VIDEO_STORY_MIN_SHOT_MS,
  VIDEO_STORY_TRANSITIONS,
  VideoStoryApiError,
  deriveTimings,
  formatTimestamp,
  type ComposedShot,
  type VideoStoryDuration,
  type VideoStorySummary,
  type VideoStoryTransition,
} from './types';
import { VideoStoryReviewSection } from './VideoStoryReviewSection';

/**
 * The video-story storyboard panel (VIDEO-STORY-PANEL-001/002).
 *
 * <b>The script is scripted, reviewed and carried by hand — never
 * synthesized and never sent.</b> The generate POST stores the owner's own
 * shot list against the approved story, the frozen evidence head and the
 * current voice; the preview shows the stored shot order with the served
 * pictures beside inert text; the download hands the server's exact ZIP to a
 * file. The publish-attempt POST exists so the server can answer
 * `publisher_unsupported` in its own words instead of a 404 that would send
 * the owner looking for another route. There is no renderer, no encoder, no
 * account and no transport behind any control here, and the about copy says
 * so on screen rather than leaving it unsaid.
 */
export function VideoStoryPanel({
  projectId,
  milestoneId,
  milestoneTitle,
  archived,
  confirmed,
}: {
  readonly projectId: string;
  readonly milestoneId: string;
  readonly milestoneTitle: string;
  readonly archived: boolean;
  readonly confirmed: boolean;
}) {
  const [story, setStory] = useState<ContentAsset | null>(null);
  const [storyApproved, setStoryApproved] = useState(false);
  const [evidenceItems, setEvidenceItems] = useState<readonly EvidenceItem[]>([]);
  const [pictures, setPictures] = useState<readonly SelectedPicture[]>([]);
  const [mediaBaseUrl, setMediaBaseUrl] = useState('/api/media');
  const [summary, setSummary] = useState<VideoStorySummary | null>(null);
  const [duration, setDuration] = useState<VideoStoryDuration>(15);
  const [shots, setShots] = useState<readonly ComposedShot[]>([]);
  const [reading, setReading] = useState(true);
  const [readFailure, setReadFailure] = useState<string | null>(null);
  const [scripting, setScripting] = useState(false);
  const [scriptFailure, setScriptFailure] = useState<VideoStoryApiError | null>(null);
  const [reloadToken, setReloadToken] = useState(0);

  const load = useCallback(async () => {
    setReading(true);

    try {
      const [draft, page, curated, discovered] = await Promise.all([
        readDraft(milestoneId),
        listEvidence(milestoneId),
        listCurated(milestoneId),
        readScript(milestoneId),
      ]);
      setStory(draft?.assetId ? draft : null);

      if (draft?.assetId) {
        const state = await readReview(draft.assetId);
        setStoryApproved(state.reviewStatus === 'approved' && state.approval !== null);
      } else {
        setStoryApproved(false);
      }

      const head = page.items.reduce(
        (newest, bundle) => (bundle.revision > newest.revision ? bundle : newest),
        { revision: 0, items: [] as readonly EvidenceItem[] },
      );
      setEvidenceItems(head.items);
      setMediaBaseUrl(curated.mediaBaseUrl);
      setPictures(
        curated.items
          .filter((item) => item.status === 'selected')
          .map((item) => ({
            mediaId: item.mediaId,
            caption: item.caption,
            viewport: item.viewport,
          })),
      );
      setSummary(discovered);
      setReadFailure(null);
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The video story could not be read.',
      );
    } finally {
      setReading(false);
    }
  }, [milestoneId]);

  useEffect(() => {
    void load();
  }, [load]);

  const addShot = useCallback(() => {
    setShots((current) => {
      const totalMs = duration * 1000;
      const next = current.length + 1;

      if (next > VIDEO_STORY_MAX_SHOTS || Math.floor(totalMs / next) < VIDEO_STORY_MIN_SHOT_MS) {
        return current;
      }

      return [...current, blankShot(pictures, evidenceItems)];
    });
  }, [duration, evidenceItems, pictures]);

  const removeShot = useCallback((key: string) => {
    setShots((current) => current.filter((shot) => shot.key !== key));
  }, []);

  const changeShot = useCallback((key: string, patch: Partial<ComposedShot>) => {
    setShots((current) =>
      current.map((shot) => (shot.key === key ? { ...shot, ...patch } : shot)),
    );
  }, []);

  const changeShotMedia = useCallback(
    (key: string, mediaId: string) => {
      // Choosing another picture re-defaults the citations to the evidence
      // depicting it: the common case needs no checkbox hunt, and every id
      // still comes from the delivered evidence read.
      const depicting = evidenceItems
        .filter((item) => item.mediaId === mediaId)
        .map((item) => item.id);
      setShots((current) =>
        current.map((shot) =>
          shot.key === key
            ? { ...shot, screenshotMediaId: mediaId, sourceEvidenceIds: depicting }
            : shot,
        ),
      );
    },
    [evidenceItems],
  );

  const toggleEvidence = useCallback((key: string, evidenceId: string) => {
    setShots((current) =>
      current.map((shot) => {
        if (shot.key !== key) {
          return shot;
        }

        const cited = shot.sourceEvidenceIds.includes(evidenceId)
          ? shot.sourceEvidenceIds.filter((id) => id !== evidenceId)
          : [...shot.sourceEvidenceIds, evidenceId];
        return { ...shot, sourceEvidenceIds: cited };
      }),
    );
  }, []);

  const generate = useCallback(async () => {
    if (story?.assetId == null || shots.length === 0) {
      return;
    }

    setScripting(true);
    setScriptFailure(null);

    try {
      // The heads this panel read are the ones the script is bound to. All
      // three are re-read here rather than trusted from the last load: when
      // any moved since, the server refuses rather than scripting against
      // provenance nobody saw.
      const [head, voice] = await Promise.all([
        readEvidenceHead(milestoneId),
        readVoiceProfile(projectId),
      ]);

      const timings = deriveTimings(duration, shots.length);

      await generateScript(milestoneId, {
        durationSeconds: duration,
        sourceAssetId: story.assetId,
        evidenceRevision: head.revision,
        voiceRevision: voice.revision,
        shots: shots.map((shot, index) => ({
          screenshotMediaId: shot.screenshotMediaId,
          caption: shot.caption,
          narration: shot.narration,
          transition: shot.transition,
          startMs: timings[index].startMs,
          endMs: timings[index].endMs,
          sourceEvidenceIds: [...shot.sourceEvidenceIds],
        })),
      });

      const discovered = await readScript(milestoneId);
      setSummary(discovered);
      setReloadToken((token) => token + 1);
    } catch (thrown) {
      setScriptFailure(asStoryError(thrown));
    } finally {
      setScripting(false);
    }
  }, [duration, milestoneId, projectId, shots, story]);

  const timings = deriveTimings(duration, Math.max(shots.length, 1));
  const canAdd =
    shots.length < VIDEO_STORY_MAX_SHOTS &&
    Math.floor((duration * 1000) / (shots.length + 1)) >= VIDEO_STORY_MIN_SHOT_MS;

  return (
    <section aria-label="Video storyboard" data-testid="video-story-panel">
      <h3 className="view__subsection" data-testid="video-story-heading">
        Video storyboard
      </h3>
      <p data-testid="video-story-about">
        A reviewed shot list for “{milestoneTitle}”: screenshots in shot order with
        subtitles, spoken lines and handoffs, downloaded as scripts a human acts on.
        Scripts only — no video is synthesized here, no footage is fabricated, and
        nothing is published automatically.
      </p>

      {reading ? <p data-testid="video-story-loading">Reading the video story…</p> : null}
      {readFailure !== null ? (
        <p role="alert" data-testid="video-story-read-failure">
          {readFailure}
        </p>
      ) : null}

      {!reading && readFailure === null ? (
        <>
          {!confirmed ? (
            <p data-testid="video-story-unconfirmed">
              This milestone is not confirmed, so it has no approved story to script from.
              Confirm it first.
            </p>
          ) : story?.assetId == null ? (
            <p data-testid="video-story-no-story">
              No story recorded for this milestone yet, so there is nothing to script a
              storyboard from.
            </p>
          ) : !storyApproved ? (
            <p data-testid="video-story-unapproved-story">
              The story is not approved, so no storyboard can be scripted from it. Approve
              the story first.
            </p>
          ) : pictures.length === 0 ? (
            <p data-testid="video-story-no-screenshots">
              No screenshot is selected for this milestone yet, so no shot can depict one.
              Select a screenshot first — a missing picture is selected or omitted, never
              invented.
            </p>
          ) : archived ? null : (
            <div>
              <label className="form__label">
                Runtime, in seconds
                <select
                  className="form__input"
                  value={duration}
                  onChange={(event) =>
                    setDuration(Number(event.target.value) as VideoStoryDuration)
                  }
                  disabled={scripting}
                  data-testid="video-story-duration"
                >
                  {VIDEO_STORY_DURATIONS.map((candidate) => (
                    <option key={candidate} value={candidate}>
                      {candidate} seconds
                    </option>
                  ))}
                </select>
              </label>

              <ol data-testid="video-story-shots">
                {shots.map((shot, index) => (
                  <li key={shot.key} data-testid={`video-story-shot-${index}`}>
                    <p data-testid={`video-story-shot-${index}-timing`}>
                      Shot {index + 1}: {formatTimestamp(timings[index].startMs)} to{' '}
                      {formatTimestamp(timings[index].endMs)}.
                    </p>
                    <label className="form__label">
                      Depicted screenshot
                      <select
                        className="form__input"
                        value={shot.screenshotMediaId}
                        onChange={(event) => changeShotMedia(shot.key, event.target.value)}
                        disabled={scripting}
                        data-testid={`video-story-shot-${index}-screenshot`}
                      >
                        {pictures.map((picture) => (
                          <option key={picture.mediaId} value={picture.mediaId}>
                            {picture.caption === '' ? picture.viewport : picture.caption} (
                            {picture.mediaId.slice(0, 8)}…)
                          </option>
                        ))}
                      </select>
                    </label>
                    <label className="form__label">
                      Subtitle, 1 to 1000 characters
                      <input
                        type="text"
                        className="form__input"
                        value={shot.caption}
                        onChange={(event) =>
                          changeShot(shot.key, { caption: event.target.value })
                        }
                        disabled={scripting}
                        data-testid={`video-story-shot-${index}-caption`}
                      />
                    </label>
                    <label className="form__label">
                      Spoken line, one line of 1 to 2000 characters
                      <input
                        type="text"
                        className="form__input"
                        value={shot.narration}
                        onChange={(event) =>
                          changeShot(shot.key, { narration: event.target.value })
                        }
                        disabled={scripting}
                        data-testid={`video-story-shot-${index}-narration`}
                      />
                    </label>
                    <label className="form__label">
                      Handoff to the next shot
                      <select
                        className="form__input"
                        value={shot.transition}
                        onChange={(event) =>
                          changeShot(shot.key, {
                            transition: event.target.value as VideoStoryTransition,
                          })
                        }
                        disabled={scripting}
                        data-testid={`video-story-shot-${index}-transition`}
                      >
                        {VIDEO_STORY_TRANSITIONS.map((candidate) => (
                          <option key={candidate} value={candidate}>
                            {candidate}
                          </option>
                        ))}
                      </select>
                    </label>
                    <fieldset data-testid={`video-story-shot-${index}-evidence`}>
                      <legend>Cited evidence, at least one</legend>
                      {evidenceItems.map((item) => (
                        <label key={item.id}>
                          <input
                            type="checkbox"
                            checked={shot.sourceEvidenceIds.includes(item.id)}
                            onChange={() => toggleEvidence(shot.key, item.id)}
                            disabled={scripting}
                            data-testid={`video-story-shot-${index}-evidence-${item.id}`}
                          />
                          {item.kind}: {(item.text === '' ? item.id : item.text).slice(0, 80)}
                        </label>
                      ))}
                    </fieldset>
                    <button
                      type="button"
                      onClick={() => removeShot(shot.key)}
                      disabled={scripting}
                      data-testid={`video-story-shot-${index}-remove`}
                    >
                      Remove this shot
                    </button>
                  </li>
                ))}
              </ol>

              <button
                type="button"
                onClick={addShot}
                disabled={scripting || !canAdd}
                data-testid="video-story-shot-add"
              >
                Add a shot
              </button>
              {!canAdd && shots.length > 0 ? (
                <p data-testid="video-story-shot-limit">
                  The runtime is fully covered: every shot already holds at least{' '}
                  {VIDEO_STORY_MIN_SHOT_MS} milliseconds.
                </p>
              ) : null}

              {shots.length > 0 ? (
                <button
                  type="button"
                  onClick={() => void generate()}
                  disabled={scripting}
                  data-testid="video-story-generate"
                >
                  {scripting ? 'Scripting…' : 'Script this storyboard'}
                </button>
              ) : null}
            </div>
          )}

          {scriptFailure !== null ? (
            <p role="alert" data-testid="video-story-generate-failure">
              {scriptFailure.code}:{' '}
              {scriptFailure.messageFor('durationSeconds') ??
                scriptFailure.messageFor('shots') ??
                scriptFailure.message}
            </p>
          ) : null}

          {summary?.assetId == null ? (
            <p data-testid="video-story-no-script">
              No storyboard scripted yet. Composing needs the approved story and a
              selected screenshot above.
            </p>
          ) : (
            <VideoStoryReviewSection
              key={summary.assetId}
              assetId={summary.assetId}
              mediaBaseUrl={mediaBaseUrl}
              archived={archived}
              reloadToken={reloadToken}
            />
          )}
        </>
      ) : null}
    </section>
  );
}

interface SelectedPicture {
  readonly mediaId: string;
  readonly caption: string;
  readonly viewport: string;
}

let shotSequence = 0;

function blankShot(
  pictures: readonly SelectedPicture[],
  evidenceItems: readonly EvidenceItem[],
): ComposedShot {
  shotSequence += 1;

  const mediaId = pictures[0]?.mediaId ?? '';
  const depicting = evidenceItems
    .filter((item) => item.mediaId === mediaId)
    .map((item) => item.id);

  return {
    key: `shot-${shotSequence}`,
    screenshotMediaId: mediaId,
    caption: '',
    narration: '',
    transition: 'cut',
    sourceEvidenceIds: depicting,
  };
}

function asStoryError(thrown: unknown): VideoStoryApiError {
  if (thrown instanceof VideoStoryApiError) {
    return thrown;
  }

  return new VideoStoryApiError(
    {
      code: 'internal_error',
      message: thrown instanceof Error ? thrown.message : 'The storyboard could not be scripted.',
      traceId: '',
    },
    0,
  );
}
