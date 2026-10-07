import type { ApiFailure } from '../background-jobs/types';
import {
  EvolutionImageApiError,
  type EvolutionImage,
  type EvolutionImageList,
  type RenderEvolutionImage,
} from './types';

/**
 * The only client of the evolution image routes in this repository.
 *
 * It follows the same rules as the delivered capture, evidence, credential and operation
 * clients: requests are `same-origin`, so the browser attaches the session cookie and this
 * code never sees it, and every mutation carries a fresh antiforgery value that is used
 * once and dropped.
 *
 * <b>What this module never depicts.</b> Candidates are never compared and no route here
 * promotes one. The render answers with the artifact, not a job, because rendering is
 * deterministic. Preview is reviewable and download serves the exact approved bytes only
 * while a live approval still describes the stored selection; the panel links to those
 * routes rather than synthesising any picture.
 */

/** Renders a comparison of the named milestones. Answers 201 with the artifact, not a job. */
export async function renderEvolutionImage(
  projectId: string,
  render: RenderEvolutionImage,
  signal?: AbortSignal,
): Promise<EvolutionImage> {
  return write<EvolutionImage>(
    `/api/projects/${encodeURIComponent(projectId)}/evolution/images`,
    {
      method: 'POST',
      body: {
        milestoneIds: [...render.milestoneIds],
        layout: render.layout,
        selectedScreenshotIds: render.selectedScreenshotIds ?? null,
        expectedSelectionHash: render.expectedSelectionHash ?? null,
        idempotencyKey: render.idempotencyKey ?? null,
      },
    },
    signal,
  );
}

/** Lists a project's rendered comparisons. */
export async function listEvolutionImages(
  projectId: string,
  signal?: AbortSignal,
): Promise<EvolutionImageList> {
  return read<EvolutionImageList>(
    `/api/projects/${encodeURIComponent(projectId)}/evolution/images`,
    { method: 'GET', signal },
  );
}

/** Reads one rendered comparison. */
export async function readEvolutionImage(
  evolutionId: string,
  signal?: AbortSignal,
): Promise<EvolutionImage> {
  return read<EvolutionImage>(
    `/api/evolution/images/${encodeURIComponent(evolutionId)}`,
    { method: 'GET', signal },
  );
}

/** Approves one rendered comparison against the manifest hash that was reviewed. */
export async function approveEvolutionImage(
  evolutionId: string,
  expectedSelectionHash: string,
  signal?: AbortSignal,
): Promise<EvolutionImage> {
  return write<EvolutionImage>(
    `/api/evolution/images/${encodeURIComponent(evolutionId)}/approve`,
    { method: 'POST', body: { expectedSelectionHash } },
    signal,
  );
}

/** The preview route: the draft bytes, viewable so an owner can approve what they see. */
export function previewEvolutionImageUrl(evolutionId: string): string {
  return `/api/evolution/images/${encodeURIComponent(evolutionId)}/preview`;
}

/** The download route: the exact approved bytes, gated on a live approval. */
export function downloadEvolutionImageUrl(evolutionId: string): string {
  return `/api/evolution/images/${encodeURIComponent(evolutionId)}/download`;
}

interface EvolutionRequest {
  readonly method: 'GET' | 'POST';
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function read<T>(path: string, request: EvolutionRequest): Promise<T> {
  return send<T>(path, request, undefined);
}

async function write<T>(path: string, request: EvolutionRequest, signal?: AbortSignal): Promise<T> {
  return send<T>(path, request, signal);
}

async function send<T>(path: string, request: EvolutionRequest, signal?: AbortSignal): Promise<T> {
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
    throw new EvolutionImageApiError(await toFailure(response), response.status);
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
    throw new EvolutionImageApiError(await toFailure(response), response.status);
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
