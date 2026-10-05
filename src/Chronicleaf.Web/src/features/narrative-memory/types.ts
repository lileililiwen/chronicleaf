/**
 * The wire shapes of the narrative routes, read from the server's own contracts rather than guessed.
 *
 * <b>What this module may hold.</b> The owner's own history lines with the evidence ids they cite,
 * and the generated summary with the input hash it is bound to. What it never holds is a
 * credential, a provider response, or a stored consent: a refresh is deterministic and reaches no
 * provider, and nothing here could reach one.
 *
 * <b>Entries and summaries are different facts.</b> An entry is something the owner wrote; a
 * summary is something a refresh concluded from the sources. This client renders the two in
 * separate sections, so a reader can never mistake a generated document for the owner's words.
 */

/** One narrative entry, as the server answers it. */
export interface NarrativeEntry {
  readonly id: string;
  readonly projectId: string;
  /** One of `origin`, `audience`, `technical-route`, `decision`, `problem`, `feedback`. */
  readonly kind: string;
  readonly occurredAt: string;
  readonly text: string;
  readonly sourceEvidenceIds: readonly string[];
  readonly revision: number;
  /** `private` entries reach consented outputs only when explicitly selected. */
  readonly visibility: 'private' | 'exportable';
}

/** One generated project summary, as the read route answers it. */
export interface ProjectSummary {
  readonly id: string;
  readonly projectId: string;
  readonly throughMilestoneId: string | null;
  readonly inputHash: string;
  readonly bodyJson: string;
  readonly revision: number;
  /** True when a source moved after this body was built. */
  readonly stale: boolean;
  readonly updatedAt: string;
}

/** One project's narrative: its explicit entries in chronological order and its summary, if any. */
export interface Narrative {
  readonly entries: readonly NarrativeEntry[];
  readonly nextCursor: string | null;
  readonly summary: ProjectSummary | null;
}

/** A refresh that was accepted into the durable queue. */
export interface NarrativeRefreshAccepted {
  readonly operationId: string;
  readonly operationUrl: string;
  readonly status: string;
  readonly created: boolean;
}

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class NarrativeApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(
    failure: {
      readonly code: string;
      readonly message: string;
      readonly traceId: string;
      readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
    },
    status: number,
  ) {
    super(failure.message);
    this.name = 'NarrativeApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }
}
