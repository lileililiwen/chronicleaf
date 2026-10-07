import { useCallback, useEffect, useState } from 'react';

import { readDraft, readEvidenceHead } from '../grounded-markdown-drafts/api';
import type { ContentAsset } from '../grounded-markdown-drafts/types';
import { readReview } from '../markdown-review-export/api';
import { listPacks } from '../content-asset-variants/api';
import type { VariantSummary } from '../content-asset-variants/types';
import { readVoiceProfile } from '../voice-profiles/api';
import { adaptXiaohongshu } from './api';
import { XIAOHONGSHU_PACK_TYPE, PackApiError } from './types';
import { PackReviewSection } from './PackReviewSection';

/**
 * The Xiaohongshu pack read/export panel (CONTENT-PACK-PANELS-003).
 *
 * <b>The pack is assembled for a human to post elsewhere — never posted
 * from here.</b> The derive POST re-renders the approved story through the
 * delivered template under the owner's chosen title, tags, cover and call
 * to action; the preview shows the stored pack as characters with its image
 * order and voice; and the download hands the server's exact bytes to a
 * file. There is no account, no login, no cookie and no transport behind
 * any control here, and export needs a live approval — an edit after
 * approval invalidates it, exactly as the contract requires.
 */
export function XiaohongshuPackPanel({
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
  const [candidates, setCandidates] = useState<readonly string[]>([]);
  const [imageOrder, setImageOrder] = useState<readonly string[]>([]);
  const [title, setTitle] = useState('');
  const [tags, setTags] = useState('');
  const [coverCopy, setCoverCopy] = useState('');
  const [callToAction, setCallToAction] = useState('');
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

      const derived = variants.filter((variant) => variant.type === XIAOHONGSHU_PACK_TYPE);
      setPacks(derived);
      setSelectedId((current) =>
        current !== null && derived.some((pack) => pack.assetId === current)
          ? current
          : (derived[0]?.assetId ?? null),
      );
      setReadFailure(null);
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The Xiaohongshu packs could not be read.',
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

      const tagList = tags
        .split(/[\s,]+/)
        .map((tag) => tag.trim())
        .filter((tag) => tag.length > 0);

      const adapted = await adaptXiaohongshu(story.assetId, {
        evidenceRevision: head.revision,
        voiceRevision: voice.revision,
        title: title.trim() === '' ? milestoneTitle : title.trim(),
        tags: tagList,
        coverCopy: coverCopy.trim() === '' ? null : coverCopy.trim(),
        callToAction: callToAction.trim() === '' ? null : callToAction.trim(),
      });

      const listed = await listPacks(milestoneId);
      const derived = listed.filter((variant) => variant.type === XIAOHONGSHU_PACK_TYPE);
      setPacks(derived);
      setSelectedId(adapted.assetId);
      setCandidates(adapted.titleCandidates);
      setImageOrder(adapted.imageOrder);
    } catch (thrown) {
      setDeriveFailure(asPackError(thrown));
    } finally {
      setDeriving(false);
    }
  }, [callToAction, coverCopy, milestoneId, milestoneTitle, projectId, story, tags, title]);

  if (!confirmed) {
    return null;
  }

  return (
    <section aria-label="Xiaohongshu content pack" data-testid="xiaohongshu-pack-panel">
      <h3 className="view__subsection" data-testid="xiaohongshu-pack-heading">
        Xiaohongshu pack
      </h3>
      <p data-testid="xiaohongshu-pack-about">
        Titles, body, image order, cover, tags and call to action for “{milestoneTitle}”, packed
        for a human to publish externally. No account is logged in here and nothing is posted
        automatically; export needs a live approval, and an edit after approval needs a new one.
      </p>

      {reading ? <p data-testid="xiaohongshu-pack-loading">Reading Xiaohongshu packs…</p> : null}
      {readFailure !== null ? (
        <p role="alert" data-testid="xiaohongshu-pack-read-failure">
          {readFailure}
        </p>
      ) : null}

      {!reading && readFailure === null ? (
        <>
          {story?.assetId == null ? (
            <p data-testid="xiaohongshu-pack-no-story">
              No story recorded for this milestone yet, so there is nothing to pack from.
            </p>
          ) : !storyApproved ? (
            <p data-testid="xiaohongshu-pack-unapproved-story">
              The story is not approved, so no pack can be derived from it. Approve the story first.
            </p>
          ) : archived ? null : (
            <div>
              <label className="form__label">
                Title — the milestone title is the first derived candidate
                <input
                  type="text"
                  className="form__input"
                  value={title}
                  placeholder={milestoneTitle}
                  onChange={(event) => setTitle(event.target.value)}
                  data-testid="xiaohongshu-pack-title"
                  disabled={deriving}
                />
              </label>
              <label className="form__label">
                Tags, separated by spaces or commas
                <input
                  type="text"
                  className="form__input"
                  value={tags}
                  onChange={(event) => setTags(event.target.value)}
                  data-testid="xiaohongshu-pack-tags"
                  disabled={deriving}
                />
              </label>
              <label className="form__label">
                Cover copy, empty for none
                <input
                  type="text"
                  className="form__input"
                  value={coverCopy}
                  onChange={(event) => setCoverCopy(event.target.value)}
                  data-testid="xiaohongshu-pack-cover"
                  disabled={deriving}
                />
              </label>
              <label className="form__label">
                Call to action, empty for none
                <input
                  type="text"
                  className="form__input"
                  value={callToAction}
                  onChange={(event) => setCallToAction(event.target.value)}
                  data-testid="xiaohongshu-pack-cta"
                  disabled={deriving}
                />
              </label>
              <button
                type="button"
                onClick={() => void derive()}
                disabled={deriving}
                data-testid="xiaohongshu-pack-derive"
              >
                {deriving ? 'Deriving…' : 'Derive Xiaohongshu pack'}
              </button>
            </div>
          )}

          {deriveFailure !== null ? (
            <p role="alert" data-testid="xiaohongshu-pack-derive-failure">
              {deriveFailure.code}: {deriveFailure.message}
            </p>
          ) : null}

          {packs.length === 0 ? (
            <p data-testid="xiaohongshu-pack-empty">
              No Xiaohongshu pack derived yet. Deriving needs the approved story above.
            </p>
          ) : (
            <label className="form__label">
              Derived packs
              <select
                className="form__input"
                value={selectedId ?? ''}
                onChange={(event) => {
                  setSelectedId(event.target.value === '' ? null : event.target.value);
                  setCandidates([]);
                  setImageOrder([]);
                }}
                data-testid="xiaohongshu-pack-picker"
              >
                {packs.map((pack) => (
                  <option key={pack.assetId} value={pack.assetId}>
                    pack (revision {pack.currentRevision})
                  </option>
                ))}
              </select>
            </label>
          )}

          {selectedId !== null ? (
            <PackReviewSection
              key={selectedId}
              assetId={selectedId}
              format="xiaohongshu"
              namespace="xiaohongshu-pack"
              packName="Xiaohongshu pack"
              archived={archived}
              preview={(asset) => (
                <>
                  <pre data-testid="xiaohongshu-pack-body-text">{asset.bodyMarkdown ?? ''}</pre>
                  {candidates.length > 0 ? (
                    <p data-testid="xiaohongshu-pack-candidates">
                      Derived titles: {candidates.join(' · ')}.
                    </p>
                  ) : null}
                  {imageOrder.length > 0 ? (
                    <p data-testid="xiaohongshu-pack-order">
                      Image order: {imageOrder.join(', ')}.
                    </p>
                  ) : (
                    <p data-testid="xiaohongshu-pack-order-note">
                      Image order and checksums, if any, are listed in the export manifest.
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
      message: thrown instanceof Error ? thrown.message : 'The pack could not be derived.',
      traceId: '',
    },
    0,
  );
}
