import type { ApiFailure, Operation } from '../background-jobs/types';
import type { AiProviderCatalogue, GenerationPreview } from '../llm-provider-contract/types';
import { DraftApiError, type ContentAsset, type DraftAccepted } from './types';

/**
 * The only client of the grounded-draft routes in this repository.
 *
 * <b>It drives the whole consent handshake itself.</b> A draft is not "the generation panel's result,
 * pointed at an asset" - it is a separate question with a separate set of refusals, and an owner should
 * not have to find a different panel to authorize one. So this client calls the delivered
 * generation-preview route to freeze the input and get the consent binding, then this package's own
 * request route with that binding. The preview is a value for the length of one press of the button and
 * is never cached: a consent is a fifteen-minute authorization over a specific frozen input, and a stored
 * one would go stale silently and then be presented as current.
 *
 * <b>Requests are `same-origin`,</b> so the browser attaches the session cookie and this code never sees
 * it, and every mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * <b>What this module may hold.</b> An asset's body, its claims and its findings - a draft the owner has
 * not approved, read back to look at. What it never holds is a credential, a provider response, or a
 * stored consent.
 */

/** Reads the provider catalogue, so the form offers only slots this deployment can reach. */
export async function readProviders(signal?: AbortSignal): Promise<AiProviderCatalogue> {
  return send<AiProviderCatalogue>('/api/ai/providers', { method: 'GET', signal });
}

/**
 * Reads the evidence this milestone has, for the selection a draft would be grounded in.
 *
 * Only the four fields a draft form needs, and read from the delivered evidence route rather than from
 * a second one: a draft that chose from a different view of the evidence would be choosing from
 * something the server never froze.
 */
export async function readEvidenceHead(
  milestoneId: string,
  signal?: AbortSignal,
): Promise<{ readonly revision: number; readonly items: readonly DraftEvidenceItem[] }> {
  return send(
    `/api/milestones/${encodeURIComponent(milestoneId)}/evidence`,
    { method: 'GET', signal },
  );
}

/** One evidence item, as far as a draft form needs to know about it. */
export interface DraftEvidenceItem {
  readonly id: string;
  readonly kind: string;
  readonly text: string;
  readonly availability: string;
}

/** Reads the asset a milestone holds, or `null` when it has none. Never a 404. */
export async function readDraft(
  milestoneId: string,
  signal?: AbortSignal,
): Promise<ContentAsset | null> {
  const asset = await send<ContentAsset>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/drafts`,
    { method: 'GET', signal },
  );

  return asset.assetId === null ? null : asset;
}

/**
 * Freezes the authorized input for a draft and returns the owner's binding to it.
 *
 * The delivered handshake, called from here so the owner sees one flow rather than two. Its own
 * refusals are the generation contract's, and this client passes them through unchanged rather than
 * translating them: a caller that renamed them would be publishing a second vocabulary for one condition.
 */
export async function authorize(
  milestoneId: string,
  selection: {
    readonly providerId: string;
    readonly model: string;
    readonly evidenceRevision: number;
    readonly selectedEvidenceIds: readonly string[];
  },
  signal?: AbortSignal,
): Promise<GenerationPreview> {
  return send<GenerationPreview>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/generation-preview`,
    {
      method: 'POST',
      body: {
        purpose: 'milestone-story',
        providerId: selection.providerId,
        model: selection.model,
        evidenceRevision: selection.evidenceRevision,
        selectedEvidenceIds: [...selection.selectedEvidenceIds],
      },
      signal,
    },
  );
}

/**
 * Asks for a draft and returns the job that will produce it.
 *
 * A 202 and an operation id, never a draft. The work is two provider calls, a repair call possibly, and
 * two local validation passes, and it happens in the delivered queue - so this call reports that the
 * request was accepted and nothing more. The panel follows the job through the delivered operation route
 * rather than deciding on its own that the draft finished.
 *
 * The provider and model are sent as well as the preview, because the server refuses a body that
 * disagrees with the authorized row rather than picking one. The consent binding is not a credential and
 * is held for this one call.
 */
export async function requestDraft(
  milestoneId: string,
  preview: GenerationPreview,
  idempotencyKey: string,
  signal?: AbortSignal,
): Promise<DraftAccepted> {
  return send<DraftAccepted>(`/api/milestones/${encodeURIComponent(milestoneId)}/drafts`, {
    method: 'POST',
    body: {
      previewId: preview.previewId,
      consentHash: preview.consentHash,
      providerId: preview.providerId,
      model: preview.model,
      evidenceRevision: preview.evidenceRevision,
      voiceRevision: preview.voiceRevision,
      idempotencyKey,
    },
    signal,
  });
}

/** Reads one job from the delivered operations route. */
export async function readDraftOperation(
  operationId: string,
  signal?: AbortSignal,
): Promise<Operation> {
  return send<Operation>(`/api/operations/${encodeURIComponent(operationId)}`, { method: 'GET', signal });
}

interface DraftRequest {
  readonly method: 'GET' | 'POST';
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function send<T>(path: string, request: DraftRequest): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Every route this client calls is behind the delivered owner boundary, and both the handshake and
  // the draft request are mutations. A fresh antiforgery value per call, used once, exactly as the
  // delivered features do.
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
    throw new DraftApiError(await toFailure(response), response.status);
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
    throw new DraftApiError(await toFailure(response), response.status);
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
 * A fresh idempotency key for one press of the button.
 *
 * New per attempt rather than derived from the form: two drafts of the same milestone at two different
 * times are two runs the owner asked for, and a key derived from the milestone would collapse the
 * second into the first.
 */
export function newIdempotencyKey(): string {
  const random = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;

  return `draft-${random}`;
}
