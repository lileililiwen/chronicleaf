import { useEffect, useState } from 'react';

import { listMilestones } from '../projects-and-milestones/api';
import type { Milestone } from '../projects-and-milestones/types';
import { readDraft } from '../grounded-markdown-drafts/api';
import { listVariants } from '../content-asset-variants/api';
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
  const [milestones, setMilestones] = useState<readonly Milestone[] | null>(null);
  const [milestoneId, setMilestoneId] = useState('');
  const [options, setOptions] = useState<readonly JourneyAssetOption[] | null>(null);
  const [selected, setSelected] = useState<readonly string[]>([]);
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

  useEffect(() => {
    let active = true;
    const controller = new AbortController();

    listMilestones(projectId, 'all', controller.signal)
      .then((page) => {
        if (active) {
          setMilestones(page.items);
          setMilestoneId(page.items[0]?.id ?? '');
        }
      })
      .catch(() => {
        if (active) {
          setMilestones([]);
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, [projectId]);

  useEffect(() => {
    if (milestoneId === '') {
      setOptions([]);
      setSelected([]);
      return;
    }

    let active = true;
    const controller = new AbortController();

    Promise.all([readDraft(milestoneId, controller.signal), listVariants(milestoneId, controller.signal)])
      .then(([draft, variants]) => {
        if (!active) {
          return;
        }

        const built: JourneyAssetOption[] = [];

        if (draft !== null && draft.assetId !== null && draft.currentRevision !== null) {
          built.push({ assetId: draft.assetId, label: `story (revision ${draft.currentRevision})` });
        }

        for (const variant of variants) {
          built.push({
            assetId: variant.assetId,
            label: `${variant.type} (revision ${variant.currentRevision})`,
          });
        }

        setOptions(built);
        // A selection names assets, and assets belong to one milestone: switching the
        // milestone clears the selection rather than carrying ids across it.
        setSelected([]);
      })
      .catch(() => {
        if (active) {
          setOptions([]);
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, [milestoneId]);

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

  const ids = () => [...selected];

  const toggle = (assetId: string) =>
    setSelected((current) =>
      current.includes(assetId)
        ? current.filter((entry) => entry !== assetId)
        : [...current, assetId],
    );

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
      {/* WEB-A11Y-ROLES-001. Project-level panels are h2 sections under the view h1. */}
      <h2 className="view__heading" data-testid="journey-heading">
        Project journey
      </h2>
      <p className="view__hint" data-testid="journey-hint">
        A preview computes the hash a publication must carry; a publication freezes the
        snapshot only after every approval passes. Unpublished material never renders on the
        public page.
      </p>

      {loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="journey-loading">
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

      <h3 className="view__heading">Preview a selection</h3>
      {milestones === null ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="journey-milestones-loading">
          Reading milestones…
        </p>
      ) : milestones.length === 0 ? (
        <p className="view__state" data-testid="journey-milestones-empty">
          No milestone recorded yet, so there is nothing to select.
        </p>
      ) : (
        <label className="form__label">
          Milestone
          <select
            className="form__input"
            value={milestoneId}
            onChange={(event) => setMilestoneId(event.target.value)}
            data-testid="journey-milestone-picker"
          >
            {milestones.map((milestone) => (
              <option key={milestone.id} value={milestone.id}>
                {milestone.title}
              </option>
            ))}
          </select>
        </label>
      )}

      {options === null ? null : options.length === 0 ? (
        <p className="view__state" data-testid="journey-assets-empty">
          This milestone holds no draft and no derived format, so there is nothing to
          select from it.
        </p>
      ) : (
        <fieldset data-testid="journey-asset-options">
          <legend>Approved assets in this milestone</legend>
          {options.map((option) => (
            <label className="form__label" key={option.assetId}>
              <input
                type="checkbox"
                checked={selected.includes(option.assetId)}
                onChange={() => toggle(option.assetId)}
                data-testid="journey-asset-option"
                value={option.assetId}
              />
              {option.label}
            </label>
          ))}
        </fieldset>
      )}
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

      <h3 className="view__heading">Publish or revoke</h3>
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

interface JourneyAssetOption {
  readonly assetId: string;
  readonly label: string;
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}
