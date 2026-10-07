import { useCallback, useEffect, useState } from 'react';

import { readDraft } from '../grounded-markdown-drafts/api';
import { readReview } from '../markdown-review-export/api';
import { listVariants } from '../content-asset-variants/api';
import { PackReviewSection } from './PackReviewSection';

/**
 * The Medium import-material panel (CONTENT-PACK-PANELS-002).
 *
 * <b>Medium is export-only, and the panel is shaped like the contract.</b>
 * There is no adapt route and no derive control: the owner picks an approved
 * article, reads its HTML/Markdown and manifest note, and downloads the exact
 * hand-carry bytes. The publish-attempt POST exists so the server can report
 * `publisher_unsupported` without any upstream call instead of a 404 that
 * would send the owner looking for another route. No remote Medium URL is
 * ever shown and no post is ever fabricated.
 */
export function MediumPackPanel({
  milestoneId,
  milestoneTitle,
  archived,
  confirmed,
}: {
  readonly milestoneId: string;
  readonly milestoneTitle: string;
  readonly archived: boolean;
  readonly confirmed: boolean;
}) {
  const [options, setOptions] = useState<readonly MediumOption[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [reading, setReading] = useState(true);
  const [readFailure, setReadFailure] = useState<string | null>(null);

  const load = useCallback(async () => {
    setReading(true);

    try {
      const [draft, variants] = await Promise.all([
        readDraft(milestoneId),
        listVariants(milestoneId),
      ]);

      const candidates: { assetId: string; label: string }[] = [];

      if (draft?.assetId) {
        candidates.push({
          assetId: draft.assetId,
          label: `story (revision ${draft.currentRevision ?? '?'})`,
        });
      }

      for (const variant of variants) {
        candidates.push({
          assetId: variant.assetId,
          label: `${variant.type} (revision ${variant.currentRevision})`,
        });
      }

      const approved: MediumOption[] = [];

      for (const candidate of candidates) {
        try {
          const state = await readReview(candidate.assetId);

          if (state.reviewStatus === 'approved' && state.approval !== null) {
            approved.push(candidate);
          }
        } catch {
          // A review that cannot be read is not an approved article: it is
          // left out of the picker rather than offered and refused later.
        }
      }

      setOptions(approved);
      setSelectedId((current) =>
        current !== null && approved.some((option) => option.assetId === current)
          ? current
          : (approved[0]?.assetId ?? null),
      );
      setReadFailure(null);
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The Medium material could not be read.',
      );
    } finally {
      setReading(false);
    }
  }, [milestoneId]);

  useEffect(() => {
    void load();
  }, [load]);

  if (!confirmed) {
    return null;
  }

  return (
    <section aria-label="Medium import pack" data-testid="medium-pack-panel">
      <h3 className="view__subsection" data-testid="medium-pack-heading">
        Medium import material
      </h3>
      <p data-testid="medium-pack-about">
        Manual import material for “{milestoneTitle}”: the approved article as HTML and Markdown
        with its images and manifest, carried by hand into Medium. This panel is export-only —
        there is no native Medium API to call, no login, and no post leaves from here.
      </p>

      {reading ? <p data-testid="medium-pack-loading">Reading Medium material…</p> : null}
      {readFailure !== null ? (
        <p role="alert" data-testid="medium-pack-read-failure">
          {readFailure}
        </p>
      ) : null}

      {!reading && readFailure === null ? (
        <>
          {options.length === 0 ? (
            <p data-testid="medium-pack-empty">
              No approved article for this milestone yet. Approve a story or a format first, then
              carry it into Medium by hand.
            </p>
          ) : (
            <label className="form__label">
              Approved articles
              <select
                className="form__input"
                value={selectedId ?? ''}
                onChange={(event) =>
                  setSelectedId(event.target.value === '' ? null : event.target.value)
                }
                data-testid="medium-pack-picker"
              >
                {options.map((option) => (
                  <option key={option.assetId} value={option.assetId}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>
          )}

          {selectedId !== null ? (
            <PackReviewSection
              key={selectedId}
              assetId={selectedId}
              format="medium"
              namespace="medium-pack"
              packName="Medium import material"
              archived={archived}
              preview={(asset) => (
                <>
                  <pre data-testid="medium-pack-article-text">{asset.bodyMarkdown ?? ''}</pre>
                  <p data-testid="medium-pack-manifest-note">
                    The download adds the rendered article HTML, the approval-covered pictures and
                    their checksum manifest with hand-carry import instructions.
                  </p>
                </>
              )}
            />
          ) : null}
        </>
      ) : null}
    </section>
  );
}

interface MediumOption {
  readonly assetId: string;
  readonly label: string;
}
