import {
  LINKEDIN_PACK_TYPE,
  XIAOHONGSHU_PACK_TYPE,
  PackApiError,
  type AdaptedLinkedInCopy,
  type AdaptedXiaohongshuPack,
  type PackFormat,
} from './types';

import type { ApiFailure } from '../background-jobs/types';

export { LINKEDIN_PACK_TYPE, XIAOHONGSHU_PACK_TYPE, PackApiError };

/**
 * The only client of the three pack adapt/refusal routes in this repository.
 *
 * <b>It derives, reads refusals and downloads — it never generates, never
 * approves and never publishes.</b> Deriving re-renders an approved story
 * through a deterministic template, so there is no provider behind these
 * calls and this client holds no credential and no consent. Approval stays
 * the review package's client, bound to the pack's own revision and body
 * hash. Publishing is a refusal route: the POST exists so the server can
 * answer `publisher_unsupported` instead of a 404 that would send the owner
 * looking for another route.
 *
 * <b>Requests are `same-origin`,</b> so the browser attaches the session
 * cookie and this code never sees it, and every mutation carries a fresh
 * antiforgery value that is used once and dropped. The revisions the owner
 * was looking at travel in the derive body, and both must still be current:
 * a body naming a moved head is refused rather than rendered against stale
 * provenance, because a pack of stale content is stale.
 */

/** Derives LinkedIn copy from an approved story. Pictures stay unselected: curation owns selection. */
export async function adaptLinkedIn(
  storyAssetId: string,
  body: {
    readonly evidenceRevision: number;
    readonly voiceRevision: number;
    readonly callToAction: string | null;
    readonly hashtags: readonly string[];
  },
  signal?: AbortSignal,
): Promise<AdaptedLinkedInCopy> {
  return send<AdaptedLinkedInCopy>(
    `/api/assets/${encodeURIComponent(storyAssetId)}/adaptations/linkedin`,
    {
      method: 'POST',
      body: {
        evidenceRevision: body.evidenceRevision,
        voiceRevision: body.voiceRevision,
        callToAction: body.callToAction,
        hashtags: [...body.hashtags],
        pictures: [],
      },
      signal,
    },
  );
}

/** Derives a Xiaohongshu pack from an approved story. Pictures stay unselected: curation owns selection. */
export async function adaptXiaohongshu(
  storyAssetId: string,
  body: {
    readonly evidenceRevision: number;
    readonly voiceRevision: number;
    readonly title: string;
    readonly tags: readonly string[];
    readonly coverCopy: string | null;
    readonly callToAction: string | null;
  },
  signal?: AbortSignal,
): Promise<AdaptedXiaohongshuPack> {
  return send<AdaptedXiaohongshuPack>(
    `/api/assets/${encodeURIComponent(storyAssetId)}/adaptations/xiaohongshu`,
    {
      method: 'POST',
      body: {
        evidenceRevision: body.evidenceRevision,
        voiceRevision: body.voiceRevision,
        title: body.title,
        tags: [...body.tags],
        coverCopy: body.coverCopy,
        callToAction: body.callToAction,
        pictures: [],
      },
      signal,
    },
  );
}

/**
 * Attempts a native publish the contract forbids, so the server can refuse
 * it in its own words. Always answers 422 `publisher_unsupported` (or the
 * delivered 404 for a foreign id) with no account action, no socket and no
 * side effect: the refusal is the representation.
 */
export async function attemptPackPublish(
  assetId: string,
  format: PackFormat,
  signal?: AbortSignal,
): Promise<never> {
  await send<unknown>(
    `/api/assets/${encodeURIComponent(assetId)}/adaptations/${format}/publish`,
    { method: 'POST', body: {}, signal },
  );

  // Unreachable: publication is a refusal, so success has no representation.
  // Throwing here keeps the signature honest — a resolve would read as a send.
  throw new PackApiError(
    {
      code: 'internal_error',
      message: 'The server answered a forbidden publish with success, which the contract forbids.',
      traceId: '',
    },
    0,
  );
}

/**
 * Downloads the approved pack's exact bytes, in the backend's own shape.
 *
 * The server answers with the stored bytes as an attachment — or with a 409
 * naming `approval_required`, `approval_stale` or `media_unavailable`. This
 * client never renders the answer as a document: it hands the bytes to a
 * download, so markup in them stays inert on the way out as well.
 */
export async function downloadPack(
  assetId: string,
  format: PackFormat,
  revision: number | null,
  signal?: AbortSignal,
): Promise<{ readonly fileName: string; readonly bytes: Blob }> {
  const revisionQuery = revision === null ? '' : `&revision=${revision}`;

  const response = await fetch(
    `/api/assets/${encodeURIComponent(assetId)}/export?format=${format}${revisionQuery}`,
    {
      method: 'GET',
      credentials: 'same-origin',
      headers: { Accept: 'application/zip, text/plain' },
      signal,
    },
  );

  if (!response.ok) {
    throw new PackApiError(await toFailure(response), response.status);
  }

  return {
    fileName: fileNameOf(response, format),
    bytes: await response.blob(),
  };
}

/** Hands downloaded pack bytes to a file the owner chose to save. */
export function savePackDownload(fileName: string, bytes: Blob): void {
  const url = URL.createObjectURL(bytes);

  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    // The bytes are the server's attachment, handed to a file: revoking here
    // would race the click above on some browsers, so the object URL lives as
    // long as the page rather than being cut out from under the download.
  }
}

interface PackRequest {
  readonly method: 'POST';
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function send<T>(path: string, request: PackRequest): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Every route this client calls is behind the delivered owner boundary, and
  // both are mutations. A fresh antiforgery value per call, used once,
  // exactly as the delivered features do.
  const metadata = await readAntiforgeryValue();
  headers[metadata.headerName] = metadata.token;

  const response = await fetch(path, {
    method: request.method,
    credentials: 'same-origin',
    headers,
    body: request.body === undefined ? undefined : JSON.stringify(request.body),
    signal: request.signal,
  });

  if (!response.ok) {
    throw new PackApiError(await toFailure(response), response.status);
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
    throw new PackApiError(await toFailure(response), response.status);
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

function fileNameOf(response: Response, format: PackFormat): string {
  const disposition = response.headers.get('content-disposition') ?? '';
  const match = /filename="?([^"]+)"?/.exec(disposition);

  return match?.[1] ?? `chronicleaf-export.${format === 'medium' ? 'zip' : 'txt'}`;
}
