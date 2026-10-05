import { useCallback, useEffect, useRef, useState } from 'react';

import { getOperation } from '../background-jobs/api';
import type { Operation, OperationStatus } from '../background-jobs/types';
import { listEvidence } from '../milestone-evidence/api';
import { getProject } from '../projects-and-milestones/api';
import {
  LocalGitApiError,
  readCatalog,
  readRepository,
  registerRepository,
  requestSync,
} from './api';
import type { LocalGitSyncAccepted, LocalGitSyncDraft, LocalRepository, LocalRepositoryCatalog } from './types';

/**
 * The state behind the two local Git panels.
 *
 * Two hooks rather than one, for the delivered GitHub feature's reason: a registration belongs to a project
 * and is read once per project, while a collection belongs to a milestone and is read again every few
 * seconds because the evidence panel above it appends. Keeping them apart stops a milestone's progress from
 * re-reading a project's registration.
 *
 * <b>The catalogue is separate from both.</b> It is the operator's configuration rather than this owner's
 * record, it is the same for every project, and a picker built from it needs nothing else. It is read once
 * per mount and never re-read, because nothing in this UI changes it - there is no route that would.
 */

/** How often a panel re-reads something another surface can change. */
const REFRESH_MS = 4000;

/** How often an accepted collection's job is re-read while it can still change. */
const OPERATION_REFRESH_MS = 2000;

const ACTIVE: readonly OperationStatus[] = ['queued', 'running'];

/**
 * The other four of the delivered six: a status the queue will not move the job out of.
 *
 * Written out rather than inferred as "whatever is not active" at each place that needs it, because the list
 * that decides a re-read and the list that decides a signal have to be the same four words.
 */
const TERMINAL: readonly OperationStatus[] = ['succeeded', 'failed', 'cancelled', 'superseded'];

export interface LocalCatalogState {
  /** The registered roots, or null while the catalogue has not been read. */
  readonly catalog: LocalRepositoryCatalog | null;
  readonly loading: boolean;
  /** True when this deployment does not read local directories at all. */
  readonly unavailable: boolean;
  readonly failure: LocalGitApiError | null;
}

/**
 * The operator's registered roots, read once.
 *
 * <b>An empty list is an answer, not a failure.</b> A Community install whose operator registered nothing has
 * a catalogue with no entries, and a panel that rendered that as an error would be reporting a
 * misconfiguration as a broken product. `unavailable` is the separate case: a deployment that refuses local
 * access entirely, where the list is empty because the roots are not this caller's to be told about.
 */
