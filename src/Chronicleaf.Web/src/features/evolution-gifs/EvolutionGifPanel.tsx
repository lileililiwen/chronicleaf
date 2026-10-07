import { useEffect, useState } from 'react';

import {
  approveEvolutionGif,
  downloadEvolutionGifUrl,
  listEvolutionGifs,
  previewEvolutionGifStaticUrl,
  previewEvolutionGifUrl,
  renderEvolutionGif,
} from './api';
import { EvolutionGifApiError, type EvolutionGif } from './types';

/**
 * The evolution GIF triggers (FEATURE-TRIGGER-PANELS-004).
 *
 * Three states are visible here and each is shown as what it is. A project with
 * no rendered animation says so rather than playing something that does not
 * exist. A refusal shows the server's own code and message verbatim. Preview
 * and download are links to the routes that serve the bytes - the static
 * preview is the exact first frame playback starts with - and no animation is
 * ever synthesised client-side.
 *
 * Nothing renders except on an explicit owner act, except the initial list
 * read that tells the owner what is already stored.
 */
export function EvolutionGifPanel({ projectId }: { readonly projectId: string }) {
  const [items, setItems] = useState<readonly EvolutionGif[]>([]);
  const [milestoneIds, setMilestoneIds] = useState('');
  const [loop, setLoop] = useState(false);
  const [approveHash, setApproveHash] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<EvolutionGifApiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = async (signal?: AbortSignal) => {
    setItems((await listEvolutionGifs(projectId, signal)).items);
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
      const rendered = await renderEvolutionGif(projectId, { milestoneIds: ids, loop });
      return `An animation of ${rendered.frameCount} frames is rendered at ${rendered.selectionHash.slice(0, 12)}.`;
    });

  const approve = (gif: EvolutionGif) =>
    run(async () => {
      await approveEvolutionGif(gif.id, approveHash.trim());
      return `${gif.id} is approved against the reviewed selection hash.`;
    });

  return (
    <section className="view" aria-label="Evolution GIFs" data-testid="evolution-gif-panel">
      <h3 className="view__heading" data-testid="evolution-gif-heading">
        Evolution GIFs
      </h3>
      <p className="view__hint" data-testid="evolution-gif-hint">
        Animations depict only selected screenshots, in canvas order with the playback the
        review authorized. Nothing here is synthesised.
      </p>

      {loading ? (
        <p className="view__state" data-testid="evolution-gif-loading">
          Reading this project&apos;s animations…
        </p>
      ) : items.length === 0 && failure === null ? (
        <p className="view__state" data-testid="evolution-gif-empty">
          No evolution GIFs. Nothing has been rendered for this project, so there is nothing
          to preview or approve.
        </p>
      ) : null}

      {failure ? (
        <p className="notice notice--error" role="alert" data-testid="evolution-gif-error">
          {failure.message}
        </p>
      ) : null}

      {notice ? (
        <p className="notice notice--ok" role="status" data-testid="evolution-gif-notice">
          {notice}
        </p>
      ) : null}

      {items.length > 0 ? (
        <ul className="milestones" data-testid="evolution-gif-list">
          {items.map((gif) => (
            <li key={gif.id} className="milestone" data-testid="evolution-gif-item">
              <span data-testid="evolution-gif-frames">{gif.frameCount} frames</span>{' '}
              <span data-testid="evolution-gif-status">{gif.status}</span>{' '}
              <span data-testid="evolution-gif-hash">{gif.selectionHash}</span>
              <a href={previewEvolutionGifUrl(gif.id)} data-testid="evolution-gif-preview">
                Preview
              </a>{' '}
              <a href={previewEvolutionGifStaticUrl(gif.id)} data-testid="evolution-gif-preview-static">
                First frame
              </a>{' '}
              <a href={downloadEvolutionGifUrl(gif.id)} data-testid="evolution-gif-download">
                Download
              </a>
              <div className="view__actions">
                <button
                  type="button"
                  className="action action--primary"
                  onClick={() => void approve(gif)}
                  disabled={busy}
                  data-testid="evolution-gif-approve"
                >
                  Approve the reviewed hash
                </button>
              </div>
            </li>
          ))}
        </ul>
      ) : null}

      <h4 className="view__heading">Render an animation</h4>
      <label className="form__label">
        Milestone ids, comma separated
        <input
          type="text"
          className="form__input"
          value={milestoneIds}
          onChange={(event) => setMilestoneIds(event.target.value)}
          data-testid="evolution-gif-milestones"
        />
      </label>
      <label className="form__label">
        <input
          type="checkbox"
          checked={loop}
          onChange={(event) => setLoop(event.target.checked)}
          data-testid="evolution-gif-loop"
        />
        Loop the playback
      </label>
      <label className="form__label">
        Reviewed selection hash, for approval
        <input
          type="text"
          className="form__input"
          value={approveHash}
          onChange={(event) => setApproveHash(event.target.value)}
          data-testid="evolution-gif-approve-hash"
        />
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action action--primary"
          onClick={() => void render()}
          disabled={busy}
          data-testid="evolution-gif-render"
        >
          Render
        </button>
      </div>
    </section>
  );
}

function asFailure(thrown: unknown): EvolutionGifApiError | null {
  return thrown instanceof EvolutionGifApiError ? thrown : null;
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}
