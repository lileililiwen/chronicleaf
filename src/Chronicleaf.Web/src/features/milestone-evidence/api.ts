import type {
  ApiFailure,
  EvidenceBundle,
  EvidencePage,
  ManualEvidenceDraft,
} from './types';

/**
 * The only client of the evidence routes in this repository.
 *
 * It follows the same rules as the delivered milestone and operation clients, for the same reasons:
 * requests are `same-origin`, so the browser attaches the session cookie and this code never sees
 * it, and every mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * A refusal is raised as an {@link EvidenceApiError} that keeps the code and the status rather than
 * flattening them. The case this UI most has to get right is the 409: it means somebody else appended
 * evidence first, and the honest response is to show that and re-read - not to drop the owner's
 * statement and leave them thinking it was saved.
 */

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class EvidenceApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'EvidenceApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** True when somebody else's append reached this milestone first. */
  get isRevisionConflict(): boolean {
    return this.status === 409;
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}

/**
 * Reads a milestone's evidence. A limit is optional and is clamped by the server rather than refused,
 * so this client never has to know the maximum.
 */
export async function listEvidence(
  milestoneId: string,
  limit?: number,
  signal?: AbortSignal,
): Promise<EvidencePage> {
  const query = limit === undefined ? '' : `?limit=${encodeURIComponent(String(limit))}`;

  return readJson<EvidencePage>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/evidence${query}`,
    { method: 'GET', signal },
  );
}

/**
 * Appends what the owner wrote by hand.
 *
 * The body has no kind, no availability and no source: the server decides all three from what was
 * written, so a form cannot claim to be a commit or a fully collected source. The revision the owner
 * was looking at travels both as `If-Match` and in the body, because the server refuses a body that
 * disagrees with the header rather than picking one.
 */
export async function appendManualEvidence(
  milestoneId: string,
  revision: number,
  draft: ManualEvidenceDraft,
  signal?: AbortSignal,
): Promise<EvidenceBundle> {
  return writeJson<EvidenceBundle>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/evidence/manual`,
    {
      method: 'POST',
      revision,
      body: {
        notes: draft.notes,
        knownLimitations: draft.knownLimitations,
        nextSteps: draft.nextSteps,
        expectedRevision: revision,
      },
      signal,
    },
  );
}

interface Request {
  readonly method: string;
  readonly body?: unknown;
  readonly revision?: number;
  readonly signal?: AbortSignal;
}

async function readJson<T>(path: string, request: Request): Promise<T> {
  return send<T>(path, request, false);
}

async function writeJson<T>(path: string, request: Request): Promise<T> {
  return send<T>(path, request, true);
}

async function send<T>(path: string, request: Request, mutating: boolean): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  if (mutating) {
    const metadata = await readAntiforgeryValue();
    headers[metadata.headerName] = metadata.token;
  }

  if (request.revision !== undefined) {
    // The evidence revision this append is based on. Without it the server would not know which
    // snapshot the owner was looking at, and refusing to guess is the whole point.
    headers['If-Match'] = String(request.revision);
  }

  const response = await fetch(path, {
    method: request.method,
    credentials: 'same-origin',
    headers,
    body: request.body === undefined ? undefined : JSON.stringify(request.body),
    signal: request.signal,
  });

  if (!response.ok) {
    throw new EvidenceApiError(await toFailure(response), response.status);
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
    throw new EvidenceApiError(await toFailure(response), response.status);
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
