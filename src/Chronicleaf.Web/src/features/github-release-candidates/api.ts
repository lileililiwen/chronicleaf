import type { ApiFailure } from '../background-jobs/types';
import type {
  GitHubCandidatePage,
  GitHubCandidateSyncAccepted,
  GitHubWebhookSubscription,
} from './types';

/**
 * The only browser client of the GitHub candidate routes in this repository.
 *
 * It follows the same rules as the delivered GitHub, local-Git, credential and operation clients:
 * requests are `same-origin` so the browser attaches the session cookie and this code never sees it,
 * and every mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * <b>Two things are deliberately absent from this module.</b> There is no field anywhere that could
 * carry a secret - the webhook secret is stored through the delivered credential route and referred to
 * by id, and this module never sends or receives one. And there is no verb that confirms anything:
 * `subscribe` and `unsubscribe` change whether a connection listens, `requestSync` asks for a poll, and
 * nothing here writes a milestone, appends evidence or publishes. A candidate this module fetches is a
 * proposal, and there is no call in this file that could turn one into a record.
 */

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class GitHubCandidateApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'GitHubCandidateApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /**
   * True when the project has no repository connected.
   *
   * An answer rather than a failure, and treating it as one would put a red error on a project that has
   * simply not connected a repository yet.
   */
  get isConnectionMissing(): boolean {
    return this.code === 'github_candidate_connection_missing';
  }

  /**
   * True when the project revision this request was based on has moved.
   *
   * The same class as the delivered panels' conflict handling: nothing is wrong with the request, the
   * project it was built from has changed, and the answer is to read the project again rather than to
   * retry the same stale write.
   */
  get isRevisionConflict(): boolean {
    return this.status === 409;
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}

/**
 * A project's proposed candidates, newest first, with the stored total beside the page.
 *
 * The page is bounded by the server, not by this call, and the total comes from the same response rather
 * than from a second request - so a panel cannot end up showing a page length as though it were the whole
 * history.
 */
export async function readCandidates(
  projectId: string,
  limit: number,
  signal?: AbortSignal,
): Promise<GitHubCandidatePage> {
  return send<GitHubCandidatePage>(
    `/api/projects/${encodeURIComponent(projectId)}/github-candidates?limit=${encodeURIComponent(String(limit))}`,
    { method: 'GET', signal },
  );
}

/**
 * Asks a project's repository to be polled for releases and tags.
 *
 * A 202 and an operation id, never a candidate. A poll is a bounded set of network reads against a
 * rate-limited host, so the work happens on the delivered queue and this response points at the delivered
 * operation route rather than holding the browser open across GitHub.
 *
 * <b>The project revision travels as `If-Match` and is not repeated in the body.</b> It is a precondition
 * on the project, not a value the client gets to choose, and a body carrying its own copy would be a
 * second place the two could disagree.
 */
export async function requestSync(
  projectId: string,
  draft: { readonly since: string | null; readonly idempotencyKey: string; readonly revision: number },
  signal?: AbortSignal,
): Promise<GitHubCandidateSyncAccepted> {
  return send<GitHubCandidateSyncAccepted>(
    `/api/projects/${encodeURIComponent(projectId)}/github-candidates/sync`,
    {
      method: 'POST',
      revision: draft.revision,
      body: { since: draft.since, idempotencyKey: draft.idempotencyKey },
      signal,
    },
  );
}

/**
 * Whether this project's connection is listening for signed deliveries.
 *
 * A 404 means it is not listening and has never been, which this client turns into `null` rather than a
 * failure: "no subscription" is the ordinary state of a project that has not turned one on.
 */
export async function readSubscription(
  projectId: string,
  signal?: AbortSignal,
): Promise<GitHubWebhookSubscription | null> {
  try {
    return await send<GitHubWebhookSubscription>(
      `/api/projects/${encodeURIComponent(projectId)}/github-webhook`,
      { method: 'GET', signal },
    );
  } catch (error) {
    if (error instanceof GitHubCandidateApiError && error.status === 404) {
      return null;
    }

    throw error;
  }
}

/**
 * Points the connection at a webhook secret that is already stored, or stops it listening.
 *
 * The argument is a vault reference and never a secret. The secret itself is written through the
 * delivered credential route under the `github-webhook` provider, which means this panel cannot leak one:
 * it has no field to type one into and no way to read one back.
 *
 * An absent reference is not a malformed body - it is the owner turning the subscription off, and the
 * server treats it as exactly that rather than as something to refuse.
 */
export async function subscribe(
  projectId: string,
  credentialRef: string | null,
  revision: number,
  signal?: AbortSignal,
): Promise<GitHubWebhookSubscription> {
  return send<GitHubWebhookSubscription>(
    `/api/projects/${encodeURIComponent(projectId)}/github-webhook`,
    {
      method: 'PUT',
      revision,
      body: { credentialRef },
      signal,
    },
  );
}

/**
 * Stops the connection accepting deliveries, without losing the receipts it already has.
 *
 * The server answers 204 when there was nothing subscribed, because "no subscription" is exactly the
 * state the owner asked for and a 404 would send a client looking for something that is correctly
 * absent.
 */
export async function unsubscribe(
  projectId: string,
  revision: number,
  signal?: AbortSignal,
): Promise<GitHubWebhookSubscription | null> {
  const response = await sendOptional<GitHubWebhookSubscription>(
    `/api/projects/${encodeURIComponent(projectId)}/github-webhook`,
    { method: 'DELETE', revision, signal },
  );

  return response;
}

interface Request {
  readonly method: string;
  readonly body?: unknown;
  readonly revision?: number;
  readonly signal?: AbortSignal;
}

async function send<T>(path: string, request: Request): Promise<T> {
  const response = await sendOptional<T>(path, request);

  if (response === null) {
    throw new GitHubCandidateApiError(
      { code: 'internal_error', message: 'The request could not be completed.', traceId: '' },
      502,
    );
  }

  return response;
}

/**
 * Sends the request and reads the answer, or <c>null</c> for the one success that carries no body.
 *
 * The 204 is the only bodyless success in this package's surface, and it is handled here rather than by
 * a caller: a panel that had to remember that one route answers empty is a panel that will eventually try
 * to parse nothing.
 */
async function sendOptional<T>(path: string, request: Request): Promise<T | null> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Every route this client calls is behind the delivered owner boundary, and every one of them except
  // the reads is a mutation. A fresh value per call, used once, exactly as the delivered features do.
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
    throw new GitHubCandidateApiError(await toFailure(response), response.status);
  }

  if (response.status === 204) {
    return null;
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
    throw new GitHubCandidateApiError(await toFailure(response), response.status);
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
