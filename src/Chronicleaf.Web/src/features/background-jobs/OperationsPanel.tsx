import { useState } from 'react';

import { cancelOperation } from './api';
import type { Operation, OperationStatus } from './types';
import { OPERATION_STATUSES } from './types';

/**
 * What the panel says about each status (BACKGROUND-JOBS-001, BACKGROUND-JOBS-002).
 *
 * These are the six words the database stores, unchanged, plus one sentence each saying what the
 * owner is looking at. A status with no sentence is a status the panel would have to invent wording
 * for, and inventing it is exactly how a panel starts telling an owner that `superseded` means
 * something reassuring. The map is total over the union, so a seventh status is a type error rather
 * than a blank row.
 */
const STATUS_WORDING: Record<OperationStatus, string> = {
  queued: 'Waiting for a worker to pick this up.',
  running: 'A worker is running this now.',
  succeeded: 'Finished, and its result is recorded.',
  failed: 'Stopped without a result.',
  cancelled: 'Stopped at the owner’s request, and no result was kept.',
  superseded: 'Its input changed first, so the result was not attached.',
};

/** The visible status word, which is the stored one. */
export function statusWord(status: OperationStatus): string {
  return status;
}

/** One sentence about what the status means for this operation. */
export function statusExplanation(status: OperationStatus): string {
  return STATUS_WORDING[status];
}

/**
 * The operations panel.
 *
 * It reports what the server said and nothing else. There is no sample operation, no assumed
 * progress, no "this usually takes a moment" and no status inferred from a timer: an operation
 * appears here when the API returned it, and its status is the word the API sent. That is the whole
 * contract of this component - a background job's record is the only thing the owner is shown, so a
 * panel that smoothed it would be the one place in the product where a failure could be hidden.
 *
 * Cancelling is offered only while the server says the operation is cancellable, and the button is
 * replaced by the new status the server returns. The request does not claim the work stopped; it
 * reports that the operation is now `cancelled`, and the sentence beside it says that no result was
 * kept.
 */
export function OperationsPanel({
  operations,
  loading,
  failure,
  onChanged,
}: {
  readonly operations: readonly Operation[];
  readonly loading: boolean;
  readonly failure: { readonly message: string } | null;
  readonly onChanged: () => void;
}) {
  const [filter, setFilter] = useState<OperationStatus | 'all'>('all');
  const [cancelling, setCancelling] = useState<string | null>(null);
  const [cancelFailure, setCancelFailure] = useState<string | null>(null);

  const visible =
    filter === 'all' ? operations : operations.filter((operation) => operation.status === filter);

  const stop = async (operation: Operation) => {
    setCancelling(operation.id);
    setCancelFailure(null);

    try {
      await cancelOperation(operation.id);
      onChanged();
    } catch (thrown) {
      setCancelFailure(
        thrown instanceof Error && thrown.message.length > 0
          ? thrown.message
          : 'The operation could not be cancelled.',
      );
    } finally {
      setCancelling(null);
    }
  };

  return (
    <section className="view" data-testid="operations-panel">
      <h2 className="view__heading">Background jobs</h2>
      <p className="view__state" data-testid="operations-intro">
        What the application was asked to do in the background, and what became of it. Every status
        here is the one the application stored.
      </p>

      {failure ? (
        <p className="notice notice--error" role="alert" data-testid="operations-error">
          {failure.message}
        </p>
      ) : null}

      {cancelFailure ? (
        <p className="notice notice--error" role="alert" data-testid="operations-cancel-error">
          {cancelFailure}
        </p>
      ) : null}

      <div className="filters" role="group" aria-label="Filter operations by status">
        <button
          type="button"
          className={filter === 'all' ? 'filter filter--active' : 'filter'}
          onClick={() => setFilter('all')}
          data-testid="operations-filter-all"
          aria-pressed={filter === 'all'}
        >
          All
        </button>
        {OPERATION_STATUSES.map((status) => (
          <button
            key={status}
            type="button"
            className={filter === status ? 'filter filter--active' : 'filter'}
            onClick={() => setFilter(status)}
            data-testid={`operations-filter-${status}`}
            aria-pressed={filter === status}
          >
            {statusWord(status)}
          </button>
        ))}
      </div>

      {loading && operations.length === 0 ? (
        <p className="view__state" data-testid="operations-loading">
          Loading background jobs…
        </p>
      ) : visible.length === 0 ? (
        <div className="empty" data-testid="operations-empty">
          <p className="empty__title">
            {operations.length === 0
              ? 'No background jobs have run yet.'
              : 'No background jobs have that status.'}
          </p>
        </div>
      ) : (
        <ul className="records" data-testid="operations-list">
          {visible.map((operation) => (
            <li key={operation.id}>
              <article className="record" data-testid="operation-row" data-status={operation.status}>
                <p className="record__title">
                  <span className="record__name">{operation.kind}</span>
                  <span className="badge" data-testid="operation-status">
                    {statusWord(operation.status)}
                  </span>
                </p>

                <p className="record__body" data-testid="operation-explanation">
                  {statusExplanation(operation.status)}
                </p>

                <dl className="facts">
                  <div>
                    <dt>Attempt</dt>
                    <dd data-testid="operation-attempt">
                      {operation.attempt} of {operation.maxAttempts}
                    </dd>
                  </div>
                  <div>
                    <dt>Input revision</dt>
                    <dd data-testid="operation-revision">{operation.inputRevision}</dd>
                  </div>
                  {operation.errorCode ? (
                    <div>
                      <dt>Error code</dt>
                      <dd data-testid="operation-error-code">{operation.errorCode}</dd>
                    </div>
                  ) : null}
                  {operation.resultRef ? (
                    <div>
                      <dt>Result</dt>
                      <dd data-testid="operation-result-ref">{operation.resultRef}</dd>
                    </div>
                  ) : null}
                  {operation.nextAttemptAt && operation.status === 'queued' ? (
                    <div>
                      <dt>Next attempt</dt>
                      <dd data-testid="operation-next-attempt">{formatTime(operation.nextAttemptAt)}</dd>
                    </div>
                  ) : null}
                </dl>

                {operation.cancellable ? (
                  <p className="record__actions">
                    <button
                      type="button"
                      className="action"
                      onClick={() => void stop(operation)}
                      disabled={cancelling === operation.id}
                      data-testid="operation-cancel"
                    >
                      {cancelling === operation.id ? 'Stopping…' : 'Stop this job'}
                    </button>
                  </p>
                ) : null}
              </article>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function formatTime(value: string): string {
  const parsed = new Date(value);

  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}
