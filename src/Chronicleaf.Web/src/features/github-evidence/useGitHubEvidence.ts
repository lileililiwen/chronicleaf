import { useCallback, useEffect, useRef, useState } from 'react';

import { getOperation } from '../background-jobs/api';
import type { Operation, OperationStatus } from '../background-jobs/types';
import { listEvidence } from '../milestone-evidence/api';
import {
  GitHubEvidenceApiError,
  connectRepository,
  readConnection,
  requestSync,
} from './api';
import type { GitHubConnection, GitHubSyncAccepted, GitHubSyncDraft } from './types';

/**
 * The state behind the two GitHub panels.
 *
 * Two hooks rather than one, because the two surfaces have genuinely different lifetimes: a repository
 * belongs to a project and is read once per project, while a collection belongs to a milestone and is
 * read again every few seconds because the evidence panel above it appends. Keeping them apart stops
 * a milestone's progress from re-reading a project's connection, and stops a project's connection from
 * dragging a collection's status with it.
 *
 * <b>The refresh is the point of this file.</b> The evidence panel above a milestone appends to that
 * milestone, and an append is how an owner most often adds the material a collection is meant to
 * gather more of. Reading the revision once on mount would leave this panel sending a revision the
 * owner is no longer looking at, and the server would refuse a collection the owner had every reason to
 * expect. So the revision is kept current on a short, bounded interval, and the progress of the job is
 * read from the delivered operations route rather than inferred from this component's own state.
 */

/**
 * How often a panel re-reads something another surface can change.
 *
 * One interval for both reads on purpose: a project's connection and a milestone's evidence head are
 * each written by a panel this one does not own, and they are read together often enough that a page
 * never shows two of them disagreeing for long.
 */
const REFRESH_MS = 4000;

/** How often an accepted collection's job is re-read while it can still change. */
const OPERATION_REFRESH_MS = 2000;

const ACTIVE: readonly OperationStatus[] = ['queued', 'running'];

/**
 * The other four of the delivered six: a status the queue will not move the job out of.
 *
 * Not inferred as "whatever is not active" at each place that needs it, because the list that decides
 * a re-read and the list that decides a signal have to be the same four words, and one of them
 * spelled from memory is a status nothing ever fires on.
 */
const TERMINAL: readonly OperationStatus[] = ['succeeded', 'failed', 'cancelled', 'superseded'];

export interface GitHubConnectionState {
  /** The connection as the server stores it, or null while none has been read or none exists. */
  readonly connection: GitHubConnection | null;
  readonly loading: boolean;
  /** A read that failed for a reason other than there being no connection. */
  readonly failure: GitHubEvidenceApiError | null;
  /**
   * True when the server said this project has no repository connected.
   *
   * An answer, not a failure: it is the state a new project is in, and a panel that rendered it as an
   * error would be telling an owner something is wrong before they had done anything.
   */
  readonly missing: boolean;
  readonly reload: () => void;

  readonly connecting: boolean;
  readonly connectFailure: GitHubEvidenceApiError | null;
  readonly connectNotice: string | null;
  connect: (input: {
    readonly owner: string;
    readonly repository: string;
    readonly credentialRef: string | null;
  }) => Promise<void>;
}

/**
 * Reads a project's repository connection and owns writing it.
 *
 * The connection is re-read after every successful write rather than patched from the response: the
 * stored row is the answer, including the default branch and whether a token is configured, and a
 * locally patched copy is a weaker claim than the one the server just made.
 */