export function useLocalCatalog(): LocalCatalogState {
  const [catalog, setCatalog] = useState<LocalRepositoryCatalog | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<LocalGitApiError | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    readCatalog(controller.signal)
      .then((answer) => {
        if (!active) {
          return;
        }

        setCatalog(answer);
        setFailure(null);
      })
      .catch((thrown: unknown) => {
        if (!active || isAbort(thrown)) {
          return;
        }

        setCatalog(null);
        setFailure(asFailure(thrown));
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

  return {
    catalog,
    loading,
    unavailable: catalog !== null && !catalog.available,
    failure,
  };
}

export interface LocalRepositoryState {
  /** The registration as the server stores it, or null while none has been read or none exists. */
  readonly repository: LocalRepository | null;
  readonly loading: boolean;
  /** A read that failed for a reason other than there being no registration. */
  readonly failure: LocalGitApiError | null;
  /**
   * True when the server said this project has no local repository registered.
   *
   * An answer, not a failure: it is the state a new project is in, and a panel that rendered it as an error
   * would be telling an owner something is wrong before they had done anything.
   */
  readonly missing: boolean;
  readonly reload: () => void;

  readonly registering: boolean;
  readonly registerFailure: LocalGitApiError | null;
  readonly registerNotice: string | null;
  register: (input: { readonly rootAlias: string; readonly relativePath: string }) => Promise<void>;
}

/**
 * Reads a project's local repository registration and owns writing it.
 *
 * The registration is re-read after every successful write rather than patched from the response: the stored
 * row is the answer, including the branch and the revision HEAD resolved to, and a locally patched copy is a
 * weaker claim than the one the server just made.
 */
export function useLocalRepository(projectId: string): LocalRepositoryState {
  const [repository, setRepository] = useState<LocalRepository | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<LocalGitApiError | null>(null);
  const [nonce, setNonce] = useState(0);

  const [registering, setRegistering] = useState(false);
  const [registerFailure, setRegisterFailure] = useState<LocalGitApiError | null>(null);
  const [registerNotice, setRegisterNotice] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;
    let registered = false;

    setLoading(true);

    const load = (first: boolean) => {
      readRepository(projectId, controller.signal)
        .then((answer) => {
          if (!active) {
            return;
          }

          registered = true;
          setRepository(answer);
          setFailure(null);
        })
        .catch((thrown: unknown) => {
          if (!active || isAbort(thrown)) {
            return;
          }

          // "No registration" arrives as a refusal, because the server will not answer with an empty one that
          // could be mistaken for a real one. It is turned into an absence here and nowhere else, so every
          // panel reads the same state the same way.
          if (thrown instanceof LocalGitApiError && thrown.isRepositoryMissing) {
            setRepository(null);
            setFailure(null);
            return;
          }

          setRepository(null);
          setFailure(asFailure(thrown));
        })
        .finally(() => {
          // Only the first read owns the loading state: a background re-read must not put a panel that is
          // already showing something back into "Reading…".
          if (active && first) {
            setLoading(false);
          }
        });
    };

    load(true);

    // A project's registration is written by the panel in the project header and read by a panel under every
    // milestone, so on a page with a milestone list the write lands above these reads. The absence is
    // re-read until it is gone and then the interval stops - not an optimisation, a consequence: there is no
    // delete route, so a registered project's registration cannot become unregistered and nothing here has
    // anything left to notice.
    const refresh = () => {
      if (!registered && document.visibilityState === 'visible') {
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
  }, [projectId, nonce]);

  const register = useCallback(
    async (input: { readonly rootAlias: string; readonly relativePath: string }) => {
      setRegistering(true);
      setRegisterFailure(null);
      setRegisterNotice(null);

      try {
        // The project revision is read here rather than taken from a parent, so the write is conditional on
        // the state this panel actually looked at and a concurrent edit is refused rather than overwritten.
        const project = await readProjectRevision(projectId);
        await registerRepository(projectId, input, project);

        // Re-read rather than trusting the response to be the whole story: the milestone panel below reads
        // what is stored, and it must not have to trust that the form above it was filled in correctly.
        setNonce((value) => value + 1);
        setRegisterNotice(
          `This project now collects from ${input.rootAlias}/${input.relativePath}. The milestones below ask it for a ref.`,
        );
      } catch (thrown) {
        // Nothing was stored, so the registration is left exactly as it was and the refusal is shown.
        setRegisterFailure(asFailure(thrown));
      } finally {
        setRegistering(false);
      }
    },
    [projectId],
  );

  return {
    repository,
    loading,
    failure,
    missing: repository === null && failure === null && !loading,
    reload: useCallback(() => setNonce((value) => value + 1), []),
    registering,
    registerFailure,
    registerNotice,
    register,
  };
}

export interface LocalGitSyncState {
  /** The project's local repository, or null while none is registered. */
  readonly repository: LocalRepository | null;
  readonly repositoryMissing: boolean;
  readonly repositoryFailure: LocalGitApiError | null;

  /** The evidence revision a collection asked for, read from the server rather than remembered. */
  readonly evidenceRevision: number;
  readonly evidenceFailure: string | null;

  readonly busy: boolean;
  readonly failure: LocalGitApiError | null;
  /** The 202, kept so the panel can name the job it created. */
  readonly accepted: LocalGitSyncAccepted | null;
  /** The job as the delivered operations route reports it, the only status this panel renders. */
  readonly operation: Operation | null;
  /** A refusal from the progress route, reported rather than swallowed. */
  readonly progressFailure: string | null;

  /**
   * The refs to collect. The evidence revision and the idempotency key are deliberately not parameters: one is
   * read here at the moment of the request, and the other belongs to this press rather than to the component.
   */
  requestSync: (input: Omit<LocalGitSyncDraft, 'expectedBundleRevision' | 'idempotencyKey'>) => Promise<void>;
}

/**
 * Everything one milestone's collection needs: what it would collect from, which bundle it would extend, and
 * how the job it created is doing.
 *
 * The evidence revision is deliberately not an argument. It is read here and sent from here, because a
 * revision supplied by a component is a revision somebody remembered, and the whole conditional-write
 * guarantee rests on that number being the one the owner is actually looking at.
 *
 * <b>onSettled is the outward signal, and it is a transition rather than a status.</b> A finished collection
 * has appended to the evidence, or has decided not to, and either way the server holds an answer about this
 * milestone that the panel above did not have a moment ago. This hook already polls the operations route and is
 * the only thing that knows when the job stopped being able to change, so the signal is raised here rather
 * than by a second timer: `onSettled` fires on the move into a terminal status, once, and never again for that
 * job. It fires for a failure, a cancellation and a supersession too - a collection that appended nothing made
 * no argument for a stale panel, and it is the same code path either way.
 */
export function useLocalGitSync(
  projectId: string,
  milestoneId: string,
  onSettled?: (operation: Operation) => void,
): LocalGitSyncState {
  const project = useLocalRepository(projectId);

  const [evidenceRevision, setEvidenceRevision] = useState(0);
  const [evidenceFailure, setEvidenceFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<LocalGitApiError | null>(null);
  const [accepted, setAccepted] = useState<LocalGitSyncAccepted | null>(null);
  const [operation, setOperation] = useState<Operation | null>(null);
  const [progressFailure, setProgressFailure] = useState<string | null>(null);

  // The caller's callback, read through a ref rather than taken as a dependency. A parent that writes a new
  // function on every render must not be able to make one finished job announce itself again.
  const onSettledRef = useRef(onSettled);

  // The last status this panel observed, which is what makes the signal below a transition rather than an
  // observation. Null means nothing has been seen yet, and that is deliberately not a terminal status: a job
  // that is read once and is already finished has moved, and a panel that never saw it running still needs to
  // hear that it stopped.
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

  // The evidence panel above this one appends to the same milestone, and an append is how an owner most often
  // adds the material a collection is meant to gather more of. Reading the revision once on mount would leave
  // this panel sending a revision the owner has moved past, so it keeps itself current instead - bounded and
  // cheap, stopping while the tab is hidden and torn down with the panel.
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

  // Progress is read from the delivered operations route, never guessed from what this component did. The
  // effect is keyed on whether the job can still change, not on the job itself: depending on the operation
  // object would restart the poll on every read and turn a bounded poll into a loop.
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

  // A finished collection has moved the evidence head, whether it appended or not, so the revision the next
  // collection would send is re-read rather than left at the value that started this one.
  useEffect(() => {
    if (operation === null || ACTIVE.includes(operation.status)) {
      return;
    }

    void loadRevision();
  }, [loadRevision, operation]);

  // Declared above the effect that uses it, and that order is the mechanism: effects run in the order they
  // were declared within a commit, so by the time a terminal status is noticed the ref already holds the
  // callback this render was given.
  useEffect(() => {
    onSettledRef.current = onSettled;
  });

  // The move into a status the job will not leave, reported once. The poll above stops as soon as the status
  // is terminal, so this usually runs a single time - but "usually" is not a guarantee, and the previous
  // status is what separates the two: a terminal status that was already terminal is not a change.
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
    async (input: Omit<LocalGitSyncDraft, 'expectedBundleRevision' | 'idempotencyKey'>) => {
      setBusy(true);
      setFailure(null);

      // The revision is read at the moment of the request, not at the moment the panel mounted. A panel that
      // had been open across the owner's own note would otherwise ask the server to append to a bundle that
      // has already moved, and be refused for a request the owner could see was reasonable.
      const revision = await readCurrentRevision(milestoneId);

      try {
        const accepted202 = await requestSync(milestoneId, {
          ...input,
          expectedBundleRevision: revision,
          // A fresh key per request, because a second press is a second collection and not a retry of the
          // first. The button is disabled while a request is in flight, so a key cannot be spent twice.
          idempotencyKey: crypto.randomUUID(),
        });

        setAccepted(accepted202);
        setOperation(null);
      } catch (thrown) {
        setAccepted(null);
        setOperation(null);
        setFailure(asFailure(thrown));

        // The refusal may be because the evidence moved, so the head is re-read and the form is left against
        // what is there now rather than against what it refused.
        await loadRevision();
      } finally {
        setBusy(false);
      }
    },
    [loadRevision, milestoneId],
  );

  return {
    repository: project.repository,
    repositoryMissing: project.missing,
    repositoryFailure: project.failure,
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
    // A revision of zero is what the server itself publishes for a milestone with no evidence, and a refusal
    // to read it is better answered by the request than by hiding the form.
    return 0;
  }
}

/**
 * The project's own revision, read through the delivered projects client rather than by a raw fetch.
 *
 * The registration is conditional on the project revision, so the number has to be the one the server holds
 * now rather than one a parent happened to have. Reading it here is what makes the write compare-and-set.
 */
async function readProjectRevision(projectId: string): Promise<number> {
  const project = await getProject(projectId);

  return project.revision;
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof Error && thrown.name === 'AbortError';
}

function asFailure(thrown: unknown): LocalGitApiError {
  if (thrown instanceof LocalGitApiError) {
    return thrown;
  }

  return new LocalGitApiError(
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
