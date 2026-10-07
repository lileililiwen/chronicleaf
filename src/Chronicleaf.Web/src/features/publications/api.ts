import type { ApiFailure } from '../background-jobs/types';
import {
  PublicationApiError,
  type PublicationReceipt,
  type PublishAccepted,
  type PublishPreview,
  type PublishRequest,
  type PublisherTargetList,
} from './types';

/**
 * The only client of the publisher-contract routes in this repository.
 *
 * It follows the same rules as the delivered capture, evidence, credential and operation
 * clients: requests are `same-origin`, so the browser attaches the session cookie and this
 * code never sees it, and every mutation carries a fresh antiforgery value that is used
 * once and dropped.
 *
 * <b>What this module never sends.</b> The preview answers what a send would do without
 * sending anything; the publish validates the live approval, the confirmed preview and
 * the credential metadata before a job exists. No byte here reaches a remote side except
 * through the one adapter call the queued run makes: the routes authorize and report, and
 * the run sends. Publishing is therefore always an explicit owner act with a separate,
 * explicit confirmation - never a side effect of rendering.
 */

/** Lists the classified publisher targets, in registry order. A read, never a send. */
export async function listTargets(signal?: AbortSignal): Promise<PublisherTargetList> {
  return read<PublisherTargetList>(`/api/publisher-targets`, { method: 'GET', signal });
}

/** Answers what a send would do, without sending anything. */
export async function previewPublish(
  assetId: string,
  targetId: string,
  mode: string,
  signal?: AbortSignal,
): Promise<PublishPreview> {
  return write<PublishPreview>(
    `/api/assets/${encodeURIComponent(assetId)}/publish-preview`,
    { method: 'POST', body: { targetId, mode } },
    signal,
  );
}

/** Sends only what the stored rows still describe. Answers 202 with the watched operation. */
export async function publishAsset(
  assetId: string,
  request: PublishRequest,
  signal?: AbortSignal,
): Promise<PublishAccepted | PublicationReceipt> {
  return write<PublishAccepted | PublicationReceipt>(
    `/api/assets/${encodeURIComponent(assetId)}/publish`,
    {
      method: 'POST',
      body: {
        targetId: request.targetId,
        mode: request.mode,
        previewHash: request.previewHash,
        approvalId: request.approvalId,
        idempotencyKey: request.idempotencyKey,
        confirm: request.confirm,
      },
    },
    signal,
  );
}

/** Reads one stored receipt: what was sent where and what the remote side answered. */
export async function readReceipt(
  attemptId: string,
  signal?: AbortSignal,
): Promise<PublicationReceipt> {
  return read<PublicationReceipt>(
    `/api/publications/${encodeURIComponent(attemptId)}`,
    { method: 'GET', signal },
  );
}

/** Asks the target to resolve an ambiguous receipt where the target permits it. */
export async function reconcileReceipt(
  attemptId: string,
  signal?: AbortSignal,
): Promise<PublicationReceipt> {
  return write<PublicationReceipt>(
    `/api/publications/${encodeURIComponent(attemptId)}/reconcile`,
    { method: 'POST' },
    signal,
  );
}

interface PublicationRequest {
  readonly method: 'GET' | 'POST';
  readonly body?: unknown;
  readonly signal?: AbortSignal;
}

async function read<T>(path: string, request: PublicationRequest): Promise<T> {
  return send<T>(path, request, undefined);
}

async function write<T>(path: string, request: PublicationRequest, signal?: AbortSignal): Promise<T> {
  return send<T>(path, request, signal);
}

async function send<T>(path: string, request: PublicationRequest, signal?: AbortSignal): Promise<T> {
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
    throw new PublicationApiError(await toFailure(response), response.status);
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
    throw new PublicationApiError(await toFailure(response), response.status);
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
