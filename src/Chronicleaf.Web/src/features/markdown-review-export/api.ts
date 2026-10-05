import { ReviewApiError, type ContentReview, type EditedRevision } from './types';

import type { ApiFailure } from '../background-jobs/types';

/**
 * The only client of the review routes in this repository.
 *
 * <b>It edits, approves and exports - it never generates and never publishes.</b> An edit
 * appends a human revision beside the generated ones, an approval binds a revision and a body
 * hash, and an export downloads approved bytes as an attachment. There is no provider behind any
 * of these calls, so this client holds no credential and no consent.
 *
 * <b>Requests are `same-origin`,</b> so the browser attaches the session cookie and this code
 * never sees it, and every mutation carries a fresh antiforgery value that is used once and
 * dropped. The edit additionally carries the head revision in `If-Match`, because an edit
 * against a stale head would fork the text the approval was bound to.
 */

/** Reads the review state of one asset. */
export async function readReview(assetId: string, signal?: AbortSignal): Promise<ContentReview> {
  return send<ContentReview>(`/api/assets/${encodeURIComponent(assetId)}/review`, {
    method: 'GET',
    signal,
  });
}

/**
 * Appends a human revision carrying the owner's edited body and title.
 *
 * The expected revision travels twice - in `If-Match` and in the body - and the server refuses
 * the call when the two disagree, so a panel that built one from a stale read cannot fork the
 * text. A missing `If-Match` is a 428 the server answers before anything is written.
 */
export async function editAsset(
  assetId: string,
  expectedRevision: number,
  body: { readonly bodyMarkdown: string; readonly title: string | null },
  signal?: AbortSignal,
): Promise<EditedRevision> {
  return send<EditedRevision>(`/api/assets/${encodeURIComponent(assetId)}`, {
    method: 'PATCH',
    ifMatch: String(expectedRevision),
    body: {
      expectedRevision,
      bodyMarkdown: body.bodyMarkdown,
      title: body.title,
    },
    signal,
  });
}

/**
 * Records a human approval of the head revision against the head evidence.
 *
 * Both revisions are named because both must be current. The approval control that calls this
 * is a button only a press can reach - it is never a form submission, so Enter in the editor
 * cannot approve.
 */
export async function approveAsset(
  assetId: string,
  body: { readonly contentRevision: number; readonly evidenceRevision: number },
  signal?: AbortSignal,
): Promise<ContentReview['approval']> {
  return send(`/api/assets/${encodeURIComponent(assetId)}/approve`, {
    method: 'POST',
    body: {
      contentRevision: body.contentRevision,
      evidenceRevision: body.evidenceRevision,
      voiceFindingsAcknowledged: false,
    },
    signal,
  });
}

/** Withdraws the live approval, keeping its row as history. */
export async function unapproveAsset(
  assetId: string,
  reason: string | null,
  signal?: AbortSignal,
): Promise<ContentReview['approval']> {
  return send(`/api/assets/${encodeURIComponent(assetId)}/unapprove`, {
    method: 'POST',
    body: { reason },
    signal,
  });
}

/**
 * Downloads the approved revision's exact bytes.
 *
 * The server answers with the stored bytes as a Markdown attachment, or with a 409 naming
 * `approval_required` or `approval_stale`. This client never renders the answer as a document:
 * it hands the bytes to a download, so markup in them stays inert on the way out as well.
 */
export async function exportAsset(
  assetId: string,
  revision: number | null,
  signal?: AbortSignal,
): Promise<{ readonly fileName: string; readonly bytes: Blob }> {
  const query = revision === null ? 'format=markdown' : `format=markdown&revision=${revision}`;
  const headers: Record<string, string> = { Accept: 'text/markdown' };

  const antiforgery = await readAntiforgeryValue();
  headers[antiforgery.headerName] = antiforgery.token;

  const response = await fetch(`/api/assets/${encodeURIComponent(assetId)}/export?${query}`, {
    method: 'GET',
    credentials: 'same-origin',
    headers,
    signal,
  });

  if (!response.ok) {
    throw new ReviewApiError(await toFailure(response), response.status);
  }

  return {
    fileName: fileNameOf(response),
    bytes: await response.blob(),
  };
}

interface ReviewRequest {
  readonly method: 'GET' | 'POST' | 'PATCH';
  readonly ifMatch?: string;
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function send<T>(path: string, request: ReviewRequest): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  if (request.ifMatch !== undefined) {
    headers['If-Match'] = request.ifMatch;
  }

  // Every route this client calls is behind the delivered owner boundary, and the edit, approve
  // and unapprove routes are mutations. A fresh antiforgery value per call, used once, exactly
  // as the delivered features do.
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
    throw new ReviewApiError(await toFailure(response), response.status);
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
    throw new ReviewApiError(await toFailure(response), response.status);
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

  return match?.[1] ?? 'chronicleaf-export.md';
}
