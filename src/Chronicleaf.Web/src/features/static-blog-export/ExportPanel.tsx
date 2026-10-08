import { useEffect, useState } from 'react';

import { listMilestones } from '../projects-and-milestones/api';
import type { Milestone } from '../projects-and-milestones/types';
import { readDraft } from '../grounded-markdown-drafts/api';
import { listVariants } from '../content-asset-variants/api';
import { readReview } from '../markdown-review-export/api';
import {
  downloadStaticBlogExport,
  newIdempotencyKey,
  requestStaticBlogExport,
  readStaticBlogExport,
} from './api';
import { StaticBlogApiError, type StaticBlogExportStatus } from './types';

/**
 * The static blog export triggers (STATIC-BLOG-EXPORT-UI-001/002).
 *
 * Three states are visible here and each is shown as what it is. A project with no
 * milestone says so rather than an export that was never asked for. A selection whose
 * assets carry no live approval is shown as excluded in the server's own status word,
 * and a refused export shows the server's own code and message verbatim. The portable
 * ZIP is offered only when the status route answers `succeeded`: any other terminal
 * state names its error code and offers no download.
 *
 * Nothing here fires on render except the reads that fill the pickers: the export
 * POST leaves only from the Export button, and the download leaves only from the
 * download control the ready state reveals. There is no progress bar and no
 * percentage anywhere: the job's status word, counts and digest are the server's,
 * and this panel reports them rather than inventing finer ones.
 */
