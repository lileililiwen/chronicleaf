import type { ApiFailure, Operation } from '../background-jobs/types';
import { CaptureApiError, type CaptureAccepted, type CaptureDraft, type ScreenshotCandidatePage } from './types';

/**
 * The only client of the capture routes in this repository.
 *
 * It follows the same rules as the delivered evidence, credential and operation clients: requests are
 * `same-origin`, so the browser attaches the session cookie and this code never sees it, and the
 * mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * <b>What this module may hold.</b> A URL an owner typed and the evidence revision it is about. It
 * receives a job pointer and a list of stored candidates. It never receives bytes: the pictures are
 * served by the delivered media route, whose base the server publishes in the list, so this client
 * has no way to hand out an image itself and no path where one could be invented.
 *
 * <b>What this module deliberately does not do.</b> It does not decide whether a URL may be captured.
 * The server applies its URL policy before a job exists and again to the address the name resolves to
 * immediately before every connection; a client-side guess would be a second policy that could only
 * ever agree with the first one, and the first one is the one that is enforced. So an unusable URL is
 * sent and refused, and the refusal is shown as the server's own answer.
 */

/** Reads the candidates a milestone has, newest first, through the delivered route. */
export async function listCandidates(
  milestoneId: string,
  signal?: AbortSignal,
): Promise<ScreenshotCandidatePage> {
  return send<ScreenshotCandidatePage>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/screenshots`,
    { method: 'GET', signal },
  );
}

/**
 * Asks for a capture and returns the job that will perform it.
 *
 * A 202 and an operation id, never a candidate. The work is bounded browser work with a time limit
 * attached and it happens in the delivered queue, so this call reports that the request was accepted
 * and nothing more - the panel follows the job through the delivered operation route rather than
 * deciding on its own that the capture finished.
 *
 * The evidence revision travels in the body and as `If-Match`, and the server refuses a body that
 * disagrees with the header rather than picking one. The idempotency key is the caller's: a repeat of
 * the same key returns the job the first request created instead of photographing the same pages
 * twice.
 */
export async function requestCapture(
  milestoneId: string,
  draft: CaptureDraft,
  idempotencyKey: string,
  signal?: AbortSignal,
): Promise<CaptureAccepted> {
  return send<CaptureAccepted>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/captures`,
    {
      method: 'POST',
      body: {
        urls: [...draft.urls],
        viewports: [...draft.viewports],
        waitSelector: emptyToNull(draft.waitSelector),
        waitMs: draft.waitMs,
        fullPage: draft.fullPage,
        expectedBundleRevision: draft.expectedBundleRevision,
        idempotencyKey,
      },
      revision: draft.expectedBundleRevision,
    },
    signal,
  );
}

/**
 * Reads one job from the delivered operations route.
 *
 * Progress is never guessed from what the panel did. The 202 said the work was accepted; only this
 * route knows whether it has run, failed, is waiting, or became superseded, and a panel that reported
 * its own optimism as progress would be reporting nothing.
 */
export async function readCaptureOperation(
  operationId: string,
  signal?: AbortSignal,
): Promise<Operation> {
  return send<Operation>(`/api/operations/${encodeURIComponent(operationId)}`, { method: 'GET', signal });
}

interface CaptureRequest {
  readonly method: 'GET' | 'POST';
  readonly body?: unknown;
  /** The evidence revision this request extends, sent as If-Match. */
  readonly revision?: number;
  readonly signal?: AbortSignal;
}

async function send<T>(path: string, request: CaptureRequest, signal?: AbortSignal): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Every route this client calls is behind the delivered owner boundary, and the capture route is the
  // one mutation among them. A fresh value per call, used once, exactly as the delivered features do.
  if (request.method !== 'GET') {
    const metadata = await readAntiforgeryValue();
    headers[metadata.headerName] = metadata.token;
  }

  if (request.revision !== undefined) {
    headers['If-Match'] = String(request.revision);
  }

  const response = await fetch(path, {
    method: request.method,
    credentials: 'same-origin',
    headers,
    body: request.body === undefined ? undefined : JSON.stringify(request.body),
    signal: signal ?? request.signal,
  });

  if (!response.ok) {
    throw new CaptureApiError(await toFailure(response), response.status);
  }

  return (await response.json()) as T;
}

async function readAntiforgeryValue(): Promise<{ headerName: string; token: string }> {
  const response = await fetch('/api/session/antiforgery', {
    method: 'GET',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new CaptureApiError(await toFailure(response), response.status);
  }

  return (await response.json()) as { headerName: string; token: string };
}

async function toFailure(response: Response): Promise<ApiFailure> {
  try {
    const body = (await response.json()) as Partial<ApiFailure>;

    return {
      code: typeof body.code === 'string' ? body.code : 'internal_error',
      message:
        typeof body.message === 'string' ? body.message : 'The request could not be completed.',
      traceId: typeof body.traceId === 'string' ? body.traceId : '',
      fieldErrors: body.fieldErrors,
    };
  } catch {
    // A body that is not the envelope still leaves a refusal to report; the status is the answer.
    return {
      code: 'internal_error',
      message: 'The request could not be completed.',
      traceId: '',
    };
  }
}

/**
 * An optional word the owner left blank becomes an absent field rather than an empty string.
 *
 * The server treats a null as "this request does not name one" and an empty string as "it names the
 * empty string", and the second would be refused by the ref rules for no reason an owner could see.
 */
function emptyToNull(value: string | null): string | null {
  const trimmed = (value ?? '').trim();

  return trimmed.length === 0 ? null : trimmed;
}

/**
 * A fresh idempotency key for one press of the button.
 *
 * The key is what makes a double submission harmless, and it has to be a new value per attempt rather
 * than one derived from the form: two captures of the same URL at two different times are two jobs the
 * owner asked for, and a key derived from the URL would collapse the second into the first.
 */
export function newIdempotencyKey(): string {
  const random = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;

  return `capture-${random}`;
}
