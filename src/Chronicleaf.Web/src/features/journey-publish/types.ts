/**
 * The wire shapes of the project journey routes, read from the
 * public-project-journey contracts rather than guessed.
 *
 * A preview computes the hash publication must carry; a publication freezes
 * the snapshot only after every approval passes. The public projection serves
 * the frozen snapshot only while every binding still describes the stored
 * state - and carries only explicitly published milestones and entries: no
 * private note, no candidate, no unselected picture and no credential ever
 * reaches it.
 */
import type { ApiFailure } from '../background-jobs/types';

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class JourneyApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(`${failure.code}: ${failure.message}`);
    this.name = 'JourneyApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }
}

/** One previewed picture, exactly as the preview route answers it. */
export interface JourneyPreviewPicture {
  readonly mediaId: string;
  readonly caption: string;
}

/** One previewed entry, exactly as the preview route answers it. */
export interface JourneyPreviewEntry {
  readonly assetId: string;
  readonly milestoneId: string;
  readonly milestoneTitle: string;
  readonly occurredAt: string;
  readonly contentRevision: number;
  readonly bodyHash: string;
  readonly evidenceRevision: number;
  readonly pictures: readonly JourneyPreviewPicture[];
}

/** One previewed selection, as the preview route answers it. */
export interface JourneyPreview {
  readonly selectionHash: string;
  readonly entries: readonly JourneyPreviewEntry[];
}

/** One journey, as the private routes answer it. */
export interface JourneyState {
  readonly projectId: string;
  readonly slug: string;
  readonly enabled: boolean;
  readonly revision: number;
  readonly selectionHash: string;
  readonly snapshotRef: string;
  readonly revokedAt: string | null;
  readonly entryCount: number;
}

/** What an owner sends when they publish, republish or revoke a journey. */
export interface JourneyPublish {
  readonly enabled: boolean;
  readonly slug: string;
  readonly expectedRevision: number | null;
  readonly selectionHash: string;
  readonly approvedAssetIds: readonly string[];
}

/** One published picture, as the anonymous public route serves it. */
export interface JourneyPublicPicture {
  readonly caption: string;
  readonly mediaUrl: string;
}

/** One published entry, as the anonymous public route serves it. */
export interface JourneyPublicEntry {
  readonly milestoneTitle: string;
  readonly occurredAt: string;
  readonly bodyMarkdown: string;
  readonly pictures: readonly JourneyPublicPicture[];
}

/** One journey, as the anonymous public route serves it. */
export interface JourneyPublic {
  readonly slug: string;
  readonly projectName: string;
  readonly revision: number;
  readonly entries: readonly JourneyPublicEntry[];
}
