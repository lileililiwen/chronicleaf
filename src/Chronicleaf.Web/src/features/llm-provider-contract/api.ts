import type {
  AiProviderCatalogue,
  AiSettingsResolution,
  ApiFailure,
  EvidenceHead,
  GenerationPreview,
  GenerationRun,
} from './types';

/**
 * The only client of the generation routes in this repository.
 *
 * It follows the same rules as the delivered credential, evidence and operation clients: requests are
 * `same-origin`, so the browser attaches the session cookie and this code never sees it, and every
 * mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * What is different here is what this client may hold. A consent hash lives in one call's arguments
 * and in the component state that is immediately cleared after the call; it is a binding over an
 * authorized input, not a credential, and a browser that kept one would be a browser holding a
 * permission nobody granted it beyond the fifteen minutes the server honours. Nothing here reads a
 * provider response directly: the run comes back through the server, which is where the credential
 * lease, the bound and the redaction live.
 */
export class GenerationApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'GenerationApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /**
   * True when the consent no longer covers the input and a new preview is required.
   *
   * Distinct from a validation failure and from a provider failure on purpose: nothing is wrong with
   * the request, and the answer is a fresh preview rather than a corrected body. The surface sends
   * the owner back to the preview step instead of showing an error they cannot act on.
   */
  get isConsentStale(): boolean {
    return this.code === 'consent_stale';
  }

  /** True when the preview's window has closed, which the owner can only fix by previewing again. */
  get isExpired(): boolean {
    return this.isConsentStale;
  }

  /** True when this deployment has no adapter for the selected provider. */
  get isProviderNotInstalled(): boolean {
    return this.code === 'provider_not_installed';
  }

  /**
   * True when the provider did not answer inside its budget.
   *
   * Worth distinguishing because the call may or may not have been billed: the surface says so rather
   * than inviting a retry that would spend the owner's money twice.
   */
  get isTimeout(): boolean {
    return this.code === 'generation_timeout';
  }

  /** True when the provider is throttling, which is not this application's rate limiter. */
  get isRateLimited(): boolean {
    return this.code === 'rate_limited';
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}

/**
 * Reads the provider catalogue.
 *
 * The two lists come back separately and are not merged here: "this scope has a credential" and
 * "this build can call a provider" are different questions, and a client that answered one of them
 * would be showing an owner a green tick for a provider nothing can reach.
 */
export async function readProviders(signal?: AbortSignal): Promise<AiProviderCatalogue> {
  return send<AiProviderCatalogue>('/api/ai/providers', { method: 'GET', signal });
}

/**
 * Resolves an owner's provider selection.
 *
 * A resolution, not a save. It is a pre-flight check the owner runs before they build a preview, and
 * it is where an unknown model or a provider with no adapter is refused - before there is a redacted
 * snapshot on screen to authorize, and therefore before anything has been spent.
 */
export async function resolveSettings(
  request: {
    readonly providerId: string;
    readonly model: string;
    readonly credentialRef?: string | null;
    readonly endpointRef?: string | null;
  },
  signal?: AbortSignal,
): Promise<AiSettingsResolution> {
  return send<AiSettingsResolution>('/api/settings/ai', {
    method: 'POST',
    body: {
      providerId: request.providerId,
      model: request.model,
      credentialRef: request.credentialRef ?? null,
      endpointRef: request.endpointRef ?? null,
    },
    signal,
  });
}

/**
 * Reads a milestone's current evidence head, which a preview must name by revision.
 *
 * The revision is read rather than remembered on purpose. A tab left open while the evidence moves on
 * would otherwise authorize input the owner never saw, and the server would refuse it with a message
 * the surface could not have prevented.
 */
export async function readEvidenceHead(
  milestoneId: string,
  signal?: AbortSignal,
): Promise<EvidenceHead> {
  const head = await send<{ items: readonly { revision: number; items: readonly never[] }[] }>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/evidence`,
    { method: 'GET', signal },
  );

  const bundle = head.items[0];

  return {
    revision: bundle?.revision ?? 0,
    items:
      bundle === undefined
        ? []
        : (bundle.items as unknown as EvidenceHead['items']).map((item) => ({
            id: item.id as string,
            kind: item.kind as string,
            sourceRef: item.sourceRef as string,
            text: item.text as string,
            redactions: (item.redactions as readonly string[]) ?? [],
            availability: item.availability as string,
          })),
  };
}

/**
 * Freezes the redacted input for a milestone story and returns the owner's authorization to it.
 *
 * This is the milestone route, which is exactly the generic route with `purpose: 'milestone-story'`
 * and this milestone as the aggregate. The client calls the milestone route so that the two cannot
 * drift into disagreeing about what a preview is.
 */
export async function createMilestonePreview(
  request: {
    readonly milestoneId: string;
    readonly providerId: string;
    readonly model: string;
    readonly credentialRef: string | null;
    readonly evidenceRevision: number;
    readonly selectedEvidenceIds: readonly string[];
  },
  signal?: AbortSignal,
): Promise<GenerationPreview> {
  return send<GenerationPreview>(
    `/api/milestones/${encodeURIComponent(request.milestoneId)}/generation-preview`,
    {
      method: 'POST',
      body: {
        providerId: request.providerId,
        model: request.model,
        credentialRef: request.credentialRef,
        evidenceRevision: request.evidenceRevision,
        selectedEvidenceIds: [...request.selectedEvidenceIds],
      },
      signal,
    },
  );
}

/**
 * Makes the one bounded call the owner authorized.
 *
 * The consent hash is sent once and the caller drops it: it authorizes one call, and the window on it
 * is the server's fifteen minutes, not this component's lifetime.
 */
export async function generate(
  request: {
    readonly previewId: string;
    readonly consentHash: string;
  },
  signal?: AbortSignal,
): Promise<GenerationRun> {
  return send<GenerationRun>(
    `/api/generation-previews/${encodeURIComponent(request.previewId)}/generate`,
    { method: 'POST', body: { consentHash: request.consentHash }, signal },
  );
}

interface Request {
  readonly method: string;
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function send<T>(path: string, request: Request): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Every route this client calls is behind the delivered boundary, and every one of them except the
  // catalogue read is a mutation. A fresh value per call, used once, exactly as the other features do.
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
    throw new GenerationApiError(await toFailure(response), response.status);
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
    throw new GenerationApiError(await toFailure(response), response.status);
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
