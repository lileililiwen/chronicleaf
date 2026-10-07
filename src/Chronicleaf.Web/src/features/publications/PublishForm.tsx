import { useEffect, useState } from 'react';

import { listTargets, previewPublish, publishAsset, readReceipt } from './api';
import { PublicationApiError, type PublicationReceipt, type PublishAccepted, type PublishPreview, type PublisherTarget } from './types';

/**
 * The one shared publish confirm flow (PUBLISH-FROM-VARIANT-002/003).
 *
 * Both publish hosts - the Publish area's `PublicationPanel` and the
 * variant row's publish section - render this form rather than owning
 * their own consent. The discipline is the delivered panel's, moved not
 * weakened: the preview authorizes nothing, and the publish carries a
 * separate, explicit confirmation behind its own control. Refusals show
 * the server's own code and message verbatim.
 *
 * Nothing here fires on render except the target list read: previewing
 * and publishing are explicit owner acts, and the asset and approval ids
 * are fixed by the host rather than typed.
 */
export function PublishForm({
  assetId,
  approvalId,
  initialTargetId,
  namespace = 'publication',
}: {
  /** The asset the host picked. Fixed: this form never re-names it. */
  readonly assetId: string;
  /** The approval the host read. Prefilled, still shown, never silently substituted. */
  readonly approvalId: string;
  /** A target to start on when the list names it. */
  readonly initialTargetId?: string;
  /** Testid scope. The Publish area keeps `publication` so every existing selector holds. */
  readonly namespace?: string;
}) {
  const [targets, setTargets] = useState<readonly PublisherTarget[] | null>(null);
  const [targetsFailure, setTargetsFailure] = useState<PublicationApiError | null>(null);
  const [targetId, setTargetId] = useState(initialTargetId ?? '');
  const [mode, setMode] = useState('draft');
  const [approval, setApproval] = useState(approvalId);
  const [idempotencyKey, setIdempotencyKey] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [preview, setPreview] = useState<PublishPreview | null>(null);
  const [receipt, setReceipt] = useState<PublicationReceipt | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<PublicationApiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    setApproval(approvalId);
  }, [approvalId]);

  // A new asset invalidates the preview and the receipt; a refilled approval id does
  // not, because neither the preview nor the receipt is bound to it until publish.
  useEffect(() => {
    setPreview(null);
    setReceipt(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assetId]);

  useEffect(() => {
    let active = true;
    const controller = new AbortController();

    listTargets(controller.signal)
      .then((listed) => {
        if (!active) {
          return;
        }

        setTargets(listed.targets);
        setTargetsFailure(null);
        setTargetId((current) => {
          if (current !== '' && listed.targets.some((target) => target.targetId === current)) {
            return current;
          }

          return firstServable(listed.targets);
        });
      })
      .catch((thrown: unknown) => {
        if (active && !isAbort(thrown)) {
          setTargetsFailure(asFailure(thrown));
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, []);

  const run = async (work: () => Promise<string | null>) => {
    setBusy(true);
    setFailure(null);
    setNotice(null);

    try {
      setNotice(await work());
    } catch (thrown) {
      setFailure(asFailure(thrown));
    } finally {
      setBusy(false);
    }
  };

  const previewSend = () =>
    run(async () => {
      setPreview(await previewPublish(assetId, targetId, mode));
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

      const answered = await publishAsset(assetId, {
        targetId,
        mode,
        previewHash: preview.previewHash,
        approvalId: approval.trim(),
        idempotencyKey: idempotencyKey.trim(),
        confirm: confirmed,
      });

      // The two answers share every field name including `operationId`: what tells
      // them apart is `created`, true only on the 202 that accepted a new publish
      // and always false on the 200 that returns the original receipt.
      if (isFreshAcceptance(answered)) {
        setReceipt(await readReceipt(answered.attemptId));
        return `The publish is accepted as operation ${answered.operationId}.`;
      }

      setReceipt(answered);
      return 'An identical request had already created this publish; its receipt is shown.';
    });

  const loadingTargets = targets === null && targetsFailure === null;

  return (
    <section aria-label="Publish form" data-testid={`${namespace}-form`}>
      {notice ? (
        <p className="notice notice--ok" role="status" data-testid={`${namespace}-notice`}>
          {notice}
        </p>
      ) : null}

      {failure ? (
        <p className="notice notice--error" role="alert" data-testid={`${namespace}-error`}>
          {failure.message}
        </p>
      ) : null}

      {loadingTargets ? (
        <p className="view__state" data-testid={`${namespace}-targets-loading`}>
          Reading publisher targets…
        </p>
      ) : null}

      {targetsFailure ? (
        <p className="notice notice--error" role="alert" data-testid={`${namespace}-targets-error`}>
          {targetsFailure.message}
        </p>
      ) : null}

      {targets !== null ? (
        targets.length === 0 ? (
          <p className="view__state" data-testid={`${namespace}-targets-empty`}>
            No publisher target is classified on this installation, so there is nothing to
            send to.
          </p>
        ) : (
          <label className="form__label">
            Target
            <select
              className="form__input"
              value={targetId}
              onChange={(event) => setTargetId(event.target.value)}
              data-testid={`${namespace}-target-picker`}
            >
              {targets.map((target) => (
                <option
                  key={target.targetId}
                  value={target.targetId}
                  disabled={target.exportOnly}
                >
                  {targetLabel(target)}
                </option>
              ))}
            </select>
          </label>
        )
      ) : null}

      <label className="form__label">
        Mode
        <select
          className="form__input"
          value={mode}
          onChange={(event) => setMode(event.target.value)}
          data-testid={`${namespace}-mode-input`}
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
          disabled={busy || loadingTargets}
          data-testid={`${namespace}-preview`}
        >
          Preview what a send would do
        </button>
      </div>

      {preview ? (
        <dl className="session__facts" data-testid={`${namespace}-preview-result`}>
          <dt>Preview hash</dt>
          <dd data-testid={`${namespace}-preview-hash`}>{preview.previewHash}</dd>
          <dt>Content revision</dt>
          <dd data-testid={`${namespace}-preview-revision`}>{preview.contentRevision}</dd>
          <dt>Warnings</dt>
          <dd data-testid={`${namespace}-preview-warnings`}>
            {preview.warnings.length === 0 ? 'None.' : preview.warnings.join(' ')}
          </dd>
        </dl>
      ) : null}

      <label className="form__label">
        Approval id the request was read against
        <input
          type="text"
          className="form__input"
          value={approval}
          onChange={(event) => setApproval(event.target.value)}
          data-testid={`${namespace}-approval-input`}
        />
      </label>
      <label className="form__label">
        Idempotency key
        <input
          type="text"
          className="form__input"
          value={idempotencyKey}
          onChange={(event) => setIdempotencyKey(event.target.value)}
          data-testid={`${namespace}-idempotency-input`}
        />
      </label>
      <label className="form__label">
        <input
          type="checkbox"
          checked={confirmed}
          onChange={(event) => setConfirmed(event.target.checked)}
          data-testid={`${namespace}-confirm`}
        />
        I confirm this send to the named target
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action action--primary"
          onClick={() => void publish()}
          disabled={busy || loadingTargets}
          data-testid={`${namespace}-publish`}
        >
          Publish
        </button>
      </div>

      {receipt === null ? null : (
        <dl className="session__facts" data-testid={`${namespace}-receipt`}>
          <dt>State</dt>
          <dd data-testid={`${namespace}-state`}>{receipt.state}</dd>
          <dt>Target</dt>
          <dd data-testid={`${namespace}-target`}>{receipt.targetId}</dd>
          <dt>Mode</dt>
          <dd data-testid={`${namespace}-mode`}>{receipt.mode}</dd>
          {receipt.errorCode ? (
            <>
              <dt>Error</dt>
              <dd data-testid={`${namespace}-error-code`}>{receipt.errorCode}</dd>
            </>
          ) : null}
          {receipt.reconcileHint ? (
            <>
              <dt>Reconcile</dt>
              <dd data-testid={`${namespace}-reconcile-hint`}>{receipt.reconcileHint}</dd>
            </>
          ) : null}
        </dl>
      )}
    </section>
  );
}

function targetLabel(target: PublisherTarget): string {
  if (target.exportOnly) {
    return `${target.targetId} (hand-carried export, not sent from here)`;
  }

  const modes = [
    target.supportsDraft ? 'draft' : null,
    target.supportsPublish ? 'publish' : null,
  ].filter((mode): mode is string => mode !== null);

  return `${target.targetId} (${modes.join(' + ')})`;
}

function firstServable(targets: readonly PublisherTarget[]): string {
  const servable = targets.find((target) => !target.exportOnly);
  return servable?.targetId ?? targets[0]?.targetId ?? '';
}

/** True only for the 202 that accepted a new publish; every receipt reads `created: false`. */
function isFreshAcceptance(
  answered: PublishAccepted | PublicationReceipt,
): answered is PublishAccepted {
  return answered.created;
}

function asFailure(thrown: unknown): PublicationApiError | null {
  return thrown instanceof PublicationApiError ? thrown : null;
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}
