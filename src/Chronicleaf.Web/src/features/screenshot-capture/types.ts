/**
 * The wire shapes of the two capture routes, read from the server's own contracts rather than guessed.
 *
 * Nothing here is a secret and nothing here holds a browser. `ScreenshotCandidate` is a stored image
 * with its provenance - the URL that was asked for, the URL the page ended up at, the viewport, the
 * dimensions and when it was captured - and `captureAccepted` is a pointer at a queued job, never a
 * picture. The panel is written so that the distance between those two things is the panel's whole
 * subject: a capture is work somebody has to wait for, and until it has run there is no candidate.
 *
 * `status`, `caption` and `isPrimary` are carried even though this package always writes
 * `candidate`, the empty string and `false`. Screenshot-curation is the next feature to read these
 * rows, and a client that could not tell "offered" from "chosen" would have to change to show that
 * difference later. They are here so the difference can be rendered from the moment it exists.
 */
import type { ApiFailure, Operation } from '../background-jobs/types';

/** One captured page, exactly as the server publishes it. */
export interface ScreenshotCandidate {
  readonly id: string;
  readonly mediaId: string;
  readonly projectId: string;
  /** The URL the owner asked to photograph. */
  readonly sourceUrl: string;
  /** Where that URL ended up, redirects included. Differs from `sourceUrl` whenever a page redirected. */
  readonly finalUrl: string;
  readonly viewport: string;
  readonly width: number;
  readonly height: number;
  readonly capturedAt: string;
  /** Always `candidate` today. */
  readonly status: string;
  /** Always the empty string today; captioning belongs to curation. */
  readonly caption: string;
  /** Always false today; one primary is curation's constraint to hold. */
  readonly isPrimary: boolean;
  readonly revision: number;
}

/** The candidate list, as an object so an empty milestone is a renderable answer and not a failure. */
export interface ScreenshotCandidatePage {
  readonly items: readonly ScreenshotCandidate[];
  /** The delivered media route the bytes are served from, published by the server rather than built here. */
  readonly mediaBaseUrl: string;
}

/** The 202 a capture request answers with: a job to follow, and nothing else. */
export interface CaptureAccepted {
  readonly operationId: string;
  readonly operationUrl: string;
  readonly status: string;
  /** False when this request replayed a job an earlier identical request already created. */
  readonly created: boolean;
}

/** What the owner asked for, before it becomes a job. */
export interface CaptureDraft {
  readonly url: string;
  readonly viewports: readonly string[];
  readonly waitSelector: string | null;
  readonly waitMs: number | null;
  readonly fullPage: boolean;
  /** The evidence revision this capture is about, sent in the body and as If-Match. */
  readonly expectedBundleRevision: number;
}

/** The bounds the server enforces, restated for the form so the UI never promises what the server refuses. */
export interface CaptureBounds {
  readonly maximumPages: number;
  readonly maximumViewports: number;
  readonly maximumWaitMs: number;
  readonly maximumSelectorLength: number;
}

export const CAPTURE_BOUNDS: CaptureBounds = {
  maximumPages: 3,
  maximumViewports: 2,
  maximumWaitMs: 10_000,
  maximumSelectorLength: 512,
};

export const CAPTURE_VIEWPORTS = [
  { name: 'desktop', label: 'Desktop', size: '1440 × 900' },
  { name: 'mobile', label: 'Mobile', size: '390 × 844' },
] as const;

/**
 * A refusal from the API, carrying the delivered envelope rather than a flattened message.
 *
 * The two codes this feature can be refused with are answers about the environment rather than about
 * something the owner typed, and they are named here because each one means "no browser ran" and the
 * panel has to say so in words rather than showing a spinner that stopped.
 */
export class CaptureApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'CaptureApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /**
   * The destination is not one a capture may reach, so nothing was requested and no job exists.
   *
   * This is the URL policy refusing at request time. The server's message deliberately names no host
   * and no range, so the panel says what was refused and stops there rather than asking for the
   * address back - there is nothing it could tell the owner that the owner did not just type.
   */
  get isTargetBlocked(): boolean {
    return this.code === 'capture_target_blocked';
  }

  /** Somebody else moved the evidence this capture was requested against. */
  get isRevisionConflict(): boolean {
    return this.status === 409;
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}

/**
 * What a finished capture job means, read from the delivered operation route.
 *
 * A capture is bounded browser work in the delivered queue, so the honest report of what happened is
 * the job's own terminal status and its stored error code. The two codes below are the ones a reader
 * of this panel must be able to tell apart from each other, because only one of them is a retry and
 * neither of them produced a picture.
 */
export const CAPTURE_JOB_CODES = {
  /**
   * The host cannot confine a browser, so no browser was started and no page was visited.
   *
   * This is a refusal, not a failure of the page. There is no retry that helps and no setting the
   * owner can change from a browser, and the design treats a less-secure fallback as unavailable
   * rather than as an option - so the panel says the host cannot do this and stops.
   */
  sandboxUnavailable: 'capture_sandbox_unavailable',
  /** The destination was refused by the URL policy when the job re-checked it. */
  targetBlocked: 'capture_target_blocked',
  /** The page did not settle inside the job's time limit. */
  timeout: 'capture_timeout',
} as const;

/** True when the job stopped because this host cannot confine a browser. */
export function isSandboxUnavailable(operation: Operation | null): boolean {
  return operation?.errorCode === CAPTURE_JOB_CODES.sandboxUnavailable;
}

/** True when the job stopped because the URL policy refused a destination. */
export function isTargetBlocked(operation: Operation | null): boolean {
  return operation?.errorCode === CAPTURE_JOB_CODES.targetBlocked;
}

/** The operation statuses a job can still move out of, mirroring the sibling panels' own list. */
export const ACTIVE_OPERATION_STATUSES = ['queued', 'running', 'retrying'] as const;
