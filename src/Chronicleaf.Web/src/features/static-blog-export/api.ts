import { StaticBlogApiError, type StaticBlogExportStatus, type StaticBlogExportAccepted } from './types';

import type { ApiFailure } from '../background-jobs/types';

/**
 * The only client of the static-blog export routes in this repository.
 *
 * <b>It selects, requests, watches and downloads - it never approves, never
 * generates and never publishes.</b> The POST validates every selected
 * approval before a job exists; the status route reports that job; the
 * download route serves the deterministic archive only while every frozen
 * binding still describes the stored state. No byte here reaches any host
 * but this one: the archive is a private download until its owner deploys
 * it somewhere.
 *
 * <b>Requests are `same-origin`,</b> so the browser attaches the session
 * cookie and this code never sees it, and the request mutation carries a
 * fresh antiforgery value that is used once and dropped. Status reads are
 * GETs and carry none.
 *
 * <b>What this module never sends.</b> `expectedSelectionHash` is always
 * `null`: no route serves the selection hash to pin against, so a
 * client-computed one would be an invented checksum rather than a
 * binding. The server freezes the bindings it resolved; this client
 * names the selection and reads back what the server decided.
 */

/** Asks for a static blog export and returns the job that will build it. */
export async function requestStaticBlogExport(
  projectId: string,
  body: {
    readonly approvedAssetIds: readonly string[];
    readonly basePath: string;
    readonly idempotencyKey: string;
  },
  signal?: AbortSignal,
): Promise<StaticBlogExportAccepted> {
  return send<StaticBlogExportAccepted>(
    `/api/projects/${encodeURIComponent(projectId)}/exports/static-blog`,
    {
      method: 'POST',
      body: {
        approvedAssetIds: [...body.approvedAssetIds],
        basePath: body.basePath,
        expectedSelectionHash: null,
        idempotencyKey: body.idempotencyKey,
      },
      signal,
    },
  );
}

/** Reads one export job the request answered. Never a job that was never asked for. */
export async function readStaticBlogExport(
  projectId: string,
  operationId: string,
  signal?: AbortSignal,
): Promise<StaticBlogExportStatus> {
  return send<StaticBlogExportStatus>(
    `/api/projects/${encodeURIComponent(projectId)}/exports/static-blog/${encodeURIComponent(operationId)}`,
    { method: 'GET', signal },
  );
}

/**
 * Downloads the completed archive's exact bytes.
 *
 * The server answers with the deterministic ZIP as an attachment - or with
 * a refusal naming `approval_stale` or `media_unavailable` when the frozen
 * bindings moved after the job completed. This client never renders the
 * answer: it hands the bytes to a download.
 */
export async function downloadStaticBlogExport(
  projectId: string,
  operationId: string,
  signal?: AbortSignal,
): Promise<{ readonly fileName: string; readonly bytes: Blob }> {
  const antiforgery = await readAntiforgeryValue();

  const response = await fetch(
    `/api/projects/${encodeURIComponent(projectId)}/exports/static-blog/${encodeURIComponent(operationId)}/download`,
    {
      method: 'GET',
      credentials: 'same-origin',
      headers: {
        Accept: 'application/zip',
        [antiforgery.headerName]: antiforgery.token,
      },
      signal,
    },
  );

  if (!response.ok) {
    throw new StaticBlogApiError(await toFailure(response), response.status);
  }

  return {
    fileName: fileNameOf(response),
    bytes: await response.blob(),
  };
}

interface StaticBlogRequest {
  readonly method: 'GET' | 'POST';
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function send<T>(path: string, request: StaticBlogRequest): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // The request route is behind the delivered owner boundary and is a mutation. A fresh
  // antiforgery value per call, used once, exactly as the delivered features do.
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
    throw new StaticBlogApiError(await toFailure(response), response.status);
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
    throw new StaticBlogApiError(await toFailure(response), response.status);
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

function fileNameOf(response: Response): string {
  const disposition = response.headers.get('content-disposition') ?? '';
  const match = /filename="?([^"]+)"?/.exec(disposition);

  return match?.[1] ?? 'chronicleaf-static-blog.zip';
}

/**
 * A fresh idempotency key for one press of the Export button.
 *
 * New per attempt rather than derived from the selection: two exports of the same assets at
 * two different times are two runs the owner asked for, and a key derived from the selection
 * would collapse the second into the first.
 */
export function newIdempotencyKey(): string {
  const random = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;

  return `static-blog-${random}`;
}
