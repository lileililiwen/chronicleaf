/**
 * The wire shapes of the publisher-contract routes, read from the
 * publisher-contract contracts rather than guessed.
 *
 * A receipt names what was sent where and what the remote side answered; the
 * content itself stays behind the review package's own export, so this shape
 * never carries a body, a picture or a credential. A preview authorizes
 * nothing; a publish sends only what the stored rows still describe.
 */
import type { ApiFailure } from '../background-jobs/types';

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class PublicationApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(`${failure.code}: ${failure.message}`);
    this.name = 'PublicationApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }
}

/** One target preview, as the preview route answers it. */
export interface PublishPreview {
  readonly targetId: string;
  readonly mode: string;
  readonly assetId: string;
  readonly contentRevision: number;
  readonly bodyHash: string;
  readonly mediaPlan: readonly string[];
  readonly warnings: readonly string[];
  readonly previewHash: string;
}

/** One accepted publish: the durable operation the owner watches. */
export interface PublishAccepted {
  readonly operationId: string;
  readonly attemptId: string;
  readonly created: boolean;
}

/** One stored receipt, as the receipt and reconcile routes answer it. */
export interface PublicationReceipt {
  readonly attemptId: string;
  readonly operationId: string;
  readonly targetId: string;
  readonly assetId: string;
  readonly contentRevision: number;
  readonly bodyHash: string;
  readonly mode: string;
  readonly state: string;
  readonly remoteId: string | null;
  readonly remoteUrl: string | null;
  readonly errorCode: string | null;
  readonly reconcileHint: string | null;
  readonly created: boolean;
}

/** What the publish route reads: the confirmed preview and the explicit consent. */
export interface PublishRequest {
  readonly targetId: string;
  readonly mode: string;
  readonly previewHash: string;
  readonly approvalId: string;
  readonly idempotencyKey: string;
  readonly confirm: boolean;
}

/**
 * One classified publisher target, as the target list answers it.
 *
 * Classification, not transport: what the target id means. An export-only
 * target names material that leaves through its export package, carried by
 * hand - the pickers show it disabled and the server refuses it.
 */
export interface PublisherTarget {
  readonly targetId: string;
  readonly supportsDraft: boolean;
  readonly supportsPublish: boolean;
  readonly supportsReconcile: boolean;
  readonly exportOnly: boolean;
}

/** Every classified publisher target, in registry order. */
export interface PublisherTargetList {
  readonly targets: readonly PublisherTarget[];
}
