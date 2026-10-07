import { useCallback, useEffect, useState } from 'react';

import { readDraft, readEvidenceHead } from '../grounded-markdown-drafts/api';
import type { ContentAsset } from '../grounded-markdown-drafts/types';
import { readReview } from '../markdown-review-export/api';
import { listPacks } from '../content-asset-variants/api';
import type { VariantSummary } from '../content-asset-variants/types';
import { readVoiceProfile } from '../voice-profiles/api';
import { adaptLinkedIn } from './api';
import { LINKEDIN_PACK_TYPE, PackApiError } from './types';
import { PackReviewSection } from './PackReviewSection';

/**
 * The LinkedIn copy read/export panel (CONTENT-PACK-PANELS-001).
 *
 * <b>The copy is derived, reviewed and carried by hand — never sent.</b> The
 * derive POST re-renders the approved story through the delivered template,
 * the preview shows the stored text as characters with the owned media and
 * the voice it was written under, and the download hands the server's exact
 * bytes to a file. The publish-attempt POST exists so the server can answer
 * `publisher_unsupported` in its own words instead of a 404 that would send
 * the owner looking for another route.
 */
export function LinkedInPackPanel({
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
  const [packs, setPacks] = useState<readonly VariantSummary[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [mediaIds, setMediaIds] = useState<readonly string[]>([]);
  const [callToAction, setCallToAction] = useState('');
  const [hashtags, setHashtags] = useState('');
  const [reading, setReading] = useState(true);
  const [readFailure, setReadFailure] = useState<string | null>(null);
  const [deriving, setDeriving] = useState(false);
  const [deriveFailure, setDeriveFailure] = useState<PackApiError | null>(null);

  const load = useCallback(async () => {
    setReading(true);

    try {
      const [draft, variants] = await Promise.all([
        readDraft(milestoneId),
        listPacks(milestoneId),
      ]);
      setStory(draft?.assetId ? draft : null);

      if (draft?.assetId) {
        const state = await readReview(draft.assetId);
        setStoryApproved(state.reviewStatus === 'approved' && state.approval !== null);
      } else {
        setStoryApproved(false);
      }

      const copies = variants.filter((variant) => variant.type === LINKEDIN_PACK_TYPE);
      setPacks(copies);
      setSelectedId((current) =>
        current !== null && copies.some((pack) => pack.assetId === current)
          ? current
          : (copies[0]?.assetId ?? null),
      );
      setReadFailure(null);
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The LinkedIn copies could not be read.',
      );
    } finally {
      setReading(false);
    }
  }, [milestoneId]);

  useEffect(() => {
    void load();
  }, [load]);

  const derive = useCallback(async () => {
    if (story?.assetId == null) {
      return;
    }

    setDeriving(true);
    setDeriveFailure(null);

    try {
      // The heads this panel read are the ones the derivation is bound to.
      // Both are re-read here rather than trusted from the last load: when
      // either moved since, the server refuses rather than rendering against
      // provenance nobody saw.
      const [head, voice] = await Promise.all([
        readEvidenceHead(milestoneId),
        readVoiceProfile(projectId),
      ]);

      const tags = hashtags
        .split(/[\s,]+/)
        .map((tag) => tag.trim())
        .filter((tag) => tag.length > 0);

      const adapted = await adaptLinkedIn(story.assetId, {
        evidenceRevision: head.revision,
        voiceRevision: voice.revision,
        callToAction: callToAction.trim() === '' ? null : callToAction.trim(),
        hashtags: tags,
      });

      const listed = await listPacks(milestoneId);
      const copies = listed.filter((variant) => variant.type === LINKEDIN_PACK_TYPE);
      setPacks(copies);
      setSelectedId(adapted.assetId);
      setMediaIds(adapted.selectedMediaIds);
    } catch (thrown) {
      setDeriveFailure(asPackError(thrown));
    } finally {
      setDeriving(false);
    }
  }, [callToAction, hashtags, milestoneId, projectId, story]);

  if (!confirmed) {
    return null;
  }

  return (
    <section aria-label="LinkedIn copy pack" data-testid="linkedin-pack-panel">
      <h3 className="view__subsection" data-testid="linkedin-pack-heading">
        LinkedIn copy
      </h3>
      <p data-testid="linkedin-pack-about">
        Copy-ready text and owned media, derived from the approved story for “{milestoneTitle}”.
        Nothing here is sent anywhere: the output is carried by hand, and a native publish
        attempt is refused by the server rather than performed.
      </p>

      {reading ? <p data-testid="linkedin-pack-loading">Reading LinkedIn copies…</p> : null}
      {readFailure !== null ? (
        <p role="alert" data-testid="linkedin-pack-read-failure">
          {readFailure}
        </p>
      ) : null}

      {!reading && readFailure === null ? (
        <>
          {story?.assetId == null ? (
            <p data-testid="linkedin-pack-no-story">
              No story recorded for this milestone yet, so there is nothing to derive a copy from.
            </p>
          ) : !storyApproved ? (
            <p data-testid="linkedin-pack-unapproved-story">
              The story is not approved, so no copy can be derived from it. Approve the story first.
            </p>
          ) : archived ? null : (
            <div>
              <label className="form__label">
                Call to action, empty for none
                <input
                  type="text"
                  className="form__input"
                  value={callToAction}
                  onChange={(event) => setCallToAction(event.target.value)}
                  data-testid="linkedin-pack-cta"
                  disabled={deriving}
                />
              </label>
              <label className="form__label">
                Hashtags, separated by spaces or commas
                <input
                  type="text"
                  className="form__input"
                  value={hashtags}
                  onChange={(event) => setHashtags(event.target.value)}
                  data-testid="linkedin-pack-hashtags"
                  disabled={deriving}
                />
              </label>
              <button
                type="button"
                onClick={() => void derive()}
                disabled={deriving}
                data-testid="linkedin-pack-derive"
              >
                {deriving ? 'Deriving…' : 'Derive LinkedIn copy'}
              </button>
            </div>
          )}

          {deriveFailure !== null ? (
            <p role="alert" data-testid="linkedin-pack-derive-failure">
              {deriveFailure.code}: {deriveFailure.message}
            </p>
          ) : null}

          {packs.length === 0 ? (
            <p data-testid="linkedin-pack-empty">
              No LinkedIn copy derived yet. Deriving needs the approved story above.
            </p>
          ) : (
            <label className="form__label">
              Derived copies
              <select
                className="form__input"
                value={selectedId ?? ''}
                onChange={(event) => {
                  setSelectedId(event.target.value === '' ? null : event.target.value);
                  setMediaIds([]);
                }}
                data-testid="linkedin-pack-picker"
              >
                {packs.map((pack) => (
                  <option key={pack.assetId} value={pack.assetId}>
                    copy (revision {pack.currentRevision})
                  </option>
                ))}
              </select>
            </label>
          )}

          {selectedId !== null ? (
            <PackReviewSection
              key={selectedId}
              assetId={selectedId}
              format="linkedin"
              namespace="linkedin-pack"
              packName="LinkedIn copy"
              archived={archived}
              preview={(asset) => (
                <>
                  <pre data-testid="linkedin-pack-copy-text">{asset.bodyMarkdown ?? ''}</pre>
                  {mediaIds.length > 0 ? (
                    <p data-testid="linkedin-pack-media">
                      Owned media: {mediaIds.join(', ')}.
                    </p>
                  ) : (
                    <p data-testid="linkedin-pack-media-note">
                      Owned media, if any, are listed in the export manifest.
                    </p>
                  )}
                </>
              )}
            />
          ) : null}
        </>
      ) : null}
    </section>
  );
}

function asPackError(thrown: unknown): PackApiError {
  if (thrown instanceof PackApiError) {
    return thrown;
  }

  return new PackApiError(
    {
      code: 'internal_error',
      message: thrown instanceof Error ? thrown.message : 'The copy could not be derived.',
      traceId: '',
    },
    0,
  );
}
