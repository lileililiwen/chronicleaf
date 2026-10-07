import { useId, useState } from 'react';

import {
  MilestoneApiError,
  importCandidates,
} from '../projects-and-milestones/api';
import type {
  MilestoneDraft,
  MilestoneType,
} from '../projects-and-milestones/types';
import { candidateFailureLabel, candidateKindLabel, candidateKindSentence } from './types';
import type { GitHubCandidate } from './types';
import { useGitHubCandidates, useWebhookSecret } from './useGitHubReleaseCandidates';

/**
 * The tag- and event-derived Milestone candidates this project's repository has produced
 * (GITHUB-RELEASE-CANDIDATES-001/002).
 *
 * <b>Five things it deliberately does not do, each because the requirement says so.</b>
 *
 * <b>WORKFLOW-WAYFINDING-002: importing is proposing, not confirming.</b> Each row offers
 * "Record as milestone", which calls the delivered import route and stores a
 * candidate-status record the owner can edit and confirm. There is still no confirm verb
 * here: confirming the imported record happens in Overview through the delivered confirm
 * action, and the success notice points there. A panel that confirmed on import would be
 * making the human decision the requirement reserves for the owner.
 *
 * - It has no confirm verb. "Record as milestone" stores a candidate-status record through
 *   the delivered import route — proposing, not confirming — and confirming the
 *   imported record is a human decision made in Overview through the delivered confirm
 *   action. A panel with a button that confirmed would be a panel making a decision the
 *   owner did not make.
 * - It never says "added" for a proposal. Every row says the thing was <em>proposed</em>,
 *   because that is what happened: somebody published a release, and this server suggested
 *   it might be worth recording. A row that was imported says so beside the proposal,
 *   naming the milestone it became rather than rewriting what the row is.
 * - It shows the source and the received instant, which is the design's own display requirement, and it
 *   shows the provider's source key so an owner can see that the same release delivered twice is one row.
 * - It publishes no secret and has no field to type one into. The webhook secret is stored through the
 *   delivered credential route under the `github-webhook` provider, and this panel only ever refers to it
 *   by whether one is configured.
 * - It treats "no repository connected" and "nothing proposed yet" as different empty states, because they
 *   are different facts and an owner looking at one of them needs to know which.
 */
