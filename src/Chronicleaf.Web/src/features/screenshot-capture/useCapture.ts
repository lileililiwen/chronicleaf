import { useCallback, useEffect, useRef, useState } from 'react';

import { listEvidence } from '../milestone-evidence/api';
import type { Operation, OperationStatus } from '../background-jobs/types';
import { listCandidates, newIdempotencyKey, readCaptureOperation, requestCapture } from './api';
import {
  ACTIVE_OPERATION_STATUSES,
  type CaptureAccepted,
  type CaptureApiError,
  type CaptureDraft,
  type ScreenshotCandidate,
} from './types';

/** How often the job is read while it can still change. Bounded, and torn down with the panel. */
const POLL_MS = 2_000;

/**
 * The capture panel's whole state, in one object.
 *
 * The three refusals are separate fields rather than one `error` on purpose, because they are three
 * different things an owner can act on and one of them is not a failure at all:
 *
 * - `readFailure` is this panel failing to read the candidates it already has.
 * - `submitFailure` is the server refusing the request that was made of it, and carries the code so
 *   the panel can distinguish a destination the policy will never allow from a malformed field.
 * - `progressFailure` is the panel losing sight of a job it had already started, which is a different
 *   problem from the job failing and must not be drawn as the job failing.
 */
export interface CaptureState {
  readonly candidates: readonly ScreenshotCandidate[];
  readonly mediaBaseUrl: string;
  /** The evidence revision this panel last read from the delivered route. */
  readonly bundleRevision: number;
  /** The revision the last request carried, or null when nothing has been requested yet. */
  readonly requestedRevision: number | null;
  readonly reading: boolean;
  readonly readFailure: string | null;
  readonly busy: boolean;
  readonly submitFailure: CaptureApiError | null;
  /** The job the last accepted request created, or replayed. */
  readonly accepted: CaptureAccepted | null;
  readonly operation: Operation | null;
  readonly progressFailure: string | null;
  readonly load: () => Promise<void>;
  readonly submit: (draft: Omit<CaptureDraft, 'expectedBundleRevision'>) => Promise<void>;
}

/**
 * Drives one milestone's capture panel.
 *
 * <b>The shape of the claim this hook makes.</b> A capture is bounded browser work in the delivered
 * queue. Asking for one is a 202 and a job id, never a picture, and the only honest report of what
 * happened is that job's own terminal status read back from the delivered operations route. So this
 * hook never decides a capture finished: it follows the job, and when the job is terminal it re-reads
 * the candidates and lets the server say whether there are any.
 *
 * <b>On a host that cannot confine a browser.</b> The job fails with `capture_sandbox_unavailable` and
 * no browser is started, and that is the design's answer rather than a defect to be retried. This hook
 * reports it as a refusal the owner cannot clear from a browser rather than as a failed capture, and it
 * never retries it: there is no setting on this page that would change the answer, so a retry would
 * only look like progress.
 */
