import { useCallback, useEffect, useRef, useState } from 'react';

import type { Operation, OperationStatus } from '../background-jobs/types';
import type { AiProviderView, GenerationPreview } from '../llm-provider-contract/types';
import { authorize, newIdempotencyKey, readDraft, readDraftOperation, readEvidenceHead, readProviders, requestDraft } from './api';
import {
  ACTIVE_OPERATION_STATUSES,
  type ContentAsset,
  type DraftApiError,
} from './types';

/** How often the job is read while it can still change. Bounded, and torn down with the panel. */
const POLL_MS = 2_000;

/** One evidence item the owner can choose to ground a draft in. */
export interface DraftEvidenceOption {
  readonly id: string;
  readonly kind: string;
  readonly text: string;
  readonly availability: string;
  /** False for an item nothing was collected from, which cannot ground anything. */
  readonly usable: boolean;
}

/** What the owner typed, before it becomes a consent and then a job. */
export interface DraftRequestForm {
  readonly providerId: string;
  readonly model: string;
  readonly selectedEvidenceIds: readonly string[];
}

/**
 * The draft panel's whole state, in one object.
 *
 * The refusals are separate fields rather than one `error`, because they are three different things an
 * owner can act on:
 *
 * - `readFailure` is this panel failing to read the draft the milestone already has.
 * - `requestFailure` is the server refusing the request that was made of it, and carries the code so the
 *   panel can tell a milestone that is not confirmed from a selection with nothing collected in it.
 * - `progressFailure` is the panel losing sight of a job it had already started, which is a different
 *   problem from the job failing and must not be drawn as the job failing.
 */
export interface DraftState {
  readonly asset: ContentAsset | null;
  readonly providers: readonly AiProviderView[];
  readonly evidence: readonly DraftEvidenceOption[];
  readonly evidenceRevision: number;
  readonly reading: boolean;
  readonly readFailure: string | null;
  readonly busy: boolean;
  readonly requestFailure: DraftApiError | null;
  /** The authorization the last press of the button froze, shown so the owner can see what was sent. */
  readonly authorized: GenerationPreview | null;
  /** The job the last accepted request created, or replayed. */
  readonly accepted: { readonly operationId: string; readonly created: boolean } | null;
  readonly operation: Operation | null;
  readonly progressFailure: string | null;
  readonly load: () => Promise<void>;
  readonly request: (form: DraftRequestForm) => Promise<void>;
}

/**
 * Drives one milestone's grounded-draft panel.
 *
 * <b>The shape of the claim this hook makes.</b> A draft is two provider calls and two local validation
 * passes in the delivered queue, so asking for one is a 202 and a job id rather than a body. The only
 * honest report of what happened is that job's own terminal status read back from the delivered
 * operations route, so this hook never decides a draft finished: it follows the job, and when the job is
 * terminal it re-reads the asset and lets the server say what exists.
 *
 * <b>Nothing here decides whether a draft is trustworthy.</b> The grounding findings and the voice
 * findings are read from the server and rendered; neither is summarised into a verdict, and there is no
 * approval control, because a human approval is the review package's table bound to a revision and a body
 * hash and no browser button here could write it.
 */
export function useGroundedDraft(milestoneId: string): DraftState {
  const [asset, setAsset] = useState<ContentAsset | null>(null);
  const [providers, setProviders] = useState<readonly AiProviderView[]>([]);
  const [evidence, setEvidence] = useState<readonly DraftEvidenceOption[]>([]);
  const [evidenceRevision, setEvidenceRevision] = useState(0);
  const [reading, setReading] = useState(true);
  const [readFailure, setReadFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [requestFailure, setRequestFailure] = useState<DraftApiError | null>(null);
  const [authorized, setAuthorized] = useState<GenerationPreview | null>(null);
  const [accepted, setAccepted] = useState<{ operationId: string; created: boolean } | null>(null);
  const [operation, setOperation] = useState<Operation | null>(null);
  const [progressFailure, setProgressFailure] = useState<string | null>(null);

  const load = useCallback(async () => {
    setReading(true);

    try {
      const [draft, catalogue, head] = await Promise.all([
        readDraft(milestoneId),
        readProviders(),
        readEvidenceHead(milestoneId),
      ]);

      setAsset(draft);
      setProviders(catalogue.usable);
      setEvidenceRevision(head.revision);
      setEvidence(
        head.items.map((item) => ({
          id: item.id,
          kind: item.kind,
          text: item.text,
          availability: item.availability,
          usable: item.availability !== 'unavailable',
        })),
      );
      setReadFailure(null);
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The draft for this milestone could not be read.',
      );
    } finally {
      setReading(false);
    }
  }, [milestoneId]);

  useEffect(() => {
    void load();
  }, [load]);

  // A different milestone is a different subject: nothing about the last draft describes this one, and a
  // finished job left on screen would read as though it belonged here.
  useEffect(() => {
    setAccepted(null);
    setOperation(null);
    setProgressFailure(null);
    setRequestFailure(null);
    setAuthorized(null);
  }, [milestoneId]);

  // The last status this panel observed, which is what makes the signal below a transition rather than
  // an observation. Null means nothing has been seen yet, and a job that is read once and is already
  // finished has still moved.
  const observedStatus = useRef<OperationStatus | null>(null);

  // Keyed on whether the job can still change, not on the job itself: depending on the operation object
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
        const current = await readDraftOperation(accepted.operationId, controller.signal);

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
          thrown instanceof Error ? thrown.message : 'The draft job could not be followed.',
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

  // The move into a status the job will not leave, acted on once. The candidates are re-read from the
  // server rather than inferred from the status, so a job that failed publishes nothing and the panel
  // says so because the list is empty - not because a message claims it.
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

  const request = useCallback(
    async (form: DraftRequestForm) => {
      setBusy(true);
      setRequestFailure(null);
      setProgressFailure(null);
      setAuthorized(null);
      observedStatus.current = null;

      try {
        // The evidence revision is read at the moment of the request rather than the one this panel
        // mounted with: the evidence panel above this one appends to the same milestone, and a panel
        // left open across the owner's own note would otherwise authorize a bundle the server has already
        // moved past.
        const head = await readEvidenceHead(milestoneId);
        setEvidenceRevision(head.revision);

        // The consent handshake first, then the draft request carrying its binding. Both are refused for
        // different reasons and the panel shows whichever the server said, because collapsing them into
        // one "could not draft" is exactly the message that would leave an owner guessing.
        const preview = await authorize(milestoneId, {
          providerId: form.providerId,
          model: form.model,
          evidenceRevision: head.revision,
          selectedEvidenceIds: form.selectedEvidenceIds,
        });

        setAuthorized(preview);

        const answer = await requestDraft(milestoneId, preview, newIdempotencyKey());

        setAccepted({ operationId: answer.operationId, created: answer.created });
        setOperation(null);
      } catch (thrown) {
        if (thrown instanceof Error && thrown.name === 'DraftApiError') {
          setRequestFailure(thrown as DraftApiError);
        } else {
          setRequestFailure(null);
          setProgressFailure(
            thrown instanceof Error ? thrown.message : 'The draft could not be requested.',
          );
        }
      } finally {
        setBusy(false);
      }
    },
    [milestoneId],
  );

  return {
    asset,
    providers,
    evidence,
    evidenceRevision,
    reading,
    readFailure,
    busy,
    requestFailure,
    authorized,
    accepted,
    operation,
    progressFailure,
    load,
    request,
  };
}

function isActive(status: OperationStatus): boolean {
  return (ACTIVE_OPERATION_STATUSES as readonly OperationStatus[]).includes(status);
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}
