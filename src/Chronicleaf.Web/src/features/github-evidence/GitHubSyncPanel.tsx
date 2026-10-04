import { useState } from 'react';

import type { Operation, OperationStatus } from '../background-jobs/types';
import { gitHubFailureLabel } from './types';
import { useGitHubSync } from './useGitHubEvidence';

/**
 * What one milestone may ask its repository for, and how the job it created is doing
 * (GITHUB-EVIDENCE-001/002).
 *
 * It sits directly under the evidence panel because the collection and the evidence are the same
 * subject: what is collected is appended to the bundle above, and the revision this panel sends is the
 * revision the bundle above is on.
 *
 * Four things it deliberately does not do:
 *
 * - It never shows a progress bar. The 202 said a job was accepted, which is not progress; the only
 *   thing that knows whether the job has run, waited, failed or become superseded is the delivered
 *   operations route, so the status rendered here is that route's answer and nothing else. A bar that
 *   moved on a timer would be this panel's optimism wearing the costume of a measurement.
 * - It never shows a token, and it never asks for one. The repository and the credential are the
 *   project's, decided once in the panel above.
 * - It does not re-ask on the owner's behalf. A collection that is still queued when the tab is closed
 *   finishes on its own, and a surface that re-sent the request would either collect twice or collect
 *   against a bundle that had moved.
 * - It does not refresh the evidence panel above it. It already reads the job's real status, so when
 *   that status stops being able to change it says so, once, and the surface that renders the evidence
 *   decides what to re-read. A panel that reached into a feature it does not own would put the coupling
 *   in the wrong place; the alternative - a second interval polling the same bundle - would double the
 *   request rate on every milestone row for a change this package already knows about.
 */
