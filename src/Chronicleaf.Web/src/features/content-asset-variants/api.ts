import { VariantApiError, type DerivedVariant, type VariantSummary } from './types';

import type { ApiFailure } from '../background-jobs/types';
import type { ContentAsset } from '../grounded-markdown-drafts/types';

/**
 * The only client of the variant routes in this repository.
 *
 * <b>It derives and reads - it never generates, never approves and never publishes.</b> Deriving
 * re-renders an approved story's accepted summary through a deterministic template, so there is
 * no provider behind these calls and this client holds no credential and no consent. Approval
 * stays the review package's client, bound to the variant's own revision and body hash.
 *
 * <b>Requests are `same-origin`,</b> so the browser attaches the session cookie and this code
 * never sees it, and every mutation carries a fresh antiforgery value that is used once and
 * dropped. The revisions the owner was looking at travel in the derive body, and both must still
 * be current: a body naming a moved head is refused rather than rendered against stale
 * provenance, because a variant of stale content is stale.
 */

/** Lists the variant assets a milestone holds, in picker order. */
export async function listVariants(
  milestoneId: string,
  signal?: AbortSignal,
): Promise<readonly VariantSummary[]> {
  const answer = await send<{ readonly variants: readonly VariantSummary[] }>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/assets`,
    { method: 'GET', signal },
  );

  return answer.variants;
}

/**
 * Derives one variant format from an approved story and stores it as a draft asset.
 *
 * The source is named only when it is not the milestone's own story. The evidence and voice
 * revisions are the heads the owner was looking at; the server refuses when either has moved.
 */
export async function deriveVariant(
  milestoneId: string,
  body: {
    readonly type: string;
    readonly sourceAssetId: string | null;
    readonly evidenceRevision: number;
    readonly voiceRevision: number;
  },
  signal?: AbortSignal,
): Promise<DerivedVariant> {
  return send<DerivedVariant>(`/api/milestones/${encodeURIComponent(milestoneId)}/assets`, {
    method: 'POST',
    body: {
      type: body.type,
      sourceAssetId: body.sourceAssetId,
      evidenceRevision: body.evidenceRevision,
      voiceRevision: body.voiceRevision,
    },
    signal,
  });
}

/** Reads one variant asset with its head revision, its claims and both finding lists. */
export async function readVariant(
  assetId: string,
  signal?: AbortSignal,
): Promise<ContentAsset> {
  return send<ContentAsset>(`/api/assets/${encodeURIComponent(assetId)}`, {
    method: 'GET',
    signal,
  });
}

interface VariantRequest {
  readonly method: 'GET' | 'POST';
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function send<T>(path: string, request: VariantRequest): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Every route this client calls is behind the delivered owner boundary, and deriving is a
  // mutation. A fresh antiforgery value per call, used once, exactly as the delivered features do.
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
    throw new VariantApiError(await toFailure(response), response.status);
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
    throw new VariantApiError(await toFailure(response), response.status);
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
