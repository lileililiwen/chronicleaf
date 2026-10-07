import { useEffect, useState } from 'react';

import { previewJourney, publishJourney, readJourney } from './api';
import { JourneyApiError, type JourneyPreview, type JourneyState } from './types';

/**
 * The journey publish triggers (FEATURE-TRIGGER-PANELS-006, private half).
 *
 * Three states are visible here and each is shown as what it is. A project
 * with no journey says so rather than a published page that does not exist. A
 * refusal - unapproved content, a taken slug, a stale selection, a moved
 * revision - shows the server's own code and message verbatim. The public
 * page itself is the anonymous journey view at `/journey/:slug`, which shows
 * only what the projection route returns.
 *
 * Nothing here fires on render except the initial state read that tells the
 * owner whether anything is published: previewing and publishing are explicit
 * owner acts.
 */
export function JourneyPanel({ projectId }: { readonly projectId: string }) {
  const [stored, setStored] = useState<JourneyState | null>(null);
  const [assetIds, setAssetIds] = useState('');
  const [preview, setPreview] = useState<JourneyPreview | null>(null);
  const [slug, setSlug] = useState('');
  const [revision, setRevision] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<JourneyApiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = async (signal?: AbortSignal) => {
    try {
      setStored(await readJourney(projectId, signal));
    } catch (thrown) {
      // A project with no journey has no row, and the route answers 404: that is the
      // empty state, not a failure. Any other refusal is shown verbatim.
      if (thrown instanceof JourneyApiError && thrown.status === 404) {
        setStored(null);
        return;
      }

      throw thrown;
    }
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

  const ids = () =>
    assetIds
      .split(',')
      .map((entry) => entry.trim())
      .filter((entry) => entry.length > 0);

  const previewSelection = () =>
    run(async () => {
      setPreview(await previewJourney(projectId, ids()));
      return null;
    });

  const publish = (enabled: boolean) =>
    run(async () => {
      if (preview === null && enabled) {
        throw new JourneyApiError(
          {
            code: 'preview_required',
            message: 'A publication carries the selection hash its preview computed. Preview first.',
            traceId: '',
          },
          400,
        );
      }

      const trimmedRevision = revision.trim();
      const saved = await publishJourney(projectId, {
        enabled,
        slug: slug.trim(),
        expectedRevision: trimmedRevision.length === 0 ? null : Number(trimmedRevision),
        selectionHash: preview?.selectionHash ?? '',
        approvedAssetIds: ids(),
      });

      return enabled
        ? `The journey is published at /journey/${saved.slug} with ${saved.entryCount} entries.`
        : 'The journey is revoked. The public read answers 404 and no row was deleted.';
    });

  return (
    <section className="view" aria-label="Project journey" data-testid="journey-panel">
      <h3 className="view__heading" data-testid="journey-heading">
        Project journey
      </h3>
      <p className="view__hint" data-testid="journey-hint">
        A preview computes the hash a publication must carry; a publication freezes the
        snapshot only after every approval passes. Unpublished material never renders on the
        public page.
      </p>

      {loading ? (
        <p className="view__state" data-testid="journey-loading">
          Reading this project&apos;s journey…
        </p>
      ) : stored === null && failure === null ? (
        <p className="view__state" data-testid="journey-empty">
          No journey published. Nothing public exists for this project, so there is nothing
          to revoke.
        </p>
      ) : null}

      {failure ? (
        <p className="notice notice--error" role="alert" data-testid="journey-error">
          {failure.message}
        </p>
      ) : null}

      {notice ? (
        <p className="notice notice--ok" role="status" data-testid="journey-notice">
          {notice}
        </p>
      ) : null}

      {stored !== null ? (
        <dl className="session__facts" data-testid="journey-state">
          <dt>Slug</dt>
          <dd data-testid="journey-slug">{stored.slug || '(none)'}</dd>
          <dt>Enabled</dt>
          <dd data-testid="journey-enabled">{String(stored.enabled)}</dd>
          <dt>Revision</dt>
          <dd data-testid="journey-revision">{stored.revision}</dd>
          <dt>Entries</dt>
          <dd data-testid="journey-entries">{stored.entryCount}</dd>
        </dl>
      ) : null}

      <h4 className="view__heading">Preview a selection</h4>
      <label className="form__label">
        Approved asset ids, comma separated
        <input
          type="text"
          className="form__input"
          value={assetIds}
          onChange={(event) => setAssetIds(event.target.value)}
          data-testid="journey-assets-input"
        />
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action"
          onClick={() => void previewSelection()}
          disabled={busy}
          data-testid="journey-preview"
        >
          Preview
        </button>
      </div>

      {preview ? (
        <p className="view__state" data-testid="journey-preview-result">
          The selection hashes to <span data-testid="journey-selection-hash">{preview.selectionHash}</span> over{' '}
          {preview.entries.length} entries.
        </p>
      ) : null}

      <h4 className="view__heading">Publish or revoke</h4>
      <label className="form__label">
        Slug, lowercase kebab case
        <input
          type="text"
          className="form__input"
          value={slug}
          onChange={(event) => setSlug(event.target.value)}
          data-testid="journey-slug-input"
        />
      </label>
      <label className="form__label">
        Expected revision, empty when no journey exists yet
        <input
          type="text"
          className="form__input"
          value={revision}
          onChange={(event) => setRevision(event.target.value)}
          data-testid="journey-revision-input"
        />
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action action--primary"
          onClick={() => void publish(true)}
          disabled={busy}
          data-testid="journey-publish"
        >
          Publish
        </button>
        <button
          type="button"
          className="action action--danger"
          onClick={() => void publish(false)}
          disabled={busy}
          data-testid="journey-revoke"
        >
          Revoke
        </button>
      </div>

      {stored !== null && stored.enabled ? (
        <p className="view__state">
          <a href={`/journey/${stored.slug}`} data-testid="journey-public-link">
            Open the public journey
          </a>
        </p>
      ) : null}
    </section>
  );
}

function asFailure(thrown: unknown): JourneyApiError | null {
  return thrown instanceof JourneyApiError ? thrown : null;
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}
