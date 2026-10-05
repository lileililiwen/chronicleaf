import type { ApiFailure } from '../background-jobs/types';
import type {
  LocalGitSyncAccepted,
  LocalGitSyncDraft,
  LocalRepository,
  LocalRepositoryCatalog,
} from './types';

/**
 * The only client of the local Git evidence routes in this repository.
 *
 * It follows the same rules as the delivered evidence, credential and operation clients: requests are
 * `same-origin`, so the browser attaches the session cookie and this code never sees it, and every
 * mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * <b>What is deliberately absent.</b> No directory, no absolute path, and no free-text root field
 * anywhere in this module. The only half of the address a caller may name is a registered alias - read
 * from the catalogue route rather than typed - and a path relative to it. There is no way from this client
 * to make the server look at a directory the operator did not register, which is the property the whole
 * surface rests on.
 */

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class LocalGitApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'LocalGitApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /**
   * True when the project has no local repository registered.
   *
   * An answer rather than a failure, and treating it as one would put a red error on a project that is
   * simply not collecting from a local checkout yet.
   */
  get isRepositoryMissing(): boolean {
    return this.code === 'local_git_repository_missing';
  }

  /** True when somebody else moved the evidence this collection was requested against. */
  get isRevisionConflict(): boolean {
    return this.status === 409;
  }

  /**
   * True when this deployment does not read local directories at all.
   *
   * Not an error either: it is the state a shared installation is in, and a panel that rendered it as a
   * failure would report a deployment decision as a broken product.
   */
  get isNotAvailable(): boolean {
    return this.code === 'local_git_not_available_in_hosted';
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}

/**
 * The registered roots this installation offers.
 *
 * The route is a GET behind the owner boundary, like every other read here, and its whole purpose is to let
 * a picker be a picker instead of a text field that asks an owner to remember an operator's alias.
 */
export async function readCatalog(signal?: AbortSignal): Promise<LocalRepositoryCatalog> {
  return send<LocalRepositoryCatalog>('/api/local-repository-roots', { method: 'GET', signal });
}

/**
 * Reads what a project collects from the operator's disk.
 *
 * A project with no registration is refused with this package's own code rather than answered with an empty
 * one, so "nothing is registered" and "the registration is empty" cannot be confused.
 */
export async function readRepository(
  projectId: string,
  signal?: AbortSignal,
): Promise<LocalRepository> {
  return send<LocalRepository>(
    `/api/projects/${encodeURIComponent(projectId)}/local-repository`,
    { method: 'GET', signal },
  );
}

/**
 * Points a project at a repository inside a registered root.
 *
 * The alias must be one the catalogue published and the path is relative to it, so a request cannot choose
 * where the server reads: the server resolves the two against its own configuration and refuses anything
 * that leaves. There is no field here that could carry a directory.
 */
export async function registerRepository(
  projectId: string,
  request: { readonly rootAlias: string; readonly relativePath: string },
  revision: number,
  signal?: AbortSignal,
): Promise<LocalRepository> {
  return send<LocalRepository>(
    `/api/projects/${encodeURIComponent(projectId)}/local-repository`,
    {
      method: 'POST',
      revision,
      body: { rootAlias: request.rootAlias, relativePath: request.relativePath },
      signal,
    },
  );
}

/**
 * Asks a milestone to collect from the repository its project is registered against.
 *
 * A 202 and an operation id, never evidence. The collection runs several Git processes against a disk, so
 * it happens in the delivered queue and the answer is a job to read progress on through the delivered
 * operation route. `operationUrl` is in the response so a client does not have to build the path itself
 * and cannot guess at it.
 *
 * The evidence revision travels as `If-Match` and in the body, because the server refuses a body that
 * disagrees with the header rather than picking one. That revision is the bundle the owner was looking at
 * when they pressed the button, so a bundle that has moved since cannot have this collection appended.
 */
export async function requestSync(
  milestoneId: string,
  draft: LocalGitSyncDraft,
  signal?: AbortSignal,
): Promise<LocalGitSyncAccepted> {
  return send<LocalGitSyncAccepted>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/local-sync`,
    {
      method: 'POST',
      revision: draft.expectedBundleRevision,
      body: {
        headRef: draft.headRef,
        baseRef: emptyToNull(draft.baseRef),
        expectedBundleRevision: draft.expectedBundleRevision,
        idempotencyKey: draft.idempotencyKey,
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

async function send<T>(path: string, request: Request): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Every route this client calls is behind the delivered owner boundary, and every one of them except the
  // two reads is a mutation. A fresh value per call, used once, exactly as the delivered features do.
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
    throw new LocalGitApiError(await toFailure(response), response.status);
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
    throw new LocalGitApiError(await toFailure(response), response.status);
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
 * The server treats a null as "this request does not name one" and an empty string as "it names the empty
 * string", and the second would be refused by the ref rules for no reason an owner could see.
 */
function emptyToNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}
