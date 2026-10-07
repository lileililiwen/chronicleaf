import { useCallback, useEffect, useState } from 'react';

import { readEvidenceHead } from '../grounded-markdown-drafts/api';
import type { ContentAsset } from '../grounded-markdown-drafts/types';
import { approveAsset, readReview } from '../markdown-review-export/api';
import { readVoiceProfile } from '../voice-profiles/api';
import { ReviewApiError, type ContentReview } from '../markdown-review-export/types';
import { renderSafeMarkdown } from '../markdown-review-export/sanitize';
import { deriveVariant, listVariants, readVariant } from './api';
import { VARIANT_TYPES, VariantApiError, type VariantSummary } from './types';

/**
 * Reusable content formats derived from an approved story (CONTENT-ASSET-VARIANTS-001/002).
 *
 * <b>This panel's subject is a derivation, and the derivation is deterministic.</b> So four
 * things are load-bearing here and none of them is decoration:
 *
 * - <b>The picker names a format; the server renders it from the approved story.</b> No provider
 *   is called, no credential is read and no consent is presented: the body cites only the
 *   footnote ids the source summary assigned, and a section with no source reads unknown rather
 *   than invented.
 * - <b>A variant is a draft with no approval.</b> Approving the story never approves the variant;
 *   each derived asset carries its own approve control, bound to its own revision and body hash
 *   through the review package's client. The panel reports the review state the server derives
 *   rather than inferring one.
 * - <b>The preview never executes.</b> The body is rendered by the review package's safe subset
 *   renderer, which builds React text nodes and http/https anchors only - the same preview an
 *   owner approved the story under.
 * - <b>The readme-update format is a proposal.</b> Its body opens with the notice that no
 *   repository was changed, and this panel offers no control that could change one: the words
 *   are applied by hand or not at all.
 */