export function useCapture(milestoneId: string): CaptureState {
  const [candidates, setCandidates] = useState<readonly ScreenshotCandidate[]>([]);
  const [mediaBaseUrl, setMediaBaseUrl] = useState('/api/media');
  const [bundleRevision, setBundleRevision] = useState(0);
  // The revision the last request actually carried, so a 409 can name the revision the server refused
  // rather than whatever this component read when it mounted.
  const [requestedRevision, setRequestedRevision] = useState<number | null>(null);
  const [reading, setReading] = useState(true);
  const [readFailure, setReadFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [submitFailure, setSubmitFailure] = useState<CaptureApiError | null>(null);
  const [accepted, setAccepted] = useState<CaptureAccepted | null>(null);
  const [operation, setOperation] = useState<Operation | null>(null);
  const [progressFailure, setProgressFailure] = useState<string | null>(null);

  // The revision this panel last read, held so a refusal can name the revision the server was actually
  // asked about rather than the one this component happened to mount with. The revision a request
  // carries is read fresh at submit time, below, so the evidence panel above this one appending to the
  // same milestone cannot make a capture be asked for against a bundle that has already moved.
  const loadRevision = useCallback(async () => {
    try {
      const page = await listEvidence(milestoneId);
      setBundleRevision(page.revision);
    } catch (thrown) {
      // A revision that cannot be read is not a reason to refuse a capture here: the request reads it
      // again at the moment it is made, and a read that fails there surfaces as the request's own
      // failure rather than being swallowed into the candidate list.
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The evidence revision could not be read.',
      );
    }
  }, [milestoneId]);

  const load = useCallback(async () => {
    setReading(true);

    try {
      const page = await listCandidates(milestoneId);
      setCandidates(page.items);
      setMediaBaseUrl(page.mediaBaseUrl);
      setReadFailure(null);
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The captured screenshots could not be read.',
      );
    } finally {
      setReading(false);
    }

    await loadRevision();
  }, [milestoneId, loadRevision]);

  useEffect(() => {
    void load();
  }, [load]);

  // A different milestone is a different subject: nothing about the last capture describes this one,
  // and leaving a finished job on screen would be reading as though it belonged here.
  useEffect(() => {
    setAccepted(null);
    setOperation(null);
    setProgressFailure(null);
    setSubmitFailure(null);
    setRequestedRevision(null);
  }, [milestoneId]);

  // The last status this panel observed, which is what makes the signal below a transition rather than
  // an observation. Null means nothing has been seen yet, and that is deliberately not a terminal
  // status: a job that is read once and is already finished has moved, and a panel that never saw it
  // running still needs to hear that it stopped - and to re-read, which is the whole point of arriving
  // at the finished state without having watched the work happen.
  const observedStatus = useRef<OperationStatus | null>(null);

  // Keyed on whether the job can still change, not on the job itself. Depending on the operation object
  // would restart the poll on every read - each answer a new object - and turn a bounded two-second poll
  // into a loop that reads as fast as the route answers.
  const watching = accepted !== null && (operation === null || isActive(operation.status));

  useEffect(() => {
    if (accepted === null || !watching) {
      return;
    }

    const controller = new AbortController();
    let alive = true;

    const read = async () => {
      try {
        const current = await readCaptureOperation(accepted.operationId, controller.signal);

        if (!alive) {
          return;
        }

        setOperation(current);
        setProgressFailure(null);
      } catch (thrown) {
        if (!alive || isAbort(thrown)) {
          return;
        }

        setProgressFailure(
          thrown instanceof Error ? thrown.message : 'The capture job could not be followed.',
        );
      }
    };

    void read();

    const timer = window.setInterval(() => {
      if (document.visibilityState === 'visible') {
        void read();
      }
    }, POLL_MS);

    return () => {
      alive = false;
      controller.abort();
      window.clearInterval(timer);
    };
  }, [accepted, watching]);

  // The move into a status the job will not leave, acted on once.
  //
  // The poll above stops as soon as the status is terminal, so this usually runs a single time - but
  // "usually" is not a guarantee, and a panel left open across a second read of the same finished job
  // would otherwise re-read the candidates for the rest of the session. The previous status is what
  // separates the two: a terminal status that was already terminal is not a change. A fresh request
  // clears the observation, so each job is acted on in its own right.
  //
  // The candidates are re-read from the server rather than inferred from the status, so a job that
  // failed publishes nothing and the panel says so because the table is empty - not because a message
  // claims it.
  useEffect(() => {
    const previous = observedStatus.current;
    observedStatus.current = operation?.status ?? null;

    if (operation === null || isActive(operation.status)) {
      return;
    }

    if (previous !== null && !isActive(previous)) {
      return;
    }

    void load();
  }, [load, operation]);

  const submit = useCallback(
    async (draft: Omit<CaptureDraft, 'expectedBundleRevision'>) => {
      setBusy(true);
      setSubmitFailure(null);
      setProgressFailure(null);
      observedStatus.current = null;

      try {
        // The revision is read at the moment of the request, not the one this panel mounted with. The
        // evidence panel above this one appends to the same milestone, and a panel that had been open
        // across the owner's own note would otherwise ask the server to capture against a bundle that
        // has already moved - and be refused for a request the owner could see was reasonable.
        const revision = await readCurrentRevision(milestoneId);
        setRequestedRevision(revision);

        const answer = await requestCapture(
          milestoneId,
          { ...draft, expectedBundleRevision: revision },
          newIdempotencyKey(),
        );

        setAccepted(answer);
        setOperation(null);
      } catch (thrown) {
        if (thrown instanceof Error && thrown.name === 'CaptureApiError') {
          setSubmitFailure(thrown as CaptureApiError);
        } else {
          setSubmitFailure(null);
          setProgressFailure(
            thrown instanceof Error ? thrown.message : 'The capture could not be requested.',
          );
        }
      } finally {
        setBusy(false);
      }
    },
    [milestoneId],
  );

  return {
    candidates,
    mediaBaseUrl,
    bundleRevision,
    requestedRevision,
    reading,
    readFailure,
    busy,
    submitFailure,
    accepted,
    operation,
    progressFailure,
    load,
    submit,
  };
}

function isActive(status: OperationStatus): boolean {
  return (ACTIVE_OPERATION_STATUSES as readonly OperationStatus[]).includes(status);
}

/**
 * The evidence revision a capture is about, read at the moment it is asked for.
 *
 * Not the value this panel is holding: the evidence panel above this one appends to the same
 * milestone, so a panel left open across the owner's own note is holding a revision the server has
 * moved past. Reading it here means the request is asked for against what the server currently
 * believes, and a stale one is refused by the server rather than discovered here.
 */
async function readCurrentRevision(milestoneId: string): Promise<number> {
  const page = await listEvidence(milestoneId);

  return page.revision;
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}
