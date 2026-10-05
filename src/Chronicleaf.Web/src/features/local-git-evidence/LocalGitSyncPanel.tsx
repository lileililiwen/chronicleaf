import { useState } from 'react';

import type { Operation, OperationStatus } from '../background-jobs/types';
import { localGitFailureLabel } from './types';
import { useLocalGitSync } from './useLocalGitEvidence';

/**
 * What one milestone may ask its local repository for, and how the job it created is doing
 * (LOCAL-GIT-EVIDENCE-001/002).
 *
 * It sits directly under the evidence panel because the collection and the evidence are the same subject:
 * what is collected is appended to the bundle above, and the revision this panel sends is the revision that
 * bundle is on.
 *
 * Five things it deliberately does not do:
 *
 * - It never shows a progress bar. The 202 said a job was accepted, which is not progress; the only thing
 *   that knows whether the job has run, waited, failed or become superseded is the delivered operations
 *   route, so the status rendered here is that route's answer and nothing else.
 * - It never shows a directory, and it never asks for one. The repository is the project's, decided once in
 *   the panel above, and what it shows is the alias and the relative path - both of which the owner chose.
 * - It never re-asks on the owner's behalf. A collection that is still queued when the tab is closed finishes
 *   on its own, and a surface that re-sent the request would either collect twice or collect against a bundle
 *   that had moved.
 * - It does not refresh the evidence panel above it. It already reads the job's real status, so when that
 *   status stops being able to change it says so, once, and the surface that renders the evidence decides what
 *   to re-read. A panel that reached into a feature it does not own would put the coupling in the wrong place.
 * - It does not offer a comparison the owner did not ask for. A base ref is optional and the panel says what
 *   changes when it is left empty, because a collection that silently compared against something would be a
 *   collection of something else.
 */
export function LocalGitSyncPanel({
  projectId,
  milestoneId,
  milestoneTitle,
  archived,
  onSettled,
}: {
  readonly projectId: string;
  readonly milestoneId: string;
  readonly milestoneTitle: string;
  /** An archived project's evidence cannot move, so nothing is offered for it. */
  readonly archived: boolean;
  /**
   * Called once when the job this panel asked for reaches a status it will not leave.
   *
   * Optional, because a panel rendered on its own has nobody to tell, and absent means exactly that. It is
   * raised from the status this panel already reads rather than from a new timer, and it is raised on a
   * failure, a cancellation and a supersession as well as on a success: a collection that appended nothing
   * still means the server has an answer about this milestone now.
   */
  readonly onSettled?: (operation: Operation) => void;
}) {
  const state = useLocalGitSync(projectId, milestoneId, onSettled);
  const [headRef, setHeadRef] = useState('');
  const [baseRef, setBaseRef] = useState('');

  if (archived) {
    return null;
  }

  const registered = state.repository;

  return (
    <section className="evidence" data-testid="local-git-sync-panel">
      <h4 className="evidence__heading">Collect from the local repository for “{milestoneTitle}”</h4>

      {registered === null ? (
        <p className="notice notice--muted" data-testid="local-git-sync-no-repository">
          {state.repositoryMissing
            ? 'This project has no local repository registered yet, so there is nothing to collect from. Register one in the project header above.'
            : 'What this project collects from could not be read, so nothing is offered here.'}
        </p>
      ) : (
        <>
          <p className="evidence__revision" data-testid="local-git-sync-source">
            {registered.rootAlias}/{registered.relativePath}
            {registered.defaultBranch === null
              ? ''
              : `, whose HEAD is on ${registered.defaultBranch}`}
          </p>

          <p className="view__hint" data-testid="local-git-sync-revision">
            {state.evidenceFailure === null
              ? `Adding to revision ${state.evidenceRevision}. Earlier evidence is kept, not replaced.`
              : `The evidence revision could not be read (${state.evidenceFailure}), so this request names revision ${state.evidenceRevision}.`}
          </p>

          <form
            className="evidence__form"
            data-testid="local-git-sync-form"
            onSubmit={(event) => {
              event.preventDefault();
              void state.requestSync({
                headRef: headRef.trim(),
                baseRef: baseRef.trim(),
              });
            }}
          >
            <p className="view__hint" data-testid="local-git-sync-hint">
              A branch, tag or revision to read, and optionally the ref to compare it against. Leave the
              compare field empty and Chronicleaf collects the head on its own. At most 100 commits, 100
              changed files and 128 KiB of patch text become evidence; anything past that is marked as left
              out rather than quietly dropped. Uncommitted and untracked files are never collected - this
              describes committed revisions, and the bundle says how many uncommitted changes it did not read.
            </p>

            <div className="view">
              <label htmlFor={`local-head-${milestoneId}`}>Ref to collect</label>
              <input
                id={`local-head-${milestoneId}`}
                className="session__input"
                type="text"
                value={headRef}
                autoComplete="off"
                data-testid="local-git-head"
                onChange={(event) => setHeadRef(event.target.value)}
              />
              <p className="evidence__item-meta">
                Letters, digits and <code>. _ - / + ~</code>, up to 255 characters, and never starting with a
                dash. No shell is involved and no ref is ever concatenated into a command, so a value that
                could mean an option to Git is refused by name instead.
              </p>
            </div>

            <div className="view">
              <label htmlFor={`local-base-${milestoneId}`}>Compare against, if you want the diff</label>
              <input
                id={`local-base-${milestoneId}`}
                className="session__input"
                type="text"
                value={baseRef}
                autoComplete="off"
                data-testid="local-git-base"
                onChange={(event) => setBaseRef(event.target.value)}
              />
            </div>

            <div className="form__actions">
              <button
                type="submit"
                className="action action--primary"
                disabled={state.busy || headRef.trim().length === 0}
                data-testid="local-git-sync-submit"
              >
                {state.busy ? 'Asking…' : 'Collect from the local repository'}
              </button>
            </div>

            {headRef.trim().length === 0 ? (
              <p className="view__hint" data-testid="local-git-sync-needs-ref">
                Name a ref first. Chronicleaf will not collect a default branch nobody asked for.
              </p>
            ) : null}
          </form>
        </>
      )}

      {state.failure !== null ? (
        <p className="notice notice--error" role="alert" data-testid="local-git-sync-failed">
          {localGitFailureLabel(state.failure.code)}{' '}
          <span data-testid="local-git-sync-failed-message">
            The server said: {state.failure.message}
          </span>{' '}
          Nothing was queued.
        </p>
      ) : null}

      {state.accepted !== null ? (
        <LocalOperationProgress
          operationId={state.accepted.operationId}
          created={state.accepted.created}
          operation={state.operation}
          failure={state.progressFailure}
        />
      ) : null}
    </section>
  );
}

