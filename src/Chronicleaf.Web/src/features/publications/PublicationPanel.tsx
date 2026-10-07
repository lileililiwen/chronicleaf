import { useState } from 'react';

import { previewPublish, publishAsset, readReceipt, reconcileReceipt } from './api';
import { PublicationApiError, type PublicationReceipt, type PublishPreview } from './types';

/**
 * The publication status and reconcile triggers (FEATURE-TRIGGER-PANELS-005).
 *
 * Three states are visible here and each is shown as what it is. With no
 * publication attempt there is an empty state, not a status. A refusal shows
 * the server's own code and message verbatim. Nothing is published by
 * rendering: the preview authorizes nothing, and the publish carries a
 * separate, explicit confirmation behind its own control.
 *
 * Nothing here fires on render: every route call below is behind an explicit
 * owner act, and the attempt and asset ids are typed by the owner.
 */
export function PublicationPanel() {
  const [attemptId, setAttemptId] = useState('');
  const [receipt, setReceipt] = useState<PublicationReceipt | null>(null);
  const [assetId, setAssetId] = useState('');
  const [targetId, setTargetId] = useState('');
  const [mode, setMode] = useState('draft');
  const [approvalId, setApprovalId] = useState('');
  const [idempotencyKey, setIdempotencyKey] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [preview, setPreview] = useState<PublishPreview | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<PublicationApiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

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

  const previewSend = () =>
    run(async () => {
      setPreview(await previewPublish(assetId.trim(), targetId.trim(), mode));
      return null;
    });

  const publish = () =>
    run(async () => {
      if (preview === null) {
        throw new PublicationApiError(
          {
            code: 'preview_required',
            message: 'A publish confirms the preview it was read against. Preview first.',
            traceId: '',
          },
          400,
        );
      }

      const answered = await publishAsset(assetId.trim(), {
        targetId: targetId.trim(),
        mode,
        previewHash: preview.previewHash,
        approvalId: approvalId.trim(),
        idempotencyKey: idempotencyKey.trim(),
        confirm: confirmed,
      });

      if ('operationId' in answered) {
        return `The publish is accepted as operation ${answered.operationId}.`;
      }

      setReceipt(answered);
      return 'An identical request had already created this publish; its receipt is shown.';
    });

  return (
    <section className="view" aria-label="Publications" data-testid="publication-panel">
      <h3 className="view__heading" data-testid="publication-heading">
        Publications
      </h3>
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

      <h4 className="view__heading">Publication status</h4>
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

      <h4 className="view__heading">Publish an asset</h4>
      <label className="form__label">
        Asset id
        <input
          type="text"
          className="form__input"
          value={assetId}
          onChange={(event) => setAssetId(event.target.value)}
          data-testid="publication-asset-input"
        />
      </label>
      <label className="form__label">
        Target id
        <input
          type="text"
          className="form__input"
          value={targetId}
          onChange={(event) => setTargetId(event.target.value)}
          data-testid="publication-target-input"
        />
      </label>
      <label className="form__label">
        Mode
        <select
          className="form__input"
          value={mode}
          onChange={(event) => setMode(event.target.value)}
          data-testid="publication-mode-input"
        >
          <option value="draft">draft</option>
          <option value="publish">publish</option>
        </select>
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action"
          onClick={() => void previewSend()}
          disabled={busy}
          data-testid="publication-preview"
        >
          Preview what a send would do
        </button>
      </div>

      {preview ? (
        <dl className="session__facts" data-testid="publication-preview-result">
          <dt>Preview hash</dt>
          <dd data-testid="publication-preview-hash">{preview.previewHash}</dd>
          <dt>Content revision</dt>
          <dd data-testid="publication-preview-revision">{preview.contentRevision}</dd>
          <dt>Warnings</dt>
          <dd data-testid="publication-preview-warnings">
            {preview.warnings.length === 0 ? 'None.' : preview.warnings.join(' ')}
          </dd>
        </dl>
      ) : null}

      <label className="form__label">
        Approval id the request was read against
        <input
          type="text"
          className="form__input"
          value={approvalId}
          onChange={(event) => setApprovalId(event.target.value)}
          data-testid="publication-approval-input"
        />
      </label>
      <label className="form__label">
        Idempotency key
        <input
          type="text"
          className="form__input"
          value={idempotencyKey}
          onChange={(event) => setIdempotencyKey(event.target.value)}
          data-testid="publication-idempotency-input"
        />
      </label>
      <label className="form__label">
        <input
          type="checkbox"
          checked={confirmed}
          onChange={(event) => setConfirmed(event.target.checked)}
          data-testid="publication-confirm"
        />
        I confirm this send to the named target
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action action--primary"
          onClick={() => void publish()}
          disabled={busy}
          data-testid="publication-publish"
        >
          Publish
        </button>
      </div>
    </section>
  );
}
