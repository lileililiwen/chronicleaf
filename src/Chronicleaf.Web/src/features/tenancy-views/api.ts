import type { ApiFailure } from '../background-jobs/types';
import {
  TenancyApiError,
  type LoginStart,
  type Tenant,
  type TenantList,
} from './types';

/**
 * The only client of the hosted tenancy routes in this repository.
 *
 * It follows the same rules as the delivered capture, evidence, credential and operation
 * clients: requests are `same-origin`, so the browser attaches the session cookie and this
 * code never sees it, and every mutation carries a fresh antiforgery value that is used
 * once and dropped.
 *
 * <b>What this module never emulates.</b> In a Community single-tenant installation these
 * views render the server's own answers - including the honest not-enabled states - and no
 * client-side tenancy is ever pretended. The hosted login callback is server-side; this
 * client only starts the login and navigates the browser to the returned authorization
 * address.
 */

/** Lists the caller's live tenants. Needs no active tenant to answer. */
export async function listTenants(signal?: AbortSignal): Promise<TenantList> {
  return read<TenantList>('/api/hosted/tenants', { method: 'GET', signal });
}

/** Creates a tenant the caller owns and selects it onto the session. */
export async function createTenant(name: string, signal?: AbortSignal): Promise<Tenant> {
  return write<Tenant>('/api/hosted/tenants', { method: 'POST', body: { name } }, signal);
}

/** Selects one of the caller's live tenants onto the session. */
export async function selectTenant(tenantId: string, signal?: AbortSignal): Promise<Tenant> {
  return write<Tenant>(
    '/api/hosted/tenants/select',
    { method: 'POST', body: { tenantId } },
    signal,
  );
}

/**
 * Begins a verified login. Public: it authenticates nobody and stores only hashes, so the
 * response carries the authorization URL and the state to bring back, and nothing secret.
 */
export async function startLogin(signal?: AbortSignal): Promise<LoginStart> {
  return write<LoginStart>('/api/hosted/login/start', { method: 'POST' }, signal);
}

/** Records the explicit acknowledgment of a Community volume. It moves no row. */
export async function recordImport(tenantId: string, signal?: AbortSignal): Promise<void> {
  await writeVoid(
    '/api/hosted/import',
    { method: 'POST', body: { tenantId } },
    signal,
  );
}

interface TenancyRequest {
  readonly method: 'GET' | 'POST';
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function read<T>(path: string, request: TenancyRequest): Promise<T> {
  return send<T>(path, request, undefined);
}

async function write<T>(path: string, request: TenancyRequest, signal?: AbortSignal): Promise<T> {
  return send<T>(path, request, signal);
}

async function writeVoid(path: string, request: TenancyRequest, signal?: AbortSignal): Promise<void> {
  await send<unknown>(path, request, signal);
}

async function send<T>(path: string, request: TenancyRequest, signal?: AbortSignal): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Every private route this client calls is behind the delivered owner boundary. A fresh
  // value per mutation, used once, exactly as the delivered features do. The login start is
  // public and carries no antiforgery value.
  if (request.method !== 'GET' && path !== '/api/hosted/login/start') {
    const metadata = await readAntiforgeryValue();
    headers[metadata.headerName] = metadata.token;
  }

  const response = await fetch(path, {
    method: request.method,
    credentials: 'same-origin',
    headers,
    body: request.body === undefined ? undefined : JSON.stringify(request.body),
    signal: signal ?? request.signal,
  });

  if (!response.ok) {
    throw new TenancyApiError(await toFailure(response), response.status);
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
    throw new TenancyApiError(await toFailure(response), response.status);
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
