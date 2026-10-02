/**
 * The wire shapes of the operation routes, as the server actually sends them.
 *
 * The status is a closed union of exactly the six persisted words, and it is a union rather than a
 * `string` so that a server which started sending a seventh status would be a compile error here
 * rather than a sentence the panel renders verbatim. That is the point of the type: the panel cannot
 * invent a status, and it cannot quietly pass one through that it has no wording for.
 */
export type OperationStatus =
  | 'queued'
  | 'running'
  | 'succeeded'
  | 'failed'
  | 'cancelled'
  | 'superseded';

/** Every status, in the order the panel's filter offers them. */
export const OPERATION_STATUSES: readonly OperationStatus[] = [
  'queued',
  'running',
  'succeeded',
  'failed',
  'cancelled',
  'superseded',
];

/** Only these two can still change, so only these two offer the owner an action. */
export const ACTIVE_OPERATION_STATUSES: readonly OperationStatus[] = ['queued', 'running'];

/** The delivered envelope's refusal shape. */
export interface ApiFailure {
  readonly code: string;
  readonly message: string;
  readonly traceId: string;
  readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
}

/**
 * One durable operation as the owner may see it.
 *
 * Every field here is something the server is willing to show. The lease token, the lease deadline,
 * the recovery count and the idempotency fingerprint are deliberately absent from the wire shape: they
 * are the worker's bookkeeping and the key's binding, and neither is the owner's business.
 */
export interface Operation {
  readonly id: string;
  readonly kind: string;
  readonly targetId: string;
  readonly inputRevision: number;
  readonly idempotencyKey: string;
  readonly status: OperationStatus;
  readonly attempt: number;
  readonly maxAttempts: number;
  /** The redacted, fixed-vocabulary failure code, or null while the operation has not failed. */
  readonly errorCode: string | null;
  /** Present exactly when the operation succeeded; both are committed in one statement. */
  readonly resultRef: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly heartbeatAt: string | null;
  readonly nextAttemptAt: string | null;
  readonly completedAt: string | null;
  readonly cancelRequestedAt: string | null;
  readonly cancellable: boolean;
}

export interface OperationPage {
  readonly items: readonly Operation[];
}
