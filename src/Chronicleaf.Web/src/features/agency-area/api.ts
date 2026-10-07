import { AgencyApiError } from './types';
import type {
  AgencyClient,
  AgencyClientPage,
  AgencyClientProjectPage,
  AgencyProjectPage,
  AgencyReportContext,
  ApiFailure,
  WeeklyReport,
  WeeklyReportApproval,
} from './types';

export { AgencyApiError };

/**
 * The only client of the agency client and weekly-report routes in this repository.
 *
 * <b>It lists, creates, edits, archives, assigns, generates, approves and
 * downloads — it never portals, never invoices and never sends email</b>,
 * because no delivered route takes a recipient, a price or a send flag and
 * none is invented here. Every mutation carries the revision the owner was
 * looking at in `If-Match`, so a stale edit loses loudly instead of
 * overwriting, and every failure is raised with the server's own code.
 *
 * <b>Requests are `same-origin`,</b> so the browser attaches the session
 * cookie and this code never sees it, and every mutation carries a fresh
 * antiforgery value that is used once and dropped.
 */

export async function listClients(signal?: AbortSignal): Promise<AgencyClientPage> {
  return readJson<AgencyClientPage>('/api/clients', { method: 'GET', signal });
}

export async function createClient(
  draft: { readonly name: string; readonly notes: string },
  signal?: AbortSignal,
): Promise<AgencyClient> {
  return writeJson<AgencyClient>('/api/clients', {
    method: 'POST',
    body: { name: draft.name, notes: draft.notes },
    signal,
  });
}

export async function getClient(clientId: string, signal?: AbortSignal): Promise<AgencyClient> {
  return readJson<AgencyClient>(`/api/clients/${encodeURIComponent(clientId)}`, {
    method: 'GET',
    signal,
  });
}

export async function updateClient(
  clientId: string,
  revision: number,
  draft: { readonly name: string; readonly notes: string },
  signal?: AbortSignal,
): Promise<AgencyClient> {
  return writeJson<AgencyClient>(`/api/clients/${encodeURIComponent(clientId)}`, {
    method: 'PATCH',
    revision,
    body: { name: draft.name, notes: draft.notes },
    signal,
  });
}

/** Archives a client. Nothing grouped under it is moved; the stamp makes it refuse new work. */
export async function archiveClient(
  clientId: string,
  revision: number,
  signal?: AbortSignal,
): Promise<AgencyClient> {
  return writeJson<AgencyClient>(`/api/clients/${encodeURIComponent(clientId)}`, {
    method: 'DELETE',
    revision,
    signal,
  });
}

export async function listClientProjects(
  clientId: string,
  signal?: AbortSignal,
): Promise<AgencyClientProjectPage> {
  return readJson<AgencyClientProjectPage>(
    `/api/clients/${encodeURIComponent(clientId)}/projects`,
    { method: 'GET', signal },
  );
}

export async function listAllProjects(signal?: AbortSignal): Promise<AgencyProjectPage> {
  return readJson<AgencyProjectPage>('/api/projects', { method: 'GET', signal });
}

export async function readReportContext(
  clientId: string,
  projectIds: readonly string[],
  signal?: AbortSignal,
): Promise<AgencyReportContext> {
  const query = projectIds.map((id) => `projectIds=${encodeURIComponent(id)}`).join('&');
  const suffix = query.length === 0 ? '' : `?${query}`;

  return readJson<AgencyReportContext>(
    `/api/clients/${encodeURIComponent(clientId)}/context${suffix}`,
    { method: 'GET', signal },
  );
}

/**
 * Groups a project under a client, or clears it back to personal with an
 * explicit `null`. The key is always present: clearing is a decision the
 * server records, never an omission it guesses at.
 */
export async function setProjectClient(
  projectId: string,
  projectRevision: number,
  clientId: string | null,
  signal?: AbortSignal,
): Promise<{ readonly id: string; readonly revision: number; readonly clientId: string | null }> {
  return writeJson(`/api/projects/${encodeURIComponent(projectId)}/client`, {
    method: 'PATCH',
    revision: projectRevision,
    body: { clientId },
    signal,
  });
}

