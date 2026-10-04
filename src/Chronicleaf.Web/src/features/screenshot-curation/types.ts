/**
 * The wire shapes of the curation routes, read from the server's own contracts rather than guessed.
 *
 * A curated picture is the same stored image the capture list publishes - the URL asked for, the
 * URL the page ended up at, the viewport, the dimensions and when it was captured - plus the
 * choice: whether it is offered, selected or rejected, the owner's plain-text caption, whether it
 * is the milestone's primary picture, and its position in the owner's explicit sequence.
 * `bundleRevision` is the evidence revision the change was published against, so the panel can
 * name what it acted on when the server answers 409.
 */
import type { ApiFailure } from '../background-jobs/types';
import type { ScreenshotCandidate } from '../screenshot-capture/types';

/** One captured picture with its curation state, exactly as the server publishes it. */
export type CuratedScreenshot = ScreenshotCandidate;

/** The curated list, as an object so an empty milestone is a renderable answer and not a failure. */
export interface CuratedScreenshotPage {
  readonly items: readonly CuratedScreenshot[];
  /** The delivered media route the bytes are served from, published by the server rather than built here. */
  readonly mediaBaseUrl: string;
}

/** What the owner changes about one picture, before the two revisions it was read against. */
export interface CurationEdit {
  readonly status?: 'candidate' | 'selected' | 'rejected';
  readonly caption?: string;
  readonly isPrimary?: boolean;
}

/** The answer to a curation PATCH: the picture as stored, and the evidence it stands against. */
export interface CuratedScreenshotResult {
  readonly id: string;
  readonly milestoneId: string;
  readonly mediaId: string;
  readonly projectId: string;
  readonly sourceUrl: string;
  readonly finalUrl: string;
  readonly viewport: string;
  readonly width: number;
  readonly height: number;
  readonly capturedAt: string;
  readonly status: string;
  readonly caption: string;
  readonly isPrimary: boolean;
  readonly displayOrder: number;
  readonly revision: number;
  readonly bundleRevision: number;
}

/** The answer to an order POST: the selected pictures in their stored order. */
export interface ScreenshotOrderResult {
  readonly milestoneId: string;
  readonly ids: readonly string[];
  readonly bundleRevision: number;
}

/** At most 1000 characters for a caption, matching the server's bound. */
export const CURATION_CAPTION_MAX_LENGTH = 1000;

/**
 * A refusal from the API, carrying the delivered envelope rather than a flattened message.
 *
 * The two revisions a curation carries are what make a 409 legible: somebody else - or this same
 * owner in another tab - moved the picture or the evidence first, and the answer is to read again
 * rather than to resend the same numbers.
 */
export class CurationApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'CurationApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** The picture or the evidence moved first. Read again, then decide again. */
  get isRevisionConflict(): boolean {
    return this.status === 409;
  }

  /** The request named something this package does not accept. */
  get isValidationFailure(): boolean {
    return this.status === 422;
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}
