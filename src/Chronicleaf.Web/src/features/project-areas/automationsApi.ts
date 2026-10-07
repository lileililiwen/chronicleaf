import type { ApiFailure } from '../background-jobs/types';

/**
 * The browser client of the delivered sync-schedule routes
 * (PROJECT-AREA-IA-002: SCHEDULED-PROJECT-SYNC-001/002).
 *
 * No Web panel called these routes before: the schedule package shipped
 * API-observable by design. This client follows the same rules as every
 * delivered feature client: requests are `same-origin`, so the browser
 * attaches the session cookie and this code never sees it, and every
 * mutation carries a fresh antiforgery value that is used once and
 * dropped. A refusal is raised with the delivered envelope's code and
 * status rather than flattened, because the Automations view tells a
 * missing schedule (404) apart from a refused one.
 */

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class SyncScheduleApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'SyncScheduleApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** True when the project holds no schedule: the ordinary unconfigured state, not a failure. */
  get isMissing(): boolean {
    return this.status === 404;
  }
}

/** The last operation the tick created, with its redacted outcome. */
export interface SyncScheduleLastOperation {
  readonly operationId: string;
  readonly status: string;
  readonly errorCode: string | null;
  readonly completedAt: string | null;
}

/** One schedule as the server serves it: the switch, the cadence, the next due instant. */
export interface SyncSchedule {
  readonly projectId: string;
  readonly enabled: boolean;
  readonly intervalMinutes: number;
  readonly nextRunAt: string;
  readonly lastOperation: SyncScheduleLastOperation | null;
  readonly revision: number;
  readonly updatedAt: string;
}

/**
 * Reads the stored intent with the last operation the tick created.
 *
 * A project with no schedule is refused 404 by the server, which this
 * client raises as an error with `isMissing` rather than answering
 * with an empty schedule: "nothing is armed" and "an empty schedule is
 * armed" cannot be confused.
 */
export async function readSchedule(
  projectId: string,
  signal?: AbortSignal,
): Promise<SyncSchedule> {
  return send<SyncSchedule>(
    `/api/projects/${encodeURIComponent(projectId)}/sync-schedule`,
    { method: 'GET', signal },
  );
}

/**
 * Creates (at If-Match zero) or replaces the schedule under
 * compare-and-set.
 *
 * Both fields travel because a schedule that silently kept its old
 * cadence after an owner asked for a new one would look like a correct
 * answer to a question nobody asked.
 */
export async function putSchedule(
  projectId: string,
  request: { readonly enabled: boolean; readonly intervalMinutes: number },
  revision: number,
  signal?: AbortSignal,
): Promise<SyncSchedule> {
  return send<SyncSchedule>(
    `/api/projects/${encodeURIComponent(projectId)}/sync-schedule`,
    {
      method: 'PUT',
      revision,
      body: { enabled: request.enabled, intervalMinutes: request.intervalMinutes },
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

async function send<T>(path: string, request: Request): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

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
    signal: request.signal,
  });

  if (!response.ok) {
    throw new SyncScheduleApiError(await toFailure(response), response.status);
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
    throw new SyncScheduleApiError(await toFailure(response), response.status);
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
