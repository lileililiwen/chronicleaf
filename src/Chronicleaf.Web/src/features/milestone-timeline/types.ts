/**
 * The private chronological project timeline (MILESTONE-TIMELINE-001/002).
 *
 * A timeline row is a milestone the owner already has: the id is the milestone's own id, so
 * selecting a row opens the milestone editor for exactly that record. Timestamps arrive as UTC
 * ISO-8601 and are shown in the reader's own zone, because the API speaks UTC and the browser
 * knows the locale.
 */

/** One row of the timeline: what happened, when, and whether the owner confirmed it. */
export interface TimelineItem {
  readonly id: string;
  readonly title: string;
  readonly version: string | null;
  readonly occurredAt: string;
  readonly type: string;
  readonly status: 'confirmed' | 'candidate';
}

/** One page of the timeline, oldest first, plus the cursor for the next page. */
export interface TimelinePage {
  readonly items: readonly TimelineItem[];
  readonly nextCursor: string | null;
}

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export interface ApiFailure {
  readonly code: string;
  readonly message: string;
  readonly traceId: string;
  readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
}

/** A refusal from the timeline route, keeping the code, the status and the field errors. */
export class TimelineApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'TimelineApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }
}