export function ExportPanel({ projectId }: { readonly projectId: string }) {
  const [milestones, setMilestones] = useState<readonly Milestone[] | null>(null);
  const [milestoneId, setMilestoneId] = useState('');
  const [options, setOptions] = useState<readonly ExportAssetOption[] | null>(null);
  const [reviews, setReviews] = useState<Readonly<Record<string, string>>>({});
  const [selected, setSelected] = useState<readonly string[]>([]);
  const [basePath, setBasePath] = useState('');
  const [watched, setWatched] = useState<StaticBlogExportStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const [failure, setFailure] = useState<StaticBlogApiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    const controller = new AbortController();

    listMilestones(projectId, 'all', controller.signal)
      .then((page) => {
        if (!active) {
          return;
        }

        setMilestones(page.items);
        setMilestoneId((current) =>
          current !== '' && page.items.some((milestone) => milestone.id === current)
            ? current
            : (page.items[0]?.id ?? ''),
        );
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
      setReviews({});
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

        const built: ExportAssetOption[] = [];

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
        setReviews({});

        for (const option of built) {
          readReview(option.assetId, controller.signal)
            .then((state) => {
              if (active) {
                setReviews((current) => ({ ...current, [option.assetId]: state.reviewStatus }));
              }
            })
            .catch(() => {
              if (active) {
                setReviews((current) => ({ ...current, [option.assetId]: 'unknown' }));
              }
            });
        }
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

  const toggle = (assetId: string) =>
    setSelected((current) =>
      current.includes(assetId)
        ? current.filter((entry) => entry !== assetId)
        : [...current, assetId],
    );

  const included = selected.filter((assetId) => reviews[assetId] === 'approved');
  const excluded = selected.filter((assetId) => reviews[assetId] !== 'approved');

  const watch = async (operationId: string) => {
    // The job builds on the delivered queue; this loop reports its status word rather than
    // deciding anything. A terminal word ends the watch; anything else is re-read.
    for (let attempt = 0; attempt < 120; attempt += 1) {
      await sleep(1000);
      const state = await readStaticBlogExport(projectId, operationId);

      if (isTerminal(state.status)) {
        return state;
      }

      setWatched(state);
    }

    return readStaticBlogExport(projectId, operationId);
  };

  const exportSelection = () =>
    run(async () => {
      const accepted = await requestStaticBlogExport(projectId, {
        approvedAssetIds: [...selected],
        basePath: basePath.trim(),
        idempotencyKey: newIdempotencyKey(),
      });

      const terminal = await watch(accepted.operationId);
      setWatched(terminal);

      if (terminal.status !== 'succeeded') {
        throw new StaticBlogApiError(
          {
            code: terminal.errorCode ?? 'static_blog_superseded',
            message: terminal.errorCode
              ? `The export job ended as ${terminal.status} with code ${terminal.errorCode}.`
              : `The export job ended as ${terminal.status}.`,
            traceId: '',
          },
          409,
        );
      }

      return accepted.created
        ? `The export built ${terminal.postCount ?? 0} posts as operation ${terminal.operationId}.`
        : `The identical request had already built operation ${terminal.operationId}.`;
    });

  const download = () =>
    run(async () => {
      if (watched === null || watched.status !== 'succeeded') {
        throw new StaticBlogApiError(
          {
            code: 'approval_stale',
            message: 'No completed archive exists for this job, so there is nothing to download.',
            traceId: '',
          },
          409,
        );
      }

      setDownloading(true);

      try {
        const { fileName, bytes } = await downloadStaticBlogExport(
          projectId,
          watched.operationId,
        );
        const url = URL.createObjectURL(bytes);
        try {
          const anchor = document.createElement('a');
          anchor.href = url;
          anchor.download = fileName;
          document.body.appendChild(anchor);
          anchor.click();
          anchor.remove();
        } finally {
          // The bytes are the server's attachment, handed to a file: revoking here would
          // race the click above on some browsers, so the object URL lives as long as the
          // page rather than being cut out from under the download.
        }

        return null;
      } finally {
        setDownloading(false);
      }
    });

  const run = async (work: () => Promise<string | null>) => {
    setBusy(true);
    setFailure(null);
    setNotice(null);

    try {
      setNotice(await work());
    } catch (thrown) {
      setFailure(thrown instanceof StaticBlogApiError ? thrown : null);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="view" aria-label="Static blog export" data-testid="static-blog-export-panel">
      <h3 className="view__heading" data-testid="static-blog-export-heading">
        Static blog export
      </h3>
      <p className="view__hint" data-testid="static-blog-export-hint">
        An export depicts only the approved assets it names, as a portable site with an index,
        one page per post and a checksum manifest. Nothing here exports on render: the
        request leaves from the Export button and the download appears only when the
        server reports the job ready.
      </p>

      {notice ? (
        <p className="notice notice--ok" role="status" data-testid="static-blog-export-notice">
          {notice}
        </p>
      ) : null}

      {failure ? (
        <p className="notice notice--error" role="alert" data-testid="static-blog-export-error">
          {failure.code}: {failure.message}
        </p>
      ) : null}

      <h4 className="view__heading">Select approved assets</h4>
      {milestones === null ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="static-blog-export-milestones-loading">
          Reading milestones…
        </p>
      ) : milestones.length === 0 ? (
        <p className="view__state" data-testid="static-blog-export-milestones-empty">
          No milestone recorded yet, so there is nothing to export.
        </p>
      ) : (
        <label className="form__label">
          Milestone
          <select
            className="form__input"
            value={milestoneId}
            onChange={(event) => setMilestoneId(event.target.value)}
            data-testid="static-blog-export-milestone-picker"
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
        <p className="view__state" data-testid="static-blog-export-assets-empty">
          This milestone holds no draft and no derived format, so there is nothing to
          select from it.
        </p>
      ) : (
        <fieldset data-testid="static-blog-export-asset-options">
          <legend>Approved assets in this milestone</legend>
          {options.map((option) => (
            <label className="form__label" key={option.assetId}>
              <input
                type="checkbox"
                checked={selected.includes(option.assetId)}
                onChange={() => toggle(option.assetId)}
                data-testid="static-blog-export-asset-option"
                value={option.assetId}
              />
              {option.label}
              {reviews[option.assetId] === undefined ? null : reviews[option.assetId] === 'approved' ? (
                <span data-testid="static-blog-export-included">included</span>
              ) : (
                <span data-testid="static-blog-export-excluded">
                  excluded ({reviews[option.assetId]})
                </span>
              )}
            </label>
          ))}
        </fieldset>
      )}

      {selected.length === 0 ? null : (
        <p className="view__state" data-testid="static-blog-export-preview">
          {included.length} included{excluded.length === 0 ? '' : `, ${excluded.length} excluded`}:
          the server decides what builds, and anything without a live approval is refused
          by name.
        </p>
      )}

      <h4 className="view__heading">Export</h4>
      <label className="form__label">
        Base path, empty for the site root
        <input
          type="text"
          className="form__input"
          value={basePath}
          onChange={(event) => setBasePath(event.target.value)}
          placeholder="/journey"
          data-testid="static-blog-export-basepath-input"
        />
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action action--primary"
          onClick={() => void exportSelection()}
          disabled={busy || selected.length === 0}
          data-testid="static-blog-export-submit"
        >
          Export
        </button>
      </div>

      {watched === null ? (
        <p className="view__state" data-testid="static-blog-export-no-job">
          No export requested. The job this button starts is the only thing this panel
          watches.
        </p>
      ) : (
        <dl className="session__facts" data-testid="static-blog-export-status">
          <dt>State</dt>
          <dd data-testid="static-blog-export-state">{watched.status}</dd>
          <dt>Operation</dt>
          <dd data-testid="static-blog-export-operation">{watched.operationId}</dd>
          {watched.errorCode ? (
            <>
              <dt>Error</dt>
              <dd data-testid="static-blog-export-error-code">{watched.errorCode}</dd>
            </>
          ) : null}
          {watched.contentSha256 ? (
            <>
              <dt>Digest</dt>
              <dd data-testid="static-blog-export-digest">{watched.contentSha256}</dd>
            </>
          ) : null}
          {watched.postCount !== null && watched.postCount !== undefined ? (
            <>
              <dt>Posts</dt>
              <dd data-testid="static-blog-export-posts">{watched.postCount}</dd>
              <dt>Images</dt>
              <dd data-testid="static-blog-export-images">{watched.imageCount ?? 0}</dd>
            </>
          ) : null}
        </dl>
      )}

      {watched !== null && watched.status === 'succeeded' ? (
        <div className="view__actions">
          <button
            type="button"
            className="action"
            onClick={() => void download()}
            disabled={busy || downloading}
            data-testid="static-blog-export-download"
          >
            Download the portable site
          </button>
        </div>
      ) : null}
    </section>
  );
}

function isTerminal(status: string): boolean {
  return status === 'succeeded' || status === 'failed' || status === 'cancelled' || status === 'superseded';
}

function sleep(milliseconds: number): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, milliseconds);
  });
}

interface ExportAssetOption {
  readonly assetId: string;
  readonly label: string;
}