export function ContentVariantPanel({
  projectId,
  milestoneId,
  archived,
  confirmed,
}: {
  readonly projectId: string;
  readonly milestoneId: string;
  /** An archived project's variants are kept and cannot be added to, so no derive control is offered. */
  readonly archived: boolean;
  /** A milestone the owner has not confirmed has no approved story, so there is nothing to derive from. */
  readonly confirmed: boolean;
}) {
  const [variants, setVariants] = useState<readonly VariantSummary[]>([]);
  const [selected, setSelected] = useState<ContentAsset | null>(null);
  const [review, setReview] = useState<ContentReview | null>(null);
  const [evidenceRevision, setEvidenceRevision] = useState(0);
  const [format, setFormat] = useState<string>(VARIANT_TYPES[0]);
  const [reading, setReading] = useState(true);
  const [readFailure, setReadFailure] = useState<string | null>(null);
  const [deriving, setDeriving] = useState(false);
  const [deriveFailure, setDeriveFailure] = useState<VariantApiError | null>(null);
  const [approving, setApproving] = useState(false);
  const [approveFailure, setApproveFailure] = useState<ReviewApiError | null>(null);

  const load = useCallback(async () => {
    setReading(true);

    try {
      const [listed, head] = await Promise.all([
        listVariants(milestoneId),
        readEvidenceHead(milestoneId),
      ]);
      setVariants(listed);
      setEvidenceRevision(head.revision);

      if (listed.length > 0) {
        const first = listed[0];
        const [asset, state] = await Promise.all([
          readVariant(first.assetId),
          readReview(first.assetId),
        ]);
        setSelected(asset);
        setReview(state);
      } else {
        setSelected(null);
        setReview(null);
      }

      setReadFailure(null);
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The variants for this milestone could not be read.',
      );
    } finally {
      setReading(false);
    }
  }, [milestoneId, projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  const select = useCallback(async (assetId: string) => {
    try {
      const [asset, state] = await Promise.all([readVariant(assetId), readReview(assetId)]);
      setSelected(asset);
      setReview(state);
    } catch (thrown) {
      setReadFailure(thrown instanceof Error ? thrown.message : 'The variant could not be read.');
    }
  }, []);

  const derive = useCallback(async () => {
    setDeriving(true);
    setDeriveFailure(null);

    try {
      // The heads this panel read are the ones the derivation is bound to. Both are re-read
      // here rather than trusted from the last load: when either moved since, the server
      // refuses rather than rendering against provenance nobody saw.
      const [head, voice] = await Promise.all([
        readEvidenceHead(milestoneId),
        readVoiceProfile(projectId),
      ]);
      setEvidenceRevision(head.revision);

      const derived = await deriveVariant(milestoneId, {
        type: format,
        sourceAssetId: null,
        evidenceRevision: head.revision,
        voiceRevision: voice.revision,
      });
      const [listed, asset, state] = await Promise.all([
        listVariants(milestoneId),
        readVariant(derived.assetId),
        readReview(derived.assetId),
      ]);
      setVariants(listed);
      setSelected(asset);
      setReview(state);
    } catch (thrown) {
      setDeriveFailure(asVariantError(thrown));
    } finally {
      setDeriving(false);
    }
  }, [format, milestoneId, projectId]);

  const approve = useCallback(async () => {
    if (selected?.assetId == null || selected.currentRevision == null) {
      return;
    }

    setApproving(true);
    setApproveFailure(null);

    try {
      await approveAsset(selected.assetId, {
        contentRevision: selected.currentRevision,
        evidenceRevision,
      });
      const state = await readReview(selected.assetId);
      setReview(state);
    } catch (thrown) {
      setApproveFailure(asReviewError(thrown));
    } finally {
      setApproving(false);
    }
  }, [evidenceRevision, selected]);

  if (!confirmed) {
    return null;
  }

  return (
    <section aria-label="Reusable content formats" data-testid="variant-panel">
      <h3 className="view__subsection">Reusable formats</h3>
      {reading ? <p data-testid="variant-loading">Reading variants…</p> : null}
      {readFailure !== null ? (
        <p role="alert" data-testid="variant-read-failure">
          {readFailure}
        </p>
      ) : null}

      {!reading && readFailure === null ? (
        <>
          <p data-testid="variant-count">
            {variants.length === 0
              ? 'No formats derived yet. Deriving needs an approved story first.'
              : `${variants.length} derived format${variants.length === 1 ? '' : 's'}. Each one is a draft until it is approved on its own.`}
          </p>

          {archived ? null : (
            <div>
              <label htmlFor="variant-format">Format</label>
              <select
                id="variant-format"
                data-testid="variant-format"
                value={format}
                onChange={(event) => setFormat(event.target.value)}
                disabled={deriving}
              >
                {VARIANT_TYPES.map((type) => (
                  <option key={type} value={type}>
                    {type}
                  </option>
                ))}
              </select>
              <button
                type="button"
                data-testid="variant-derive"
                onClick={() => void derive()}
                disabled={deriving}
              >
                {deriving ? 'Deriving…' : 'Derive format'}
              </button>
            </div>
          )}

          {deriveFailure !== null ? (
            <p role="alert" data-testid="variant-derive-failure">
              {deriveFailureMessage(deriveFailure)}
            </p>
          ) : null}

          {variants.length > 0 ? (
            <ul data-testid="variant-list">
              {variants.map((variant) => (
                <li key={variant.assetId}>
                  <button
                    type="button"
                    data-testid={`variant-select-${variant.type}`}
                    onClick={() => void select(variant.assetId)}
                  >
                    {variant.type} (revision {variant.currentRevision})
                  </button>
                </li>
              ))}
            </ul>
          ) : null}

          {selected?.bodyMarkdown != null ? (
            <article data-testid="variant-preview">
              <h4 className="view__subsection" data-testid="variant-preview-type">{selected.type}</h4>
              <p data-testid="variant-preview-revision">Revision {selected.currentRevision}</p>
              <p data-testid="variant-preview-status">
                {review === null ? 'draft' : review.reviewStatus}
              </p>
              {selected.type === 'readme-update' ? (
                <p data-testid="variant-proposal-notice">
                  A proposal only: no repository was changed. Apply these words by hand.
                </p>
              ) : null}
              <div data-testid="variant-preview-body">{renderSafeMarkdown(selected.bodyMarkdown)}</div>
              {archived ? null : (
                <div>
                  <button
                    type="button"
                    data-testid="variant-approve"
                    onClick={() => void approve()}
                    disabled={approving}
                  >
                    {approving ? 'Approving…' : 'Approve this format'}
                  </button>
                  {approveFailure !== null ? (
                    <p role="alert" data-testid="variant-approve-failure">
                      {approveFailure.message}
                    </p>
                  ) : null}
                </div>
              )}
            </article>
          ) : null}
        </>
      ) : null}
    </section>
  );
}

function deriveFailureMessage(failure: VariantApiError): string {
  if (failure.isApprovalRequired) {
    return 'The story was never approved. Approve it first, then derive a format from it.';
  }

  if (failure.isApprovalStale) {
    return 'The story or its evidence moved since it was approved. Re-read it and approve again first.';
  }

  if (failure.isRevisionConflict) {
    return 'The evidence or voice moved since this panel read it. The panel re-reads on its own; try again.';
  }

  return failure.messageFor('type') ?? failure.message;
}

function asVariantError(thrown: unknown): VariantApiError {
  if (thrown instanceof VariantApiError) {
    return thrown;
  }

  return new VariantApiError(
    {
      code: 'internal_error',
      message: thrown instanceof Error ? thrown.message : 'The format could not be derived.',
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
      message: thrown instanceof Error ? thrown.message : 'The format could not be approved.',
      traceId: '',
    },
    0,
  );
}