export function useGitHubConnection(projectId: string): GitHubConnectionState {
  const [connection, setConnection] = useState<GitHubConnection | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<GitHubEvidenceApiError | null>(null);
  const [nonce, setNonce] = useState(0);

  const [connecting, setConnecting] = useState(false);
  const [connectFailure, setConnectFailure] = useState<GitHubEvidenceApiError | null>(null);
  const [connectNotice, setConnectNotice] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    // Held in this closure rather than read back out of state, so a background re-read cannot be the
    // thing that schedules the next one.
    let connected = false;

    setLoading(true);

    const load = (first: boolean) => {
      readConnection(projectId, controller.signal)
        .then((answer) => {
          if (!active) {
            return;
          }

          connected = true;
          setConnection(answer);
          setFailure(null);
        })
        .catch((thrown: unknown) => {
          if (!active || isAbort(thrown)) {
            return;
          }

          // "No connection" arrives as a refusal, because the server will not answer with an empty
          // connection that could be mistaken for one. It is turned into an absence here and nowhere
          // else, so every panel reads the same state the same way.
          if (thrown instanceof GitHubEvidenceApiError && thrown.isConnectionMissing) {
            setConnection(null);
            setFailure(null);
            return;
          }

          setConnection(null);
          setFailure(asFailure(thrown));
        })
        .finally(() => {
          // Only the first read owns the loading state: a background re-read must not put a panel that
          // is already showing something back into "Reading…".
          if (active && first) {
            setLoading(false);
          }
        });
    };

    load(true);

    // A project's connection is written by the panel in the project header and read by a panel under
    // every milestone, so on a page with a milestone list the write lands above these reads. Reading
    // once on mount left each milestone's panel claiming "no repository connected yet" beside a header
    // that had just connected one - a real browser run is what found it.
    //
    // The absence is re-read until it is gone and then the interval stops, which is not an optimisation
    // but a consequence: there is no delete route, because the collection history on a milestone is not
    // a thing this UI may throw away, so a connected project's connection cannot become unconnected and
    // nothing here has anything left to notice.
    const refresh = () => {
      if (!connected && document.visibilityState === 'visible') {
        load(false);
      }
    };

    const timer = window.setInterval(refresh, REFRESH_MS);
    window.addEventListener('focus', refresh);
    document.addEventListener('visibilitychange', refresh);

    return () => {
      active = false;
      controller.abort();
      window.clearInterval(timer);
      window.removeEventListener('focus', refresh);
      document.removeEventListener('visibilitychange', refresh);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, nonce]);

  const connect = useCallback(
    async (input: { owner: string; repository: string; credentialRef: string | null }) => {
      setConnecting(true);
      setConnectFailure(null);
      setConnectNotice(null);

      try {
        const saved = await connectRepository(
          projectId,
          {
            owner: input.owner,
            repository: input.repository,
            credentialRef: input.credentialRef,
          },
        );

        // Re-read rather than trusting the response to be the whole story: the panel that follows this
        // read is showing what is stored, and a milestone panel beside it must not have to trust that
        // the connect form above it was filled in correctly.
        setNonce((value) => value + 1);
        setConnectNotice(
          `This project now collects from ${saved.owner}/${saved.repository}. The milestones below ask ` +
            'it for a ref.',
        );
      } catch (thrown) {
        // Nothing was stored, so the connection is left exactly as it was and the refusal is shown.
        setConnectFailure(asFailure(thrown));
      } finally {
        setConnecting(false);
      }
    },
    [projectId],
  );

  return {
    connection,
    loading,
    failure,
    missing: connection === null && failure === null && !loading,
    reload: useCallback(() => setNonce((value) => value + 1), []),
    connecting,
    connectFailure,
    connectNotice,
    connect,
  };
}

export interface GitHubSyncState {
  /** The project's repository, or null while none is connected. */
  readonly connection: GitHubConnection | null;
  readonly connectionMissing: boolean;
  readonly connectionFailure: GitHubEvidenceApiError | null;

  /** The evidence revision a collection asked for, read from the server rather than remembered. */
  readonly evidenceRevision: number;
  readonly evidenceFailure: string | null;

  readonly busy: boolean;
  readonly failure: GitHubEvidenceApiError | null;
  /** The 202, kept so the panel can name the job it created. */
  readonly accepted: GitHubSyncAccepted | null;
  /** The job as the delivered operations route reports it, which is the only status this panel renders. */
  readonly operation: Operation | null;
  /** A refusal from the progress route, reported rather than swallowed. */
  readonly progressFailure: string | null;

  /**
   * The refs to collect. The evidence revision and the idempotency key are deliberately not
   * parameters: one is read here at the moment of the request, and the other belongs to this
   * press rather than to the component.
   */
  requestSync: (
    input: Omit<GitHubSyncDraft, 'expectedBundleRevision' | 'idempotencyKey'>,
  ) => Promise<void>;
}