export function GitHubSyncPanel({
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
   * Optional, because a panel rendered on its own has nobody to tell, and absent means exactly that. It
   * is raised from the status this panel already reads rather than from a new timer, and it is raised
   * on a failure, a cancellation and a supersession as well as on a success: a collection that appended
   * nothing still means the server has an answer about this milestone now, and a surface that only
   * reported the happy case would be reporting what it hoped for.
   */
  readonly onSettled?: (operation: Operation) => void;
}) {
  const state = useGitHubSync(projectId, milestoneId, onSettled);
  const [headRef, setHeadRef] = useState('');
  const [baseRef, setBaseRef] = useState('');
  const [releaseTag, setReleaseTag] = useState('');
  const [issueNumber, setIssueNumber] = useState('');
  const [pullRequestNumber, setPullRequestNumber] = useState('');

  if (archived) {
    return null;
  }

  const connected = state.connection;

  return (
    <section className="evidence" data-testid="github-sync-panel">
      <h4 className="evidence__heading">Collect from GitHub for “{milestoneTitle}”</h4>

      {connected === null ? (
        <p className="notice notice--muted" data-testid="github-sync-no-connection">
          {state.connectionMissing
            ? 'This project has no repository connected yet, so there is nothing to collect from. Connect one in the project header above.'
            : 'What this project collects from could not be read, so nothing is offered here.'}
        </p>
      ) : (
        <>
          <p className="evidence__revision" data-testid="github-sync-source">
            {connected.owner}/{connected.repository}
            {connected.defaultBranch === null ? '' : `, whose default branch is ${connected.defaultBranch}`}
          </p>

          <p className="view__hint" data-testid="github-sync-revision">
            {state.evidenceFailure === null
              ? `Adding to revision ${state.evidenceRevision}. Earlier evidence is kept, not replaced.`
              : `The evidence revision could not be read (${state.evidenceFailure}), so this request names revision ${state.evidenceRevision}.`}
          </p>

          <form
            className="evidence__form"
            data-testid="github-sync-form"
            onSubmit={(event) => {
              event.preventDefault();
              void state.requestSync({
                headRef: headRef.trim(),
                baseRef: baseRef.trim(),
                releaseTag: releaseTag.trim(),
                issueNumber: issueNumber.trim(),
                pullRequestNumber: pullRequestNumber.trim(),
              });
            }}
          >
            <p className="view__hint" data-testid="github-sync-hint">
              A branch, tag or revision to read, and optionally the ref to compare it against. Leave the
              rest empty and Chronicleaf collects the head on its own. At most 100 commits, 100 changed
              files and 200 KiB of diff text become evidence; anything past that is marked as left out
              rather than quietly dropped.
            </p>

            <div className="view">
              <label htmlFor={`github-head-${milestoneId}`}>Ref to collect</label>
              <input
                id={`github-head-${milestoneId}`}
                className="session__input"
                type="text"
                value={headRef}
                autoComplete="off"
                data-testid="github-head"
                onChange={(event) => setHeadRef(event.target.value)}
              />
              <p className="evidence__item-meta">
                Letters, digits and <code>. _ - / + ~</code>, up to 255 characters. No scheme and no
                host: Chronicleaf refuses a ref that could mean a path somewhere else.
              </p>
            </div>

            <div className="view">
              <label htmlFor={`github-base-${milestoneId}`}>Compare against, if you want the diff</label>
              <input
                id={`github-base-${milestoneId}`}
                className="session__input"
                type="text"
                value={baseRef}
                autoComplete="off"
                data-testid="github-base"
                onChange={(event) => setBaseRef(event.target.value)}
              />
            </div>

            <div className="view">
              <label htmlFor={`github-release-${milestoneId}`}>Release tag, if there is one</label>
              <input
                id={`github-release-${milestoneId}`}
                className="session__input"
                type="text"
                value={releaseTag}
                autoComplete="off"
                data-testid="github-release"
                onChange={(event) => setReleaseTag(event.target.value)}
              />
            </div>

            <div className="view">
              <label htmlFor={`github-issue-${milestoneId}`}>Issue number, if there is one</label>
              <input
                id={`github-issue-${milestoneId}`}
                className="session__input"
                type="text"
                inputMode="numeric"
                value={issueNumber}
                autoComplete="off"
                data-testid="github-issue"
                onChange={(event) => setIssueNumber(event.target.value)}
              />
            </div>

            <div className="view">
              <label htmlFor={`github-pull-${milestoneId}`}>Pull request number, if there is one</label>
              <input
                id={`github-pull-${milestoneId}`}
                className="session__input"
                type="text"
                inputMode="numeric"
                value={pullRequestNumber}
                autoComplete="off"
                data-testid="github-pull"
                onChange={(event) => setPullRequestNumber(event.target.value)}
              />
            </div>

            <div className="form__actions">
              <button
                type="submit"
                className="action action--primary"
                disabled={state.busy || headRef.trim().length === 0}
                data-testid="github-sync-submit"
              >
                {state.busy ? 'Asking…' : 'Collect from GitHub'}
              </button>
            </div>

            {headRef.trim().length === 0 ? (
              <p className="view__hint" data-testid="github-sync-needs-ref">
                Name a ref first. Chronicleaf will not collect a default branch nobody asked for.
              </p>
            ) : null}
          </form>
        </>
      )}

      {state.failure !== null ? (
        <p className="notice notice--error" role="alert" data-testid="github-sync-failed">
          {gitHubFailureLabel(state.failure.code)}{' '}
          <span data-testid="github-sync-failed-message">
            The server said: {state.failure.message}
          </span>{' '}
          Nothing was queued.
        </p>
      ) : null}

      {state.accepted !== null ? (
        <OperationProgress
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
 * Three refusals that look alike from the outside get three different sentences here, because they are
 * three different problems with three different next steps: a job that became superseded means the
 * evidence moved and nothing was appended, a repository that could not be read means the name or the
 * token is wrong, and a job waiting on a stated delay means GitHub asked us to slow down and the wait
 * is already bounded.
 */
function OperationProgress({
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
    <div className="evidence__bundle" data-testid="github-operation" data-status={status ?? 'unknown'}>
      <p className="evidence__revision" data-testid="github-operation-heading">
        {created ? 'Collection accepted' : 'Collection already asked for'} ·{' '}
        <span data-testid="github-operation-id">{operationId}</span>
      </p>

      <p className="evidence__item-meta" data-testid="github-operation-url">
        Read through the operations route at /api/operations/{operationId}.
      </p>

      {failure !== null ? (
        <p className="notice notice--error" role="alert" data-testid="github-operation-unreadable">
          The status of this collection could not be read: {failure} What is shown below is the last
          status that did arrive, and it may be out of date.
        </p>
      ) : null}

      {status === null ? (
        <p className="view__state" data-testid="github-operation-unknown">
          Accepted, and waiting to be read back. The 202 said the work was recorded, not that any of
          it has happened.
        </p>
      ) : (
        <p className="view__state" data-testid="github-operation-status">
          {statusSentence(operation as Operation)}
        </p>
      )}

      {status === 'failed' && operation?.errorCode != null ? (
        <p className="evidence__item-text evidence__item-text--gap" data-testid="github-operation-error">
          {gitHubFailureLabel(operation.errorCode)}{' '}
          <span data-testid="github-operation-code">
            The stored code is {operation.errorCode}. Chronicleaf keeps that code and nothing else on
            the row - no GitHub response body, no repository name and no token - so it is shown here
            verbatim rather than dressed up as something GitHub said.
          </span>
        </p>
      ) : null}
    </div>
  );
}

/**
 * One sentence per status the server can persist.
 *
 * The statuses are the delivered closed union of six words and this function is the only place they
 * become English, so a seventh status would be a compile error rather than a sentence invented here.
 */
function statusSentence(operation: Operation): string {
  if (operation.nextAttemptAt !== null && isWaiting(operation)) {
    return `Collecting, and waiting until ${formatInstant(operation.nextAttemptAt)} before trying again. ` +
      'GitHub asked for that delay and the job policy bounds it, so it cannot be parked indefinitely.';
  }

  switch (operation.status) {
    case 'queued':
      return `Queued. ${operation.attempt} attempt${operation.attempt === 1 ? '' : 's'} so far, and the queue is what decides when it runs.`;
    case 'running':
      return `Collecting from GitHub. Attempt ${operation.attempt} of ${operation.maxAttempts}.`;
    case 'succeeded':
      return 'Collected. What was gathered has been appended to the evidence above, with each fact naming its own source revision.';
    case 'failed':
      return 'The collection failed and nothing was appended.';
    case 'cancelled':
      return 'The collection was cancelled before it finished, and nothing was appended.';
    case 'superseded':
      return 'The evidence moved while this was collecting, so nothing was appended. Nothing was overwritten.';
    default:
      return 'The request could not be completed.';
  }
}

/**
 * True when the operation is between attempts and has been given a later time to try.
 *
 * Checked against the clock the browser can see rather than a status word, because the server
 * expresses a wait by scheduling the next attempt and not by naming a seventh status.
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
