import { useEffect, useState } from 'react';

import { listMilestones } from '../projects-and-milestones/api';
import type { Milestone } from '../projects-and-milestones/types';
import { readDraft } from '../grounded-markdown-drafts/api';
import { listVariants } from '../content-asset-variants/api';
import { readReview } from '../markdown-review-export/api';
import { PublishForm } from './PublishForm';
import { readReceipt, reconcileReceipt } from './api';
import { PublicationApiError, type PublicationReceipt } from './types';

/**
 * The publication status and reconcile triggers (FEATURE-TRIGGER-PANELS-005).
 *
 * Three states are visible here and each is shown as what it is. With no
 * publication attempt there is an empty state, not a status. A refusal shows
 * the server's own code and message verbatim. Nothing is published by
 * rendering: the preview authorizes nothing, and the publish carries a
 * separate, explicit confirmation behind its own control.
 *
 * The send half hosts the one shared publish form under pickers: the owner
 * picks a milestone, then one of its assets, then the form's own target
 * picker names where it goes. Nothing here fires on render except the
 * reads that fill the pickers; the attempt id for a past receipt is still
 * named by the owner, because no list route serves attempts.
 */
export function PublicationPanel({ projectId }: { readonly projectId: string }) {
  const [attemptId, setAttemptId] = useState('');
  const [receipt, setReceipt] = useState<PublicationReceipt | null>(null);
  const [milestones, setMilestones] = useState<readonly Milestone[] | null>(null);
  const [milestoneId, setMilestoneId] = useState('');
  const [options, setOptions] = useState<readonly AssetOption[] | null>(null);
  const [assetId, setAssetId] = useState('');
  const [approvalId, setApprovalId] = useState('');
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<PublicationApiError | null>(null);
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
      setAssetId('');
      setApprovalId('');
      return;
    }

    let active = true;
    const controller = new AbortController();

    Promise.all([readDraft(milestoneId, controller.signal), listVariants(milestoneId, controller.signal)])
      .then(async ([draft, variants]) => {
        if (!active) {
          return;
        }

        const built: AssetOption[] = [];

        if (draft !== null && draft.assetId !== null && draft.currentRevision !== null) {
          built.push({ assetId: draft.assetId, label: `story (revision ${draft.currentRevision})` });
        }

        for (const variant of variants) {
          built.push({
            assetId: variant.assetId,
            label: `${variant.type} (revision ${variant.currentRevision})`,
          });
        }

        if (!active) {
          return;
        }

        setOptions(built);
        setAssetId((current) =>
          current !== '' && built.some((option) => option.assetId === current)
            ? current
            : (built[0]?.assetId ?? ''),
        );
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

  useEffect(() => {
    if (assetId === '') {
      setApprovalId('');
      return;
    }

    let active = true;
    const controller = new AbortController();

    readReview(assetId, controller.signal)
      .then((state) => {
        if (active) {
          setApprovalId(state.approval?.approvalId ?? '');
        }
      })
      .catch(() => {
        if (active) {
          setApprovalId('');
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, [assetId]);

  const run = async (work: () => Promise<string | null>) => {
    setBusy(true);
    setFailure(null);
    setNotice(null);

    try {
      setNotice(await work());
    } catch (thrown) {
      setFailure(thrown instanceof PublicationApiError ? thrown : null);
    } finally {
      setBusy(false);
    }
  };

  const read = () =>
    run(async () => {
      setReceipt(await readReceipt(attemptId.trim()));
      return null;
    });

  const reconcile = () =>
    run(async () => {
      setReceipt(await reconcileReceipt(attemptId.trim()));
      return 'The receipt is reconciled against what the target reports.';
    });

  return (
    <section className="view" aria-label="Publications" data-testid="publication-panel">
      {/* WEB-A11Y-ROLES-001. Project-level panels are h2 sections under the view h1. */}
      <h2 className="view__heading" data-testid="publication-heading">
        Publications
      </h2>
      <p className="view__hint" data-testid="publication-hint">
        A receipt names what was sent where and what the remote side answered. Nothing here
        contacts a transport except through the route the owner explicitly triggered.
      </p>

      {notice ? (
        <p className="notice notice--ok" role="status" data-testid="publication-notice">
          {notice}
        </p>
      ) : null}

      {failure ? (
        <p className="notice notice--error" role="alert" data-testid="publication-error">
          {failure.message}
        </p>
      ) : null}

      <h3 className="view__heading">Publication status</h3>
      <label className="form__label">
        Attempt id
        <input
          type="text"
          className="form__input"
          value={attemptId}
          onChange={(event) => setAttemptId(event.target.value)}
          data-testid="publication-attempt-input"
        />
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action"
          onClick={() => void read()}
          disabled={busy}
          data-testid="publication-read"
        >
          Read status
        </button>
        <button
          type="button"
          className="action"
          onClick={() => void reconcile()}
          disabled={busy}
          data-testid="publication-reconcile"
        >
          Reconcile
        </button>
      </div>

      {receipt === null ? (
        <p className="view__state" data-testid="publication-empty">
          No publication attempt selected. Name an attempt to read what the remote side
          answered.
        </p>
      ) : (
        <dl className="session__facts" data-testid="publication-receipt">
          <dt>State</dt>
          <dd data-testid="publication-state">{receipt.state}</dd>
          <dt>Target</dt>
          <dd data-testid="publication-target">{receipt.targetId}</dd>
          <dt>Mode</dt>
          <dd data-testid="publication-mode">{receipt.mode}</dd>
          {receipt.errorCode ? (
            <>
              <dt>Error</dt>
              <dd data-testid="publication-error-code">{receipt.errorCode}</dd>
            </>
          ) : null}
          {receipt.reconcileHint ? (
            <>
              <dt>Reconcile</dt>
              <dd data-testid="publication-reconcile-hint">{receipt.reconcileHint}</dd>
            </>
          ) : null}
        </dl>
      )}

      <h3 className="view__heading">Publish an asset</h3>
      {milestones === null ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="publication-milestones-loading">
          Reading milestones…
        </p>
      ) : milestones.length === 0 ? (
        <p className="view__state" data-testid="publication-milestones-empty">
          No milestone recorded yet, so there is nothing to publish.
        </p>
      ) : (
        <label className="form__label">
          Milestone
          <select
            className="form__input"
            value={milestoneId}
            onChange={(event) => setMilestoneId(event.target.value)}
            data-testid="publication-milestone-picker"
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
        <p className="view__state" data-testid="publication-assets-empty">
          This milestone holds no draft and no derived format, so there is nothing to
          publish from it.
        </p>
      ) : (
        <label className="form__label">
          Asset
          <select
            className="form__input"
            value={assetId}
            onChange={(event) => setAssetId(event.target.value)}
            data-testid="publication-asset-picker"
          >
            {options.map((option) => (
              <option key={option.assetId} value={option.assetId}>
                {option.label}
              </option>
            ))}
          </select>
        </label>
      )}

      {assetId === '' ? null : (
        <PublishForm key={assetId} assetId={assetId} approvalId={approvalId} namespace="publication" />
      )}
    </section>
  );
}

interface AssetOption {
  readonly assetId: string;
  readonly label: string;
}