/**
 * The job's real status, read from the delivered operations route.
 *
 * Three refusals that look alike from the outside get three different sentences here, because they are three
 * different problems with three different next steps: a job that became superseded means the evidence or the
 * repository head moved and nothing was appended, a ref that does not resolve is a word the owner typed, and
 * a job waiting on a stated delay will be tried again.
 */
function LocalOperationProgress({
  operationId,
  created,
  operation,
  failure,
}: {
  readonly operationId: string;
  readonly created: boolean;
  readonly operation: Operation | null;
  readonly failure: string | null;
}) {
  const status: OperationStatus | null = operation?.status ?? null;

  return (
    <div
      className="evidence__bundle"
      data-testid="local-git-operation"
      data-status={status ?? 'unknown'}
    >
      <p className="evidence__revision" data-testid="local-git-operation-heading">
        {created ? 'Collection accepted' : 'Collection already asked for'} ·{' '}
        <span data-testid="local-git-operation-id">{operationId}</span>
      </p>

      <p className="evidence__item-meta" data-testid="local-git-operation-url">
        Read through the operations route at /api/operations/{operationId}.
      </p>

      {failure !== null ? (
        <p
          className="notice notice--error"
          role="alert"
          data-testid="local-git-operation-unreadable"
        >
          The status of this collection could not be read: {failure} What is shown below is the last status
          that did arrive, and it may be out of date.
        </p>
      ) : null}

      {status === null ? (
        <p className="view__state" data-testid="local-git-operation-unknown">
          Accepted, and waiting to be read back. The 202 said the work was recorded, not that any of it has
          happened.
        </p>
      ) : (
        <p className="view__state" data-testid="local-git-operation-status">
          {localStatusSentence(operation as Operation)}
        </p>
      )}

      {status === 'failed' && operation?.errorCode != null ? (
        <p
          className="evidence__item-text evidence__item-text--gap"
          data-testid="local-git-operation-error"
        >
          {localGitFailureLabel(operation.errorCode)}{' '}
          <span data-testid="local-git-operation-code">
            The stored code is {operation.errorCode}. Chronicleaf keeps that code and nothing else on the
            row - no directory, no ref, no command output and no path - so it is shown here verbatim rather
            than dressed up as something Git said.
          </span>
        </p>
      ) : null}
    </div>
  );
}

/**
 * One sentence per status the server can persist.
 *
 * The statuses are the delivered closed union of six words and this function is the only place they become
 * English, so a seventh status would be a compile error rather than a sentence invented here.
 */
function localStatusSentence(operation: Operation): string {
  if (operation.nextAttemptAt !== null && isWaiting(operation)) {
    return `Reading, and waiting until ${formatInstant(operation.nextAttemptAt)} before trying again. The wait is bounded by the job policy, so it cannot be parked indefinitely.`;
  }

  switch (operation.status) {
    case 'queued':
      return `Queued. ${operation.attempt} attempt${operation.attempt === 1 ? '' : 's'} so far, and the queue is what decides when it runs.`;
    case 'running':
      return `Reading the repository. Attempt ${operation.attempt} of ${operation.maxAttempts}.`;
    case 'succeeded':
      return 'Collected. What was gathered has been appended to the evidence above, with each fact naming its own commit revision.';
    case 'failed':
      return 'The collection failed and nothing was appended.';
    case 'cancelled':
      return 'The collection was cancelled before it finished, and nothing was appended.';
    case 'superseded':
      return 'The evidence or the repository head moved while this was collecting, so nothing was appended. Nothing was overwritten.';
    default:
      return 'The request could not be completed.';
  }
}

/**
 * True when the operation is between attempts and has been given a later time to try.
 *
 * Checked against the clock the browser can see rather than a status word, because the server expresses a
 * wait by scheduling the next attempt and not by naming a seventh status.
 */
function isWaiting(operation: Operation): boolean {
  const at = Date.parse(operation.nextAttemptAt ?? '');

  return Number.isFinite(at) && at > Date.now();
}

/** An ISO instant shown in the reader's own zone, with the UTC value available in the title. */
function formatInstant(iso: string): string {
  const date = new Date(iso);

  if (Number.isNaN(date.getTime()) || iso.length === 0) {
    return iso;
  }

  return date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}
