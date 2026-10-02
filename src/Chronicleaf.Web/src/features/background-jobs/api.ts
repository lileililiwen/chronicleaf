import type { ApiFailure, Operation, OperationPage, OperationStatus } from './types';

/**
 * The only client of the operation routes in this repository.
 *
 * It follows the same rules as the delivered milestone client, for the same reasons: requests are
 * `same-origin` so the browser attaches the session cookie and this code never sees it, and every
 * mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * A refusal is raised as an {@link OperationApiError} that keeps the code and the status rather than
 * flattening them, because the two cases this UI has to tell apart are both 4xx and both mean
 * something different: a 404 says there is no such operation here, and a 409 says it exists but has
 * already finished, which is not the same as a stop having worked.
 */

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class OperationApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'OperationApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** The status the server named for an operation it refused to cancel. */
  get reportedStatus(): OperationStatus | null {
    const named = this.fieldErrors['status']?.[0];
    return isOperationStatus(named) ? named : null;
  }
}

function isOperationStatus(value: string | undefined): value is OperationStatus {
  return (
    value === 'queued' ||
    value === 'running' ||
    value === 'succeeded' ||
    value === 'failed' ||
    value === 'cancelled' ||
    value === 'superseded'
  );
}

/** The scope's operations, newest first. */
export async function listOperations(signal?: AbortSignal): Promise<OperationPage> {
  return readJson<OperationPage>('/api/operations?limit=100', { method: 'GET', signal });
}

/** One operation, for the detail view. */
export async function getOperation(operationId: string, signal?: AbortSignal): Promise<Operation> {
  return readJson<Operation>(`/api/operations/${encodeURIComponent(operationId)}`, { method: 'GET', signal });
}

/**
 * Asks for an operation to stop. The server makes the status terminal immediately, so this does not
 * wait for a worker, and the answer is the operation as it now stands rather than a promise.
 */
export async function cancelOperation(operationId: string, signal?: AbortSignal): Promise<Operation> {
  return writeJson<Operation>(`/api/operations/${encodeURIComponent(operationId)}/cancel`, {
    method: 'POST',
    signal,
  });
}

interface Request {
  readonly method: string;
  readonly body?: unknown;
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

  const response = await fetch(path, {
    method: request.method,
    credentials: 'same-origin',
    headers,
    body: request.body === undefined ? undefined : JSON.stringify(request.body),
    signal: request.signal,
  });

  if (!response.ok) {
    throw new OperationApiError(await toFailure(response), response.status);
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
    throw new OperationApiError(await toFailure(response), response.status);
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
