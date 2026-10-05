import { useCallback, useEffect, useRef, useState } from 'react';

import { getOperation } from '../background-jobs/api';
import type { Operation, OperationStatus } from '../background-jobs/types';
import { getProject } from '../projects-and-milestones/api';
import { readProvider } from '../credential-vault/api';
import {
  GitHubCandidateApiError,
  readCandidates,
  readSubscription,
  requestSync,
  subscribe,
  unsubscribe,
} from './api';
import type {
  GitHubCandidate,
  GitHubCandidateSyncAccepted,
  GitHubWebhookSubscription,
} from './types';

/**
 * The state behind the candidate panel.
 *
 * <b>One hook, and it re-reads on a signal rather than on a timer.</b> The panel is mounted on a project
 * page, and the two things that can change what it shows are both things it did itself: a poll it
 * started, and a delivery that arrived. A panel that polled on its own would be making a claim about
 * GitHub's activity that it has no way to know, and a panel that never re-read would show an owner an
 * empty list after they had just asked for a poll. So the poll's terminal status raises a reload token
 * once, the same shape the delivered evidence panel uses for its own background writes, and nothing here
 * starts an interval to guess.
 */

/** How many candidates a page asks for. The server clamps this; the number is here so it is one. */
const PAGE_SIZE = 25;

const ACTIVE: readonly OperationStatus[] = ['queued', 'running'];

/**
 * The four statuses a job will not move out of.
 *
 * Written out rather than inferred as "whatever is not active", because the list that decides a re-read
 * and the list that decides a signal have to be the same four words.
 */
const TERMINAL: readonly OperationStatus[] = ['succeeded', 'failed', 'cancelled', 'superseded'];

export interface CandidatesState {
  readonly candidates: readonly GitHubCandidate[];
  /** The stored total, which is not the page length. */
  readonly total: number;
  readonly subscription: GitHubWebhookSubscription | null;
  readonly loading: boolean;
  /** True when the project has no repository connected, so nothing can be proposed. */
  readonly connectionMissing: boolean;
  readonly failure: GitHubCandidateApiError | null;
  /** The one-shot token a terminal poll status raises; the panel passes it back to re-read. */
  readonly reloadKey: number;
  /** Asks the repository to be polled for releases and tags. */
  readonly poll: (since: string | null) => Promise<GitHubCandidateSyncAccepted | null>;
  /** Points the connection at a stored webhook secret, or clears the subscription when null. */
  readonly setSubscription: (credentialRef: string | null) => Promise<void>;
  /** Stops the connection accepting deliveries. */
  readonly stopListening: () => Promise<void>;
  /** True while one of those three calls is in flight. */
  readonly busy: boolean;
  /** The most recent action's outcome, in words, for the status line. */
  readonly notice: string | null;
}

