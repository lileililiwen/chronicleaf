import type {
  ApiFailure,
  CredentialMetadata,
  CredentialPage,
  CredentialProvider,
  ProviderState,
} from './types';

/**
 * The only client of the credential routes in this repository.
 *
 * It follows the same rules as the delivered evidence and operation clients: requests are
 * `same-origin`, so the browser attaches the session cookie and this code never sees it, and every
 * mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * What is different here is what this client may hold. A value exists in this module for the duration
 * of one `saveCredential` call and is then dropped by the caller; there is no read that returns one,
 * no cache of one, and no state that stores one. The only things that come back are the four metadata
 * fields and the revision, which is why a reload can never show a stored value even by accident.
 */
export class CredentialApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'CredentialApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** True when the credential moved under the owner and the current one was preserved. */
  get isRevisionConflict(): boolean {
    return this.status === 409;
  }

  /** True when the operator's key is unusable, so nothing was stored. */
  get isVaultUnavailable(): boolean {
    return this.status === 503;
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}

/** Reads the configured providers, a bounded page at a time. */
export async function listCredentials(
  limit?: number,
  cursor?: string,
  signal?: AbortSignal,
): Promise<CredentialPage> {
  const parameters = new URLSearchParams();

  if (limit !== undefined) {
    parameters.set('limit', String(limit));
  }

  if (cursor !== undefined && cursor.length > 0) {
    parameters.set('cursor', cursor);
  }

  const query = parameters.toString();

  return send<CredentialPage>(
    `/api/settings/credentials${query.length > 0 ? `?${query}` : ''}`,
    { method: 'GET', signal },
    false,
  );
}

/**
 * Reads one provider's state: whether it is configured, when it changed, and the revision the next
 * write presents. The revision is taken from the response's own entity-tag rather than from a field,
 * because that is where the server publishes it.
 */
export async function readProvider(
  provider: CredentialProvider,
  signal?: AbortSignal,
): Promise<ProviderState> {
  const { body, entityTag } = await sendWithEntityTag<CredentialMetadata>(
    `/api/settings/credentials/${encodeURIComponent(provider)}`,
    { method: 'GET', signal },
    false,
  );

  return {
    provider,
    id: body.id,
    configured: body.configured,
    updatedAt: body.updatedAt,
    revision: parseEntityTag(entityTag),
  };
}

/**
 * Stores a value for a provider, replacing what was there.
 *
 * `revision` is the revision the owner was looking at, and 0 means this scope has nothing stored for
 * the provider yet. The value is sent once and is not kept: the returned metadata has no field that
 * could hold it, and the panel clears its input as soon as this resolves.
 */
export async function saveCredential(
  provider: CredentialProvider,
  value: string,
  revision: number,
  signal?: AbortSignal,
): Promise<CredentialMetadata> {
  const body = await send<CredentialMetadata>(
    `/api/settings/credentials/${encodeURIComponent(provider)}`,
    {
      method: 'PUT',
      revision,
      body: { value, expectedRevision: revision },
      signal,
    },
    true,
  );

  return body;
}

/** Removes a credential, presenting the revision the owner read. */
export async function deleteCredential(
  id: string,
  revision: number,
  signal?: AbortSignal,
): Promise<CredentialMetadata> {
  return send<CredentialMetadata>(
    `/api/settings/credentials/${encodeURIComponent(id)}`,
    { method: 'DELETE', revision, signal },
    true,
  );
}

/** The revision an entity-tag carries, or 0 when the server published none. */
export function parseEntityTag(entityTag: string | null): number {
  if (entityTag === null) {
    return 0;
  }

  const parsed = Number(entityTag.trim().replace(/^W\//, '').replace(/"/g, ''));

  return Number.isFinite(parsed) && parsed >= 0 ? parsed : 0;
}

interface Request {
  readonly method: string;
  readonly body?: unknown;
  readonly revision?: number;
  readonly signal?: AbortSignal;
}

interface Answer<T> {
  readonly body: T;
  readonly entityTag: string | null;
}

async function send<T>(path: string, request: Request, mutating: boolean): Promise<T> {
  const answer = await sendWithEntityTag<T>(path, request, mutating);

  return answer.body;
}

async function sendWithEntityTag<T>(
  path: string,
  request: Request,
  mutating: boolean,
): Promise<Answer<T>> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  if (mutating) {
    const metadata = await readAntiforgeryValue();
    headers[metadata.headerName] = metadata.token;
  }

  if (request.revision !== undefined) {
    // The revision this write replaces. Without it the server refuses rather than guessing which
    // version the owner was looking at.
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
    throw new CredentialApiError(await toFailure(response), response.status);
  }

  return {
    body: (await response.json()) as T,
    entityTag: response.headers.get('ETag'),
  };
}

async function readAntiforgeryValue(): Promise<{ headerName: string; token: string }> {
  const response = await fetch('/api/session/antiforgery', {
    method: 'GET',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new CredentialApiError(await toFailure(response), response.status);
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