export function GitHubCandidatePanel({
  projectId,
  archived,
  onNavigate,
}: {
  readonly projectId: string;
  /** An archived project cannot be asked to poll or subscribed; the server refuses both. */
  readonly archived: boolean;
  readonly onNavigate: (to: string) => void;
}) {
  const state = useGitHubCandidates(projectId);
  const secret = useWebhookSecret();
  const [since, setSince] = useState('');
  const [sinceId] = useId();

  const listening = state.subscription?.enabled === true;
  const failure = state.failure;
  const shown = state.candidates.length;

  return (
    <section className="evidence" data-testid="github-candidate-panel">
      <h4 className="evidence__heading">Milestone candidates from GitHub</h4>

      {/*
        The framing, above everything else this panel shows. It is here rather than in a tooltip or a
        footer because it is the single most important thing an owner must not misread: a row below is
        something somebody published, and Chronicleaf is suggesting it. Nothing on this panel has been
        recorded as a Milestone.
      */}
      <p className="view__state" data-testid="github-candidate-framing">
        These are proposals, not Milestones. A tag, a release or a merged pull request on GitHub is
        suggested here so an owner can decide whether it is worth recording; confirming one is done
        elsewhere, and nothing on this panel records anything by itself. Candidate sync and
        scheduled sync run from the{' '}
        <button
          type="button"
          className="link"
          onClick={() => onNavigate(`/projects/${projectId}/automations`)}
          data-testid="github-candidate-automations-link"
        >
          Automations area
        </button>
        , so new releases propose themselves instead of waiting to be asked for.
      </p>

      {state.loading ? (
        <p className="view__state" data-testid="github-candidate-loading">
          Reading the candidates this project has…
        </p>
      ) : state.connectionMissing ? (
        <div className="empty" data-testid="github-candidate-no-connection">
          <p className="empty__title" data-testid="github-candidate-no-connection-title">
            This project has no repository connected
          </p>
          <p className="empty__body" data-testid="github-candidate-no-connection-body">
            A candidate is something read from a repository, so there is nothing to read until this
            project names one. Connect a repository in the panel above and this one will have something to
            propose.
          </p>
        </div>
      ) : (
        <>
          {/*
            The window is an input rather than a choice from a menu because it is a bound, and the server
            refuses an instant it cannot read by name rather than substituting one. An owner who typed
            "last tuesday" gets told that, which is more use than a default nobody chose.
          */}
          <div className="evidence__form">
            <label htmlFor={sinceId}>Only releases and tags published since</label>
            <input
              id={sinceId}
              data-testid="github-candidate-since"
              value={since}
              disabled={archived || state.busy}
              placeholder="2026-09-01T00:00:00Z (blank reads the newest page)"
              onChange={(event) => setSince(event.target.value)}
            />
            <p className="view__hint">
              An ISO 8601 instant, or blank for the newest page this route reads. Blank is not the same as
              everything: the server reads one bounded page either way.
            </p>
          </div>

          <p className="view__actions">
            <button
              className="action action--primary"
              type="button"
              data-testid="github-candidate-poll"
              disabled={archived || state.busy}
              onClick={() => void state.poll(since.trim().length === 0 ? null : since.trim())}
            >
              {state.busy ? 'Asking…' : 'Ask for releases and tags'}
            </button>
            {listening ? (
              <button
                className="action"
                type="button"
                data-testid="github-candidate-stop-listening"
                disabled={archived || state.busy}
                onClick={() => void state.stopListening()}
              >
                Stop accepting deliveries
              </button>
            ) : (
              <button
                className="action"
                type="button"
                data-testid="github-candidate-start-listening"
                disabled={archived || state.busy || secret.id === null}
                onClick={() => {
                  if (secret.id !== null) {
                    void state.setSubscription(secret.id);
                  }
                }}
              >
                Accept signed deliveries
              </button>
            )}
          </p>

          {/*
            The subscription's state is one sentence, and it names what is stored without naming it. The
            owner needs to know a secret is configured; the vault's identifier is one more copy of a thing
            that identifies a secret and has no use in a browser.
          */}
          {/*
            Whether a secret is stored is read from the delivered credential route, and this panel has no
            field a secret could be typed into. That is a deliberate cost: it means the owner stores the
            webhook secret once, in the place secrets are stored, and this panel can never hold one - not
            in memory, not in a form, not in a request it builds. The button is disabled rather than hidden
            until a secret exists, and the sentence below says where to put one, because a disabled button
            with no explanation is the one thing worse than either.
          */}
          <p className="view__state" data-testid="github-candidate-subscription">
            {listening
              ? 'This project is listening for signed deliveries. A delivery that produces no candidate is still recorded, so a receipt says it arrived.'
              : secret.id === null
                ? 'This project is not listening for signed deliveries, and no webhook secret is stored yet. Store one under GitHub webhook secret in Credential settings, then this button will start listening.'
                : 'This project is not listening for signed deliveries. A delivery for it would be refused.'}
          </p>
        </>
      )}

      {state.notice !== null ? (
        <p className="notice notice--ok" role="status" data-testid="github-candidate-notice">
          {state.notice}
        </p>
      ) : null}

      {failure !== null ? (
        <p className="session__error" data-testid="github-candidate-failed">
          {candidateFailureLabel(failure.code)}
        </p>
      ) : null}

      {/*
        The count and the page are different numbers, and the sentence says so. A project with two hundred
        candidates and a page of twenty-five is not an empty list, and saying so is the difference between
        an owner who knows there are more and one who thinks proposals were lost.
      */}
      {state.total > 0 ? (
        <p className="view__state" data-testid="github-candidate-count">
          {state.total === 1
            ? 'One candidate is proposed for this project.'
            : `${state.total} candidates are proposed for this project.`}
          {state.total > shown ? ` Showing the newest ${shown}.` : ''}
        </p>
      ) : null}

      {shown === 0 && !state.connectionMissing ? (
        <div className="empty" data-testid="github-candidate-empty">
          <p className="empty__title" data-testid="github-candidate-empty-title">
            Nothing has been proposed yet
          </p>
          <p className="empty__body" data-testid="github-candidate-empty-body">
            A published release, a created tag, a merged pull request, a closed issue or a crossed star
            threshold appears here once GitHub sends it or a poll reads it. Nothing is proposed until then,
            and a quiet repository is a normal state rather than a failure.
          </p>
        </div>
      ) : (
        <ul className="evidence__items" data-testid="github-candidate-list">
          {state.candidates.map((candidate) => (
            <li className="evidence__item" key={candidate.id} data-testid="github-candidate-row">
              <p className="evidence__item-head">
                <span className="evidence__kind" data-testid="github-candidate-kind">
                  {candidateKindLabel(candidate.kind)}
                </span>
              </p>
              {/*
                The sentence that says "proposed", per kind. It is the design's own display requirement
                made explicit, and it is why a row here can never be read as something that was recorded.
              */}
              <p className="evidence__item-text" data-testid="github-candidate-sentence">
                {candidateKindSentence(candidate.kind)}
              </p>
              <p className="evidence__item-text" data-testid="github-candidate-title">
                {candidate.title}
              </p>
              <p className="evidence__item-meta" data-testid="github-candidate-meta">
                {candidate.source === 'webhook'
                  ? `From a signed delivery${candidate.deliveryId === null ? '' : ` ${candidate.deliveryId}`}.`
                  : 'From a poll of the repository.'}
                {` Received ${candidate.receivedAt}.`}
              </p>
              <p className="evidence__item-meta" data-testid="github-candidate-key">
                {`Source key ${candidate.sourceKey}.`}
                {candidate.revision === null ? '' : ` Commit ${candidate.revision}.`}
              </p>
              {archived ? null : (
                <CandidateImport
                  projectId={projectId}
                  candidate={candidate}
                  onNavigate={onNavigate}
                />
              )}
            </li>
          ))}
        </ul>
      )}

      {/*
        The vocabulary is closed, and this says so rather than implying the list above is exhaustive of
        anything. A kind this build does not route produces an ignored receipt rather than a row, and an
        owner who wonders why a repository event they saw did not appear deserves to be told the rule
        rather than left to guess.
      */}
      <p className="view__state" data-testid="github-candidate-routing">
        Only published releases, created tags, merged pull requests, closed issues and the 100 and 1000
        star thresholds are proposed. Everything else GitHub sends is recorded as received and deliberately
        produces nothing.
      </p>
    </section>
  );
}

