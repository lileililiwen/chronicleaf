import {
  VideoStoryApiError,
  type GeneratedVideoStory,
  type VideoStoryDuration,
  type VideoStorySummary,
} from './types';

import type { ApiFailure } from '../background-jobs/types';

export { VideoStoryApiError };

/**
 * The only client of the video-story routes in this repository.
 *
 * <b>It scripts, reads, downloads and is refused — it never synthesizes,
 * never renders, never approves and never publishes.</b> Scripting stores
 * the owner's own shot list against the approved story, the frozen evidence
 * head and the current voice, so there is no provider behind these calls
 * and this client holds no credential and no consent. Approval stays the
 * review package's client, bound to the script's own revision and body hash.
 * Publication is a refusal route: the POST exists so the server can answer
 * `publisher_unsupported` instead of a 404 that would send the owner looking
 * for another route.
 *
 * <b>Requests are `same-origin`,</b> so the browser attaches the session
 * cookie and this code never sees it, and every mutation carries a fresh
 * antiforgery value that is used once and dropped. The revisions the owner
 * was looking at travel in the generate body, and all must still be
 * current: a body naming a moved head is refused rather than scripted
 * against provenance nobody saw.
 */

/** Discovers the milestone's video-story asset, or a null asset when it holds no script yet. */
export async function readScript(
  milestoneId: string,
  signal?: AbortSignal,
): Promise<VideoStorySummary> {
  return send<VideoStorySummary>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/video-story`,
    { method: 'GET', signal },
  );
}

/**
 * Scripts the owner's shot list into a storyboard revision.
 *
 * Timings travel with every shot because the server demands exact contiguous
 * coverage; the composer derives them (see `deriveTimings`) rather than
 * asking the owner to type milliseconds that must add up.
 */
export async function generateScript(
  milestoneId: string,
  body: {
    readonly durationSeconds: VideoStoryDuration;
    readonly sourceAssetId: string;
    readonly evidenceRevision: number;
    readonly voiceRevision: number;
    readonly shots: readonly {
      readonly screenshotMediaId: string;
      readonly caption: string;
      readonly narration: string;
      readonly transition: string;
      readonly startMs: number;
      readonly endMs: number;
      readonly sourceEvidenceIds: readonly string[];
    }[];
  },
  signal?: AbortSignal,
): Promise<GeneratedVideoStory> {
  return send<GeneratedVideoStory>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/video-stories`,
    {
      method: 'POST',
      body: {
        durationSeconds: body.durationSeconds,
        sourceAssetId: body.sourceAssetId,
        evidenceRevision: body.evidenceRevision,
        voiceRevision: body.voiceRevision,
        shots: body.shots.map((shot) => ({ ...shot, sourceEvidenceIds: [...shot.sourceEvidenceIds] })),
      },
      signal,
    },
  );
}

/**
 * Attempts a native video publish the contract forbids, so the server can
 * refuse it in its own words. Always answers 422 `publisher_unsupported`
 * (or the delivered 404 for a foreign id) with no account action, no
 * socket and no side effect: the refusal is the representation.
 */
export async function attemptVideoPublish(
  assetId: string,
  signal?: AbortSignal,
): Promise<never> {
  await send<unknown>(`/api/assets/${encodeURIComponent(assetId)}/video-story/publish`, {
    method: 'POST',
    body: {},
    signal,
  });

  // Unreachable: publication is a refusal, so success has no representation.
  // Throwing here keeps the signature honest — a resolve would read as a send.
  throw new VideoStoryApiError(
    {
      code: 'internal_error',
      message: 'The server answered a forbidden publish with success, which the contract forbids.',
      traceId: '',
    },
    0,
  );
}

/**
 * Downloads the approved script's exact bytes, in the backend's own ZIP shape.
 *
 * The server answers with the stored package as an attachment — or with a
 * 409 naming `approval_required`, `approval_stale` or `media_unavailable`.
 * This client never renders the answer as a document: it hands the bytes to
 * a download, so markup in them stays inert on the way out as well.
 */
export async function downloadScript(
  assetId: string,
  revision: number | null,
  signal?: AbortSignal,
): Promise<{ readonly fileName: string; readonly bytes: Blob }> {
  const revisionQuery = revision === null ? '' : `&revision=${revision}`;

  const response = await fetch(
    `/api/assets/${encodeURIComponent(assetId)}/export?format=video-story${revisionQuery}`,
    {
      method: 'GET',
      credentials: 'same-origin',
      headers: { Accept: 'application/zip' },
      signal,
    },
  );

  if (!response.ok) {
    throw new VideoStoryApiError(await toFailure(response), response.status);
  }

  return {
    fileName: fileNameOf(response),
    bytes: await response.blob(),
  };
}

/** Hands downloaded script bytes to a file the owner chose to save. */
export function saveScriptDownload(fileName: string, bytes: Blob): void {
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

interface VideoStoryRequest {
  readonly method: 'GET' | 'POST';
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function send<T>(path: string, request: VideoStoryRequest): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Every route this client calls is behind the delivered owner boundary, and
  // scripting is a mutation. A fresh antiforgery value per call, used once,
  // exactly as the delivered features do.
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
    throw new VideoStoryApiError(await toFailure(response), response.status);
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
    throw new VideoStoryApiError(await toFailure(response), response.status);
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

  return match?.[1] ?? 'chronicleaf-video-story.zip';
}
