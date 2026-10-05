import type { ApiFailure, Operation } from '../background-jobs/types';
import { NarrativeApiError, type Narrative, type NarrativeEntry, type NarrativeRefreshAccepted } from './types';

/**
 * The only client of the narrative routes in this repository.
 *
 * <b>It records history and asks for refreshes; it never approves.</b> There is no approve verb
 * on these routes and no field here that could report one: a summary is a generated document bound
 * to its input hash, and approval stays the review package's. The refresh answers 202 with a job,
 * and this client follows the job through the delivered operation route rather than deciding on
 * its own that the summary changed.
 *
 * <b>Requests are `same-origin`,</b> so the browser attaches the session cookie and this code never sees
 * it, and every mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * <b>What this module may hold.</b> The owner's history lines and the generated summary - words the
 * owner wrote and a document the refresh concluded. What it never holds is a credential, a provider
 * response, or a stored consent.
 */

/** Reads one project's entries and its summary, if any. */
export async function readNarrative(
  projectId: string,
  signal?: AbortSignal,
): Promise<Narrative> {
  return send<Narrative>(`/api/projects/${encodeURIComponent(projectId)}/narrative`, {
    method: 'GET',
    signal,
  });
}

/**
 * Records one explicit line of project history.
 *
 * Every cited evidence id must belong to the project: a line that names material the owner never
 * collected is refused with 404 rather than stored without its source.
 */
export async function createEntry(
  projectId: string,
  body: {
    readonly kind: string;
    readonly occurredAt: string;
    readonly text: string;
    readonly sourceEvidenceIds: readonly string[];
    readonly visibility: 'private' | 'exportable';
  },
  signal?: AbortSignal,
): Promise<NarrativeEntry> {
  return send<NarrativeEntry>(`/api/projects/${encodeURIComponent(projectId)}/narrative`, {
    method: 'POST',
    body,
    signal,
  });
}

/**
 * Edits one narrative entry.
 *
 * The expected revision travels twice - in `If-Match` and in the body - and the server refuses
 * the call when the two disagree, so a panel that built one from a stale read cannot fork the
 * history. A missing `If-Match` is a 428 the server answers before anything is written, and a
 * stale writer loses with 409 rather than merging.
 */
export async function editEntry(
  projectId: string,
  entryId: string,
  expectedRevision: number,
  body: { readonly text: string },
  signal?: AbortSignal,
): Promise<NarrativeEntry> {
  return send<NarrativeEntry>(
    `/api/projects/${encodeURIComponent(projectId)}/narrative/${encodeURIComponent(entryId)}`,
    {
      method: 'PATCH',
      ifMatch: String(expectedRevision),
      body: { expectedRevision, text: body.text },
      signal,
    },
  );
}

/**
 * Asks for a summary refresh and returns the job that will produce it.
 *
 * A 202 and an operation id, never a summary. The refresh re-reads every source and rebuilds the
 * body in the delivered queue - so this call reports that the request was accepted and nothing
 * more. A replay under the same idempotency key answers with the original job.
 */
export async function refreshSummary(
  projectId: string,
  body: {
    readonly cutoff: string | null;
    readonly selectedEntryIds: readonly string[];
    readonly idempotencyKey: string;
  },
  signal?: AbortSignal,
): Promise<NarrativeRefreshAccepted> {
  return send<NarrativeRefreshAccepted>(
    `/api/projects/${encodeURIComponent(projectId)}/narrative/refresh`,
    { method: 'POST', body, signal },
  );
}

/** Reads one job from the delivered operations route. */
export async function readRefreshOperation(
  operationId: string,
  signal?: AbortSignal,
): Promise<Operation> {
  return send<Operation>(`/api/operations/${encodeURIComponent(operationId)}`, { method: 'GET', signal });
}

interface NarrativeRequest {
  readonly method: 'GET' | 'POST' | 'PATCH';
  readonly ifMatch?: string;
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function send<T>(path: string, request: NarrativeRequest): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  if (request.ifMatch !== undefined) {
    headers['If-Match'] = request.ifMatch;
  }

  // Every route this client calls is behind the delivered owner boundary, and the create, edit
  // and refresh routes are mutations. A fresh antiforgery value per call, used once, exactly as
  // the delivered features do.
  if (request.method !== 'GET') {
    const metadata = await readAntiforgeryValue();
    headers[metadata.headerName] = metadata.token;
  }

  const response = await fetch(path, {
    method: request.method,
    credentials: 'same-origin',
    headers,
    body: request.body === undefined ? undefined : JSON.stringify(request.body),
    signal: request.signal,
  });

  if (!response.ok) {
    throw new NarrativeApiError(await toFailure(response), response.status);
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
    throw new NarrativeApiError(await toFailure(response), response.status);
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
    return {
      code: 'internal_error',
      message: 'The request could not be completed.',
      traceId: '',
    };
  }
}