export async function generateReport(
  clientId: string,
  body: {
    readonly projectIds: readonly string[];
    readonly weekStart: string;
    readonly timeZone: string;
    readonly inProgressNotes: string | null;
    readonly nextWeekNotes: string | null;
  },
  signal?: AbortSignal,
): Promise<WeeklyReport> {
  return writeJson<WeeklyReport>(`/api/clients/${encodeURIComponent(clientId)}/weekly-reports`, {
    method: 'POST',
    body: {
      projectIds: [...body.projectIds],
      weekStart: body.weekStart,
      timeZone: body.timeZone,
      inProgressNotes: body.inProgressNotes,
      nextWeekNotes: body.nextWeekNotes,
      idempotencyKey: null,
    },
    signal,
  });
}

export async function getReport(
  clientId: string,
  reportId: string,
  signal?: AbortSignal,
): Promise<WeeklyReport> {
  return readJson<WeeklyReport>(
    `/api/clients/${encodeURIComponent(clientId)}/weekly-reports/${encodeURIComponent(reportId)}`,
    { method: 'GET', signal },
  );
}

export async function approveReport(
  clientId: string,
  reportId: string,
  revision: number,
  signal?: AbortSignal,
): Promise<WeeklyReportApproval> {
  return writeJson<WeeklyReportApproval>(
    `/api/clients/${encodeURIComponent(clientId)}/weekly-reports/${encodeURIComponent(reportId)}/approve`,
    { method: 'POST', revision, body: {}, signal },
  );
}

/**
 * Downloads the approved report's exact bytes, in the backend's own shape.
 *
 * The server answers with the stored Markdown or the rendered HTML as an
 * attachment — or with a 409 naming `approval_required`, `approval_stale`
 * or `media_unavailable`. This client never renders the answer as a
 * document: it hands the bytes to a download, so markup in them stays inert
 * on the way out as well.
 */
export async function downloadReport(
  clientId: string,
  reportId: string,
  format: 'markdown' | 'html',
  revision: number | null,
  signal?: AbortSignal,
): Promise<{ readonly fileName: string; readonly bytes: Blob }> {
  const revisionQuery = revision === null ? '' : `&revision=${revision}`;

  const response = await fetch(
    `/api/clients/${encodeURIComponent(clientId)}/weekly-reports/${encodeURIComponent(reportId)}/export?format=${format}${revisionQuery}`,
    {
      method: 'GET',
      credentials: 'same-origin',
      headers: { Accept: 'text/markdown, text/html' },
      signal,
    },
  );

  if (!response.ok) {
    throw new AgencyApiError(await toFailure(response), response.status);
  }

  return {
    fileName: fileNameOf(response, reportId, format),
    bytes: await response.blob(),
  };
}

/** Hands downloaded report bytes to a file the owner chose to save. */
export function saveReportDownload(fileName: string, bytes: Blob): void {
  const url = URL.createObjectURL(bytes);

  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    // The bytes are the server's attachment, handed to a file: revoking here
    // would race the click above on some browsers, so the object URL lives as
    // long as the page rather than being cut out from under the download.
  }
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
    // A fresh antiforgery value per mutation, used once, exactly as the
    // delivered features do.
    const metadata = await readAntiforgeryValue();
    headers[metadata.headerName] = metadata.token;
  }

  if (request.revision !== undefined) {
    // The revision the owner was looking at. Without it the server refuses
    // with 428 rather than guessing which version is being edited.
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
    throw new AgencyApiError(await toFailure(response), response.status);
  }

  if (response.status === 204) {
    return undefined as T;
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
    throw new AgencyApiError(await toFailure(response), response.status);
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

function fileNameOf(response: Response, reportId: string, format: 'markdown' | 'html'): string {
  const disposition = response.headers.get('content-disposition') ?? '';
  const match = /filename="?([^"]+)"?/.exec(disposition);

  return match?.[1] ?? `weekly-report-${reportId}.${format === 'html' ? 'html' : 'md'}`;
}