/**
 * One row's "Record as milestone" control (WORKFLOW-WAYFINDING-002).
 *
 * The import stores a candidate-status milestone through the delivered import route: proposing,
 * not confirming. The notice names what was stored (or replays the stored row on a second
 * press, which the server answers without duplicating) and points at Overview, where the
 * delivered confirm action makes the human decision. Failures render the server's own
 * `code: message` with field errors named. Nothing fires on render: the only POST leaves
 * from this button's press.
 */
function CandidateImport({
  projectId,
  candidate,
  onNavigate,
}: {
  readonly projectId: string;
  readonly candidate: GitHubCandidate;
  readonly onNavigate: (to: string) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [stored, setStored] = useState<{ milestoneId: string; created: boolean } | null>(null);
  const [failure, setFailure] = useState<MilestoneApiError | null>(null);

  const record = async () => {
    setBusy(true);
    setFailure(null);

    try {
      const items = await importCandidates(projectId, 'github', [
        { sourceKey: candidate.sourceKey, draft: draftFor(candidate) },
      ]);
      const outcome = items.find((item) => item.sourceKey === candidate.sourceKey) ?? items[0];

      if (outcome === undefined) {
        setFailure(
          new MilestoneApiError(
            { code: 'internal_error', message: 'The import answered with no row.', traceId: '' },
            0,
          ),
        );
      } else {
        setStored({ milestoneId: outcome.milestoneId, created: outcome.created });
      }
    } catch (thrown) {
      setFailure(
        thrown instanceof MilestoneApiError
          ? thrown
          : new MilestoneApiError(
              { code: 'internal_error', message: 'The import could not be recorded.', traceId: '' },
              0,
            ),
      );
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="view__actions" data-testid="github-candidate-import-wrap">
      {stored === null ? (
        <button
          type="button"
          className="action"
          onClick={() => void record()}
          disabled={busy}
          data-testid="github-candidate-import"
        >
          {busy ? 'Recording…' : 'Record as milestone'}
        </button>
      ) : (
        <p className="notice notice--ok" role="status" data-testid="github-candidate-imported">
          {stored.created ? 'Recorded' : 'Already recorded'} “{candidate.title}” as a candidate
          milestone. Confirm it in{' '}
          <button
            type="button"
            className="link"
            onClick={() => onNavigate(`/projects/${projectId}`)}
            data-testid="github-candidate-import-overview"
          >
            Overview
          </button>
          .
        </p>
      )}

      {failure ? (
        <p className="notice notice--error" role="alert" data-testid="github-candidate-import-error">
          {failure.code}: {failure.message}
          {Object.entries(failure.fieldErrors).map(([field, messages]) => (
            <span key={field} data-testid="github-candidate-import-field">
              {' '}
              {field}: {messages.join(' ')}
            </span>
          ))}
        </p>
      ) : null}
    </div>
  );
}

/**
 * The milestone draft one candidate becomes.
 *
 * The mapping is fixed and stated here so an owner can see what the press will store: the
 * kind decides the milestone type (a release or tag is a release, a merged pull request a
 * feature, a closed issue a bugfix, a star threshold stars), the tag names the version when
 * the row carries one, the received instant is when it happened as far as this product
 * knows, and the notes carry the provenance (kind, source key, received instant, commit
 * SHA) plus the honest statement that the record still needs a human decision. Lists stay
 * empty: a proposal carries no limitations or next steps of its own.
 */
function draftFor(candidate: GitHubCandidate): MilestoneDraft {
  const type = typeFor(candidate.kind);
  const commit =
    candidate.revision === null || candidate.revision.length === 0
      ? ''
      : `, commit ${candidate.revision}`;

  return {
    title: candidate.title,
    version: candidate.tagName ?? '',
    occurredAt: candidate.receivedAt,
    type,
    notes:
      `Proposed from GitHub ${candidateKindLabel(candidate.kind).toLowerCase()} ` +
      `"${candidate.title}" (source key ${candidate.sourceKey}, ` +
      `received ${candidate.receivedAt}${commit}). Imported as ${type}; edit before confirming.`,
    knownLimitations: [],
    nextSteps: [],
  };
}

function typeFor(kind: GitHubCandidate['kind']): MilestoneType {
  switch (kind) {
    case 'release':
    case 'tag':
      return 'release';
    case 'pull-request':
      return 'feature';
    case 'issue':
      return 'bugfix';
    case 'star':
      return 'stars';
    default:
      return 'custom';
  }
}