/**
 * Everything one milestone's collection needs: what it would collect from, which bundle it would extend,
 * and how the job it created is doing.
 *
 * The evidence revision is deliberately not an argument. It is read here and sent from here, because a
 * revision supplied by a component is a revision somebody remembered, and the whole conditional-write
 * guarantee rests on that number being the one the owner is actually looking at.
 *
 * <b>onSettled is the outward signal, and it is a transition rather than a status.</b> A finished
 * collection has appended to the evidence, or has decided not to, and either way the server holds an
 * answer about this milestone that the panel above did not have a moment ago. This hook already polls
 * the operations route and is the only thing that knows when the job stopped being able to change, so
 * the signal is raised here rather than by a second timer: `onSettled` fires on the move into a
 * terminal status, once, and never again for that job. It fires for a failure, a cancellation and a
 * supersession too - a collection that appended nothing made no argument for a stale panel, and it is
 * the same code path either way.
 */
export function useGitHubSync(
  projectId: string,
  milestoneId: string,
  onSettled?: (operation: Operation) => void,
): GitHubSyncState {
  const project = useGitHubConnection(projectId);

  const [evidenceRevision, setEvidenceRevision] = useState(0);
  const [evidenceFailure, setEvidenceFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<GitHubEvidenceApiError | null>(null);
  const [accepted, setAccepted] = useState<GitHubSyncAccepted | null>(null);
  const [operation, setOperation] = useState<Operation | null>(null);
  const [progressFailure, setProgressFailure] = useState<string | null>(null);

  // The caller's callback, read through a ref rather than taken as a dependency. A parent that writes
  // a new function on every render must not be able to make one finished job announce itself again -
  // the effect below is keyed on the job's status alone and asks the ref what to call.
  const onSettledRef = useRef(onSettled);

  // The last status this panel observed, which is what makes the signal below a transition rather than
  // an observation. Null means nothing has been seen yet, and that is deliberately not a terminal
  // status: a job that is read once and is already finished has moved, and the panel that never saw it
  // running still needs to hear that it stopped.
  const observedStatus = useRef<OperationStatus | null>(null);

  const loadRevision = useCallback(async () => {
    try {
      const page = await listEvidence(milestoneId);
      setEvidenceRevision(page.revision);
      setEvidenceFailure(null);
    } catch (thrown) {
      setEvidenceFailure(
        thrown instanceof Error ? thrown.message : 'The evidence revision could not be read.',
      );
    }
  }, [milestoneId]);

  useEffect(() => {
    void loadRevision();
  }, [loadRevision]);

  useEffect(() => {
    setAccepted(null);
    setOperation(null);
    setProgressFailure(null);
    setFailure(null);
  }, [milestoneId]);

  // The evidence panel above this one appends to the same milestone, and an append is how an owner
  // most often adds the material a collection is meant to gather more of. Reading the revision once on
  // mount would leave this panel sending a revision the owner has moved past, so it keeps itself
  // current instead.
  //
  // Bounded and cheap rather than event-driven: the evidence belongs to another feature's panel, this
  // one does not own that state, and the interval is short, stops while the tab is hidden, and is torn
  // down with the panel. It stops at the same moment the panel does, so no request outlives the
  // milestone it was reading.
  useEffect(() => {
    const refresh = () => {
      if (document.visibilityState === 'visible') {
        void loadRevision();
      }
    };

    const timer = window.setInterval(refresh, REFRESH_MS);
    window.addEventListener('focus', refresh);
    document.addEventListener('visibilitychange', refresh);

    return () => {
      window.clearInterval(timer);
      window.removeEventListener('focus', refresh);
      document.removeEventListener('visibilitychange', refresh);
    };
  }, [loadRevision]);

  // Progress is read from the delivered operations route, never guessed from what this component did.
  // The 202 said the work was accepted; only the operation route knows whether it has run, failed,
  // waited, or become superseded, and a panel that reported its own optimism as progress would be
  // reporting nothing.
  //
  // The effect is keyed on whether the job can still change, not on the job itself. Depending on the
  // operation object would restart the poll on every read - each answer a new object - and turn a
  // bounded two-second poll into a loop that reads as fast as the route answers.
  const watching = accepted !== null && (operation === null || ACTIVE.includes(operation.status));

  useEffect(() => {
    if (accepted === null || !watching) {
      return;
    }

    const controller = new AbortController();
    let active = true;

    const read = async () => {
      try {
        const current = await getOperation(accepted.operationId, controller.signal);

        if (!active) {
          return;
        }

        setOperation(current);
        setProgressFailure(null);
      } catch (thrown) {
        if (!active || isAbort(thrown)) {
          return;
        }

        setProgressFailure(
          thrown instanceof Error ? thrown.message : 'The collection could not be followed.',
        );
      }
    };

    void read();

    const timer = window.setInterval(() => {
      if (document.visibilityState === 'visible') {
        void read();
      }
    }, OPERATION_REFRESH_MS);

    return () => {
      active = false;
      controller.abort();
      window.clearInterval(timer);
    };
  }, [accepted, watching]);

  // A finished collection has moved the evidence head, whether it appended or not, so the revision the
  // next collection would send is re-read rather than left at the value that started this one.
  useEffect(() => {
    if (operation === null || ACTIVE.includes(operation.status)) {
      return;
    }

    void loadRevision();
  }, [loadRevision, operation]);

  // Declared above the effect that uses it, and that order is the mechanism: effects run in the order
  // they were declared within a commit, so by the time a terminal status is noticed the ref already
  // holds the callback this render was given.
  useEffect(() => {
    onSettledRef.current = onSettled;
  });

  // The move into a status the job will not leave, reported once.
  //
  // The poll above stops as soon as the status is terminal, so this usually runs a single time - but
  // "usually" is not a guarantee, and a panel left open across a second read of the same finished job
  // would otherwise re-read the evidence above it for the rest of the session. The previous status is
  // what separates the two: a terminal status that was already terminal is not a change, so nothing is
  // reported. A fresh request clears the observation, so each job is announced on its own.
  useEffect(() => {
    const previous = observedStatus.current;
    observedStatus.current = operation?.status ?? null;

    if (operation === null || !TERMINAL.includes(operation.status)) {
      return;
    }

    if (previous !== null && TERMINAL.includes(previous)) {
      return;
    }

    onSettledRef.current?.(operation);
  }, [operation]);

  const request = useCallback(
    async (input: Omit<GitHubSyncDraft, 'expectedBundleRevision' | 'idempotencyKey'>) => {
      setBusy(true);
      setFailure(null);

      // The revision is read at the moment of the request, not at the moment the panel mounted. A
      // panel that had been open across the owner's own note would otherwise ask the server to append
      // to a bundle that has already moved, and be refused for a request the owner could see was
      // reasonable.
      const revision = await readCurrentRevision(milestoneId);

      try {
        const accepted202 = await requestSync(milestoneId, {
          ...input,
          expectedBundleRevision: revision,
          // A fresh key per request, because a second press is a second collection and not a retry of
          // the first. The button is disabled while a request is in flight, so a key cannot be spent
          // twice by one click.
          idempotencyKey: crypto.randomUUID(),
        });

        setAccepted(accepted202);
        setOperation(null);
      } catch (thrown) {
        setAccepted(null);
        setOperation(null);
        setFailure(asFailure(thrown));

        // The refusal may be because the evidence moved, so the head is re-read and the form is left
        // against what is there now rather than against what it refused.
        await loadRevision();
      } finally {
        setBusy(false);
      }
    },
    [loadRevision, milestoneId],
  );

  return {
    connection: project.connection,
    connectionMissing: project.missing,
    connectionFailure: project.failure,
    evidenceRevision,
    evidenceFailure,
    busy,
    failure,
    accepted,
    operation,
    progressFailure,
    requestSync: request,
  };
}

/** One read of the evidence head, kept separate so the request path cannot silently use a stale value. */
async function readCurrentRevision(milestoneId: string): Promise<number> {
  try {
    return (await listEvidence(milestoneId)).revision;
  } catch {
    // A revision of zero is what the server itself publishes for a milestone with no evidence, and a
    // refusal to read it is better answered by the request than by hiding the form.
    return 0;
  }
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof Error && thrown.name === 'AbortError';
}

function asFailure(thrown: unknown): GitHubEvidenceApiError {
  if (thrown instanceof GitHubEvidenceApiError) {
    return thrown;
  }

  return new GitHubEvidenceApiError(
    {
      code: 'internal_error',
      message:
        thrown instanceof Error && thrown.message.length > 0
          ? thrown.message
          : 'The request could not be completed.',
      traceId: '',
    },
    0,
  );
}
