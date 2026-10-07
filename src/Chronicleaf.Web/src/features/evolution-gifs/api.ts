import type { ApiFailure } from '../background-jobs/types';
import {
  EvolutionGifApiError,
  type EvolutionGif,
  type EvolutionGifList,
  type RenderEvolutionGif,
} from './types';

/**
 * The only client of the evolution GIF routes in this repository.
 *
 * It follows the same rules as the delivered capture, evidence, credential and operation
 * clients: requests are `same-origin`, so the browser attaches the session cookie and this
 * code never sees it, and every mutation carries a fresh antiforgery value that is used
 * once and dropped.
 *
 * <b>What this module never synthesises.</b> No animation is built client-side. The render
 * answers with the artifact, not a job, because rendering is deterministic. Preview is
 * reviewable - as the animation, or with `?mode=static` as the exact first frame playback
 * starts with - and download serves the exact approved bytes only while a live approval
 * still describes the stored selection; the panel links to those routes.
 */

/** Renders an animation of the named milestones. Answers 201 with the artifact, not a job. */
export async function renderEvolutionGif(
  projectId: string,
  render: RenderEvolutionGif,
  signal?: AbortSignal,
): Promise<EvolutionGif> {
  return write<EvolutionGif>(
    `/api/projects/${encodeURIComponent(projectId)}/evolution/gifs`,
    {
      method: 'POST',
      body: {
        milestoneIds: [...render.milestoneIds],
        loop: render.loop,
        expectedSelectionHash: render.expectedSelectionHash ?? null,
        idempotencyKey: render.idempotencyKey ?? null,
      },
    },
    signal,
  );
}

/** Lists a project's rendered animations. */
export async function listEvolutionGifs(
  projectId: string,
  signal?: AbortSignal,
): Promise<EvolutionGifList> {
  return read<EvolutionGifList>(
    `/api/projects/${encodeURIComponent(projectId)}/evolution/gifs`,
    { method: 'GET', signal },
  );
}

/** Reads one rendered animation. */
export async function readEvolutionGif(
  gifId: string,
  signal?: AbortSignal,
): Promise<EvolutionGif> {
  return read<EvolutionGif>(
    `/api/evolution/gifs/${encodeURIComponent(gifId)}`,
    { method: 'GET', signal },
  );
}

/** Approves one rendered animation against the manifest hash that was reviewed. */
export async function approveEvolutionGif(
  gifId: string,
  expectedSelectionHash: string,
  signal?: AbortSignal,
): Promise<EvolutionGif> {
  return write<EvolutionGif>(
    `/api/evolution/gifs/${encodeURIComponent(gifId)}/approve`,
    { method: 'POST', body: { expectedSelectionHash } },
    signal,
  );
}

/** The preview route: the animation, as the server stored it. */
export function previewEvolutionGifUrl(gifId: string): string {
  return `/api/evolution/gifs/${encodeURIComponent(gifId)}/preview`;
}

/** The static preview route: the exact first frame playback starts with. */
export function previewEvolutionGifStaticUrl(gifId: string): string {
  return `/api/evolution/gifs/${encodeURIComponent(gifId)}/preview?mode=static`;
}

/** The download route: the exact approved bytes, gated on a live approval. */
export function downloadEvolutionGifUrl(gifId: string): string {
  return `/api/evolution/gifs/${encodeURIComponent(gifId)}/download`;
}

interface GifRequest {
  readonly method: 'GET' | 'POST';
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function read<T>(path: string, request: GifRequest): Promise<T> {
  return send<T>(path, request, undefined);
}

async function write<T>(path: string, request: GifRequest, signal?: AbortSignal): Promise<T> {
  return send<T>(path, request, signal);
}

async function send<T>(path: string, request: GifRequest, signal?: AbortSignal): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Every route this client calls is behind the delivered owner boundary. A fresh value per
  // mutation, used once, exactly as the delivered features do.
  if (request.method !== 'GET') {
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
    throw new EvolutionGifApiError(await toFailure(response), response.status);
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
    throw new EvolutionGifApiError(await toFailure(response), response.status);
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
