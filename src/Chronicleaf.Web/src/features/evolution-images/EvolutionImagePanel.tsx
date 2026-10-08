import { useEffect, useState } from 'react';

import {
  approveEvolutionImage,
  downloadEvolutionImageUrl,
  listEvolutionImages,
  previewEvolutionImageUrl,
  renderEvolutionImage,
} from './api';
import { EvolutionImageApiError, type EvolutionImage } from './types';

/**
 * The evolution image triggers (FEATURE-TRIGGER-PANELS-003).
 *
 * Three states are visible here and each is shown as what it is. A project with
 * no rendered comparison says so rather than drawing a picture that does not
 * exist. A refusal shows the server's own code and message verbatim. Preview
 * and download are links to the routes that serve the bytes, never synthesised
 * pictures, and nothing here promotes a candidate: curation owns that.
 *
 * Nothing renders except on an explicit owner act, except the initial list
 * read that tells the owner what is already stored.
 */
export function EvolutionImagePanel({ projectId }: { readonly projectId: string }) {
  const [items, setItems] = useState<readonly EvolutionImage[]>([]);
  const [milestoneIds, setMilestoneIds] = useState('');
  const [layout, setLayout] = useState('before-after');
  const [approveHash, setApproveHash] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<EvolutionImageApiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = async (signal?: AbortSignal) => {
    setItems((await listEvolutionImages(projectId, signal)).items);
  };

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    load(controller.signal)
      .catch((thrown: unknown) => {
        if (active && !isAbort(thrown)) {
          setFailure(asFailure(thrown));
        }
      })
      .finally(() => {
        if (active) {
          setLoading(false);
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  const run = async (work: () => Promise<string | null>) => {
    setBusy(true);
    setFailure(null);
    setNotice(null);

    try {
      const message = await work();
      setNotice(message);
      await load();
    } catch (thrown) {
      setFailure(asFailure(thrown));
    } finally {
      setBusy(false);
    }
  };

  const render = () =>
    run(async () => {
      const ids = milestoneIds
        .split(',')
        .map((entry) => entry.trim())
        .filter((entry) => entry.length > 0);
      const rendered = await renderEvolutionImage(projectId, { milestoneIds: ids, layout });
      return `A ${rendered.layout} comparison is rendered at revision ${rendered.selectionHash.slice(0, 12)}.`;
    });

  const approve = (evolution: EvolutionImage) =>
    run(async () => {
      await approveEvolutionImage(evolution.id, approveHash.trim());
      return `${evolution.id} is approved against the reviewed selection hash.`;
    });

  return (
    <section className="view" aria-label="Evolution images" data-testid="evolution-image-panel">
      <h3 className="view__heading" data-testid="evolution-image-heading">
        Evolution images
      </h3>
      <p className="view__hint" data-testid="evolution-image-hint">
        Comparisons depict only selected screenshots. Candidates are never compared here, and
        nothing here promotes one.
      </p>

      {loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="evolution-image-loading">
          Reading this project&apos;s comparisons…
        </p>
      ) : items.length === 0 && failure === null ? (
        <p className="view__state" data-testid="evolution-image-empty">
          No evolution images. Nothing has been rendered for this project, so there is nothing
          to preview or approve.
        </p>
      ) : null}

      {failure ? (
        <p className="notice notice--error" role="alert" data-testid="evolution-image-error">
          {failure.message}
        </p>
      ) : null}

      {notice ? (
        <p className="notice notice--ok" role="status" data-testid="evolution-image-notice">
          {notice}
        </p>
      ) : null}

      {items.length > 0 ? (
        <ul className="milestones" data-testid="evolution-image-list">
          {items.map((evolution) => (
            <li key={evolution.id} className="milestone" data-testid="evolution-image-item">
              <span data-testid="evolution-image-layout">{evolution.layout}</span>{' '}
              <span data-testid="evolution-image-status">{evolution.status}</span>{' '}
              <span data-testid="evolution-image-hash">{evolution.selectionHash}</span>
              <a href={previewEvolutionImageUrl(evolution.id)} data-testid="evolution-image-preview">
                Preview
              </a>{' '}
              <a href={downloadEvolutionImageUrl(evolution.id)} data-testid="evolution-image-download">
                Download
              </a>
              <div className="view__actions">
                <button
                  type="button"
                  className="action action--primary"
                  onClick={() => void approve(evolution)}
                  disabled={busy}
                  data-testid="evolution-image-approve"
                >
                  Approve the reviewed hash
                </button>
              </div>
            </li>
          ))}
        </ul>
      ) : null}

      <h4 className="view__heading">Render a comparison</h4>
      <label className="form__label">
        Milestone ids, comma separated
        <input
          type="text"
          className="form__input"
          value={milestoneIds}
          onChange={(event) => setMilestoneIds(event.target.value)}
          data-testid="evolution-image-milestones"
        />
      </label>
      <label className="form__label">
        Layout
        <select
          className="form__input"
          value={layout}
          onChange={(event) => setLayout(event.target.value)}
          data-testid="evolution-image-layout-input"
        >
          <option value="before-after">before-after</option>
          <option value="horizontal-timeline">horizontal-timeline</option>
          <option value="vertical-long">vertical-long</option>
          <option value="gallery">gallery</option>
        </select>
      </label>
      <label className="form__label">
        Reviewed selection hash, for approval
        <input
          type="text"
          className="form__input"
          value={approveHash}
          onChange={(event) => setApproveHash(event.target.value)}
          data-testid="evolution-image-approve-hash"
        />
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action action--primary"
          onClick={() => void render()}
          disabled={busy}
          data-testid="evolution-image-render"
        >
          Render
        </button>
      </div>
    </section>
  );
}

function asFailure(thrown: unknown): EvolutionImageApiError | null {
  return thrown instanceof EvolutionImageApiError ? thrown : null;
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}
