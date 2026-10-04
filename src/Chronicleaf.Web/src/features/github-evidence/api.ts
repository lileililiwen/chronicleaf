import type { ApiFailure } from '../background-jobs/types';
import type { GitHubConnection, GitHubSyncAccepted, GitHubSyncDraft } from './types';

/**
 * The only client of the GitHub evidence routes in this repository.
 *
 * It follows the same rules as the delivered evidence, credential and operation clients: requests are
 * `same-origin`, so the browser attaches the session cookie and this code never sees it, and every
 * mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * <b>What this module may hold.</b> Nothing that is a secret. It sends an owner, a repository name and
 * a reference to a credential the owner already stored through the delivered vault route, and it
 * receives a connection that publishes no URL, no token and not even the vault's identifier. The token
 * itself never passes through here: a value is stored by one call to the credential client and dropped
 * by the component that typed it, and this client's `credentialRef` is an id, not a value.
 *
 * <b>What is deliberately absent.</b> There is no endpoint, no host and no URL anywhere in these three
 * routes, in this client or in the types it moves. The host is a constant of the server's transport, so
 * nothing a caller can put in a body can decide where the server's next request goes.
 */

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class GitHubEvidenceApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'GitHubEvidenceApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /**
   * True when the project has no repository connection.
   *
   * This is an answer rather than an error, and treating it as one would put a red error on a project
   * that is simply not collecting from anywhere yet. The server names it as a validation failure on
   * purpose: the project is real, what is missing is something the owner has to supply.
   */
  get isConnectionMissing(): boolean {
    return this.code === 'github_connection_missing';
  }

  /** True when somebody else moved the evidence this collection was requested against. */
  get isRevisionConflict(): boolean {
    return this.status === 409;
  }

  /** True when GitHub could not be read, which is worth retrying rather than correcting. */
  get isUnreachable(): boolean {
    return this.code === 'github_unreachable';
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}

/**
 * Reads what a project collects from.
 *
 * A project with no connection is refused with this package's own code rather than answering with an
 * empty one, so "nothing is connected" and "the connection is empty" cannot be confused. The caller
 * turns that into an honest empty state; it does not treat it as a failure to report.
 */
export async function readConnection(
  projectId: string,
  signal?: AbortSignal,
): Promise<GitHubConnection> {
  return send<GitHubConnection>(
    `/api/projects/${encodeURIComponent(projectId)}/github-connection`,
    { method: 'GET', signal },
  );
}

/**
 * Points a project at a repository, replacing whatever it collected from before.
 *
 * The server validates the repository with a real read, so a refusal here is GitHub's answer rather
 * than this client's guess: a repository that is not public and a repository that does not exist are
 * the same refusal, and nothing is stored when it happens.
 *
 * `credentialRef` is the id of a credential the owner already stored through the delivered vault
 * route. It is optional, because a public repository needs no token, and it is an id: this function
 * has never seen the value it refers to.
 */
export async function connectRepository(
  projectId: string,
  request: {
    readonly owner: string;
    readonly repository: string;
    readonly credentialRef: string | null;
  },
  signal?: AbortSignal,
): Promise<GitHubConnection> {
  return send<GitHubConnection>(
    `/api/projects/${encodeURIComponent(projectId)}/github-connection`,
    {
      method: 'POST',
      body: {
        owner: request.owner,
        repository: request.repository,
        credentialRef: request.credentialRef,
      },
      signal,
    },
  );
}

/**
 * Asks a milestone to collect from the repository its project is connected to.
 *
 * A 202 and an operation id, never evidence. Collection is a bounded set of network reads with a rate
 * limit attached, so it happens in the delivered queue and the answer is a job to read progress on
 * through the delivered operation route. `operationUrl` is in the response so a client does not have to
 * build the path itself and cannot guess at it.
 *
 * The evidence revision travels as `If-Match` and in the body, because the server refuses a body that
 * disagrees with the header rather than picking one. That revision is the bundle the owner was looking
 * at when they pressed the button, so a bundle that has moved since cannot have this collection
 * appended to it.
 */
export async function requestSync(
  milestoneId: string,
  draft: GitHubSyncDraft,
  signal?: AbortSignal,
): Promise<GitHubSyncAccepted> {
  return send<GitHubSyncAccepted>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/github-sync`,
    {
      method: 'POST',
      revision: draft.expectedBundleRevision,
      body: {
        headRef: draft.headRef,
        baseRef: emptyToNull(draft.baseRef),
        releaseTag: emptyToNull(draft.releaseTag),
        issueNumber: toNumber(draft.issueNumber),
        pullRequestNumber: toNumber(draft.pullRequestNumber),
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

  // Every route this client calls is behind the delivered owner boundary, and every one of them
  // except the connection read is a mutation. A fresh value per call, used once, exactly as the
  // delivered features do.
  if (request.method !== 'GET') {
    const metadata = await readAntiforgeryValue();
    headers[metadata.headerName] = metadata.token;
  }

  if (request.revision !== undefined) {
    // The evidence revision this collection extends. Without it the server would not know which
    // bundle the owner was looking at, and refusing to guess is the whole point of the header.
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
    throw new GitHubEvidenceApiError(await toFailure(response), response.status);
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
    throw new GitHubEvidenceApiError(await toFailure(response), response.status);
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
function emptyToNull(value: string): string | null {
  const trimmed = value.trim();

  return trimmed.length === 0 ? null : trimmed;
}

/**
 * A number the owner typed, or nothing.
 *
 * `NaN` is never sent: an issue number that is not a whole number is left absent so the server can
 * refuse the request it can see rather than this client guessing what was meant.
 */
function toNumber(value: string): number | null {
  const trimmed = value.trim();

  if (trimmed.length === 0) {
    return null;
  }

  const parsed = Number(trimmed);

  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}