export function useGitHubCandidates(projectId: string): CandidatesState {
  const [candidates, setCandidates] = useState<readonly GitHubCandidate[]>([]);
  const [total, setTotal] = useState(0);
  const [subscription, setSubscriptionState] = useState<GitHubWebhookSubscription | null>(null);
  const [loading, setLoading] = useState(true);
  const [connectionMissing, setConnectionMissing] = useState(false);
  const [failure, setFailure] = useState<GitHubCandidateApiError | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  // The revision the last read published, so a write is conditional on the project as it was read and
  // not on a number this panel invented. A stale one is a 409, which is the delivered answer for a
  // project somebody else moved.
  const revision = useRef(0);

  // The id of the poll this panel is following, or null. Only an id, never the 202 body: the body is
  // what the server said at the moment of acceptance, and the queue's own state has to be read from the
  // delivered operation route rather than remembered from a response.
  const following = useRef<string | null>(null);

  const read = useCallback(async () => {
    const [page, stored] = await Promise.all([
      readCandidates(projectId, PAGE_SIZE),
      readSubscription(projectId),
    ]);

    setCandidates(page.items);
    setTotal(page.count);
    setSubscriptionState(stored);
    setConnectionMissing(false);
    setFailure(null);
  }, [projectId]);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    // The project is read first, for the revision the writes below are conditional on. It is a read the
    // panel needs anyway - it is what a 409 would be about - and reading it here rather than trusting the
    // page above means this panel is correct even where it is mounted on its own.
    getProject(projectId)
      .then((project) => {
        if (!active) {
          return;
        }

        revision.current = project.revision;
        return read();
      })
      .catch((thrown: unknown) => {
        if (!active || isAbort(thrown)) {
          return;
        }

        const failure = asFailure(thrown);
        setFailure(failure);
        setConnectionMissing(failure.isConnectionMissing);
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
  }, [projectId, read, reloadKey]);

  // The accepted job is re-read while it can still change, and its terminal status raises the reload token
  // exactly once. That is the only automatic re-read in this panel, and it is driven by the job this
  // panel created rather than by an interval that would be guessing at GitHub's activity.
  useEffect(() => {
    const operationId = following.current;
    if (operationId === null) {
      return;
    }

    let active = true;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const follow = async (id: string): Promise<void> => {
      try {
        const answer: Operation = await getOperation(id);

        if (!active) {
          return;
        }

        if (TERMINAL.includes(answer.status)) {
          following.current = null;
          setNotice(
            answer.status === 'succeeded'
              ? 'The poll finished. Anything new is in the list below.'
              : 'The poll did not finish. Nothing was proposed, and the reason is on the job.',
          );
          setReloadKey((value) => value + 1);
          return;
        }

        if (ACTIVE.includes(answer.status)) {
          timer = setTimeout(() => void follow(id), 2000);
        }
      } catch (thrown) {
        if (!active || isAbort(thrown)) {
          return;
        }

        following.current = null;
        setFailure(asFailure(thrown));
      }
    };

    void follow(operationId);

    return () => {
      active = false;
      if (timer !== undefined) {
        clearTimeout(timer);
      }
    };
  }, [reloadKey]);

  const poll = useCallback(
    async (since: string | null): Promise<GitHubCandidateSyncAccepted | null> => {
      setBusy(true);
      setNotice(null);

      try {
        const accepted = await requestSync(projectId, {
          since,
          revision: revision.current,
          // A key per press, derived rather than random, so pressing twice creates two jobs and a
          // double-click does not silently collapse into one - and so a replay of the same press is
          // recognisable as the same request.
          idempotencyKey: `candidates-${since ?? 'all'}-${revision.current}`,
        });

        following.current = accepted.operationId;
        setNotice(
          accepted.created
            ? 'The poll was accepted. Its progress is on the job.'
            : 'That same poll was already asked for, and this is the job that will do it.',
        );

        return accepted;
      } catch (thrown) {
        const failure = asFailure(thrown);
        setFailure(failure);
        setConnectionMissing(failure.isConnectionMissing);
        setNotice(
          failure.isRevisionConflict
            ? 'This project changed since this panel read it. Nothing was asked for; the list has been read again.'
            : null,
        );

        if (failure.isRevisionConflict) {
          setReloadKey((value) => value + 1);
        }

        return null;
      } finally {
        setBusy(false);
      }
    },
    [projectId],
  );

  const setSubscription = useCallback(
    async (credentialRef: string | null) => {
      setBusy(true);
      setNotice(null);

      try {
        const saved = await subscribe(projectId, credentialRef, revision.current);
        setSubscriptionState(saved);
        setNotice(
          saved.enabled
            ? 'This connection now accepts signed deliveries.'
            : 'This connection no longer accepts signed deliveries.',
        );
        setFailure(null);
      } catch (thrown) {
        const failure = asFailure(thrown);
        setFailure(failure);
        setConnectionMissing(failure.isConnectionMissing);
        setNotice(
          failure.isRevisionConflict
            ? 'This project changed since this panel read it. Nothing was saved; the panel has been read again.'
            : null,
        );

        if (failure.isRevisionConflict) {
          setReloadKey((value) => value + 1);
        }
      } finally {
        setBusy(false);
      }
    },
    [projectId],
  );

  const stopListening = useCallback(async () => {
    setBusy(true);
    setNotice(null);

    try {
      const stored = await unsubscribe(projectId, revision.current);
      setSubscriptionState(stored);
      setNotice('This connection no longer accepts signed deliveries.');
      setFailure(null);
    } catch (thrown) {
      const failure = asFailure(thrown);
      setFailure(failure);
      setNotice(
        failure.isRevisionConflict
          ? 'This project changed since this panel read it. Nothing was changed.'
          : null,
      );

      if (failure.isRevisionConflict) {
        setReloadKey((value) => value + 1);
      }
    } finally {
      setBusy(false);
    }
  }, [projectId]);

  return {
    candidates,
    total,
    subscription,
    loading,
    connectionMissing,
    failure,
    reloadKey,
    poll,
    setSubscription,
    stopListening,
    busy,
    notice,
  };
}

export interface WebhookSecretState {
  /**
   * The vault's identifier for the stored webhook secret, or null when none is stored.
   *
   * <b>An identifier, never a value.</b> The delivered credential route publishes whether a provider is
   * configured and its own id, and that is the whole of what this panel needs: it has to present a
   * reference the subscription route can check, and it has no use for the secret itself. Nothing in this
   * hook could read the value even if the server published it.
   */
  readonly id: string | null;
  readonly loading: boolean;
}

/**
 * Whether a webhook secret is stored, read through the delivered credential route.
 *
 * This is why the panel has no field a secret could be typed into. The secret is written once, in the
 * place secrets are written, and this hook only learns whether that happened. A panel that collected the
 * secret itself would be a second place a secret lives in a browser, and the whole point of the delivered
 * vault route is that there is one.
 */
export function useWebhookSecret(): WebhookSecretState {
  const [id, setId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    readProvider('github-webhook', controller.signal)
      .then((state) => {
        if (active) {
          setId(state.configured ? state.id : null);
        }
      })
      .catch((thrown: unknown) => {
        // A read that fails leaves the button disabled, which is the safe direction: a panel that
        // enabled &ldquo;start listening&rdquo; on a failed read would be offering a subscription it cannot
        // configure.
        if (active && !isAbort(thrown)) {
          setId(null);
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
  }, []);

  return { id, loading };
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}

/**
 * Anything that is not this package's own refusal is still something to report, not something to swallow.
 *
 * A panel that rendered an unexpected error as an empty list would be the same class of failure this
 * repository has been bitten by before: a green panel over a request that never succeeded.
 */
function asFailure(thrown: unknown): GitHubCandidateApiError {
  if (thrown instanceof GitHubCandidateApiError) {
    return thrown;
  }

  return new GitHubCandidateApiError(
    {
      code: 'internal_error',
      message: 'The request could not be completed.',
      traceId: '',
    },
    0,
  );
}
