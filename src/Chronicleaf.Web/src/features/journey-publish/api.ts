import type { ApiFailure } from '../background-jobs/types';
import {
  JourneyApiError,
  type JourneyPreview,
  type JourneyPublic,
  type JourneyPublish,
  type JourneyState,
} from './types';

/**
 * The only client of the project journey routes in this repository.
 *
 * It follows the same rules as the delivered capture, evidence, credential and operation
 * clients: requests are `same-origin`, so the browser attaches the session cookie and this
 * code never sees it, and every mutation carries a fresh antiforgery value that is used
 * once and dropped.
 *
 * <b>What the two halves are.</b> The preview and publication routes are private: a
 * selection nobody published can never be read from them. The two public routes are read
 * without a session by the anonymous journey view, and only while every binding still
 * describes the stored state - a revocation or a lapsed approval suspends them as the same
 * 404 a missing slug answers.
 */

/** Computes what a selection would publish, and the hash publication must carry. */
export async function previewJourney(
  projectId: string,
  approvedAssetIds: readonly string[],
  signal?: AbortSignal,
): Promise<JourneyPreview> {
  return write<JourneyPreview>(
    `/api/projects/${encodeURIComponent(projectId)}/journey/preview`,
    { method: 'POST', body: { approvedAssetIds: [...approvedAssetIds] } },
    signal,
  );
}

/** Publishes, republishes or revokes a journey. Freezes the snapshot after every check. */
export async function publishJourney(
  projectId: string,
  publish: JourneyPublish,
  signal?: AbortSignal,
): Promise<JourneyState> {
  return write<JourneyState>(
    `/api/projects/${encodeURIComponent(projectId)}/journey`,
    {
      method: 'PUT',
      body: {
        enabled: publish.enabled,
        slug: publish.slug,
        // The revision travels in the body on this route: a journey that exists without
        // one is the route's own 428, and no If-Match header is read here.
        expectedRevision: publish.expectedRevision,
        selectionHash: publish.selectionHash,
        approvedAssetIds: [...publish.approvedAssetIds],
      },
    },
    signal,
  );
}

/** Reads the stored journey state of one project. */
export async function readJourney(projectId: string, signal?: AbortSignal): Promise<JourneyState> {
  return read<JourneyState>(
    `/api/projects/${encodeURIComponent(projectId)}/journey`,
    { method: 'GET', signal },
  );
}

/** Reads the frozen public projection of one slug. Anonymous: no session, no store. */
export async function readPublicJourney(slug: string, signal?: AbortSignal): Promise<JourneyPublic> {
  return read<JourneyPublic>(`/api/journey/${encodeURIComponent(slug)}`, {
    method: 'GET',
    signal,
  });
}

interface JourneyRequest {
  readonly method: 'GET' | 'POST' | 'PUT';
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function read<T>(path: string, request: JourneyRequest): Promise<T> {
  return send<T>(path, request, undefined);
}

async function write<T>(path: string, request: JourneyRequest, signal?: AbortSignal): Promise<T> {
  return send<T>(path, request, signal);
}

async function send<T>(path: string, request: JourneyRequest, signal?: AbortSignal): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Every private route this client calls is behind the delivered owner boundary. A fresh
  // value per mutation, used once, exactly as the delivered features do. The public read
  // carries no session and no antiforgery value.
  if (request.method !== 'GET' && !path.startsWith('/api/journey/')) {
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
    throw new JourneyApiError(await toFailure(response), response.status);
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
    throw new JourneyApiError(await toFailure(response), response.status);
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
