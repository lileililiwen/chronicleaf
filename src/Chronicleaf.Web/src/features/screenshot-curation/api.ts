import type { ApiFailure } from '../background-jobs/types';
import {
  CurationApiError,
  type CuratedScreenshotPage,
  type CuratedScreenshotResult,
  type CurationEdit,
  type ScreenshotOrderResult,
} from './types';

/**
 * The only client of the curation routes in this repository.
 *
 * It follows the same rules as the delivered capture, evidence, credential and operation clients:
 * requests are `same-origin`, so the browser attaches the session cookie and this code never sees
 * it, and every mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * <b>What this module may hold.</b> The owner's choice about stored pictures - a status, a caption,
 * a primary flag, an explicit sequence - and the two revisions that choice was read against. It
 * never receives bytes: the pictures are served by the delivered media route, whose base the server
 * publishes in the list, so this client has no way to hand out an image itself and no path where
 * one could be invented.
 *
 * <b>Every mutation is conditional twice.</b> If-Match carries the screenshot revision and the body
 * carries the evidence revision, and the server refuses a change against either one that moved. A
 * 409 is therefore not a failure of the choice: it is the server saying the choice was made against
 * something that is no longer current, and the panel reads again rather than resending.
 */

/** Reads the pictures a milestone has with their curation state, newest first. */
export async function listCurated(
  milestoneId: string,
  signal?: AbortSignal,
): Promise<CuratedScreenshotPage> {
  return send<CuratedScreenshotPage>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/screenshots`,
    { method: 'GET', signal },
  );
}

/**
 * Applies one change to one picture and returns the picture as stored.
 *
 * A change that moves the selection publishes it into the milestone's evidence in the same server
 * transaction; a primary flip or a caption on an unselected picture moves no evidence. The panel
 * learns which happened from `bundleRevision` rather than guessing.
 */
export async function curateScreenshot(
  screenshotId: string,
  edit: CurationEdit,
  screenshotRevision: number,
  bundleRevision: number,
  signal?: AbortSignal,
): Promise<CuratedScreenshotResult> {
  return send<CuratedScreenshotResult>(
    `/api/screenshots/${encodeURIComponent(screenshotId)}`,
    {
      method: 'PATCH',
      body: {
        status: edit.status ?? null,
        caption: edit.caption ?? null,
        isPrimary: edit.isPrimary ?? null,
        expectedRevision: screenshotRevision,
        expectedBundleRevision: bundleRevision,
      },
      revision: screenshotRevision,
    },
    signal,
  );
}

/**
 * Stores the owner's explicit sequence of the selected pictures.
 *
 * The ids must name exactly the currently selected pictures, once each: the server refuses an
 * order that drops one, adds an unselected one or names one twice, because each of those would
 * silently select or unselect something.
 */
export async function orderScreenshots(
  milestoneId: string,
  ids: readonly string[],
  bundleRevision: number,
  signal?: AbortSignal,
): Promise<ScreenshotOrderResult> {
  return send<ScreenshotOrderResult>(
    `/api/milestones/${encodeURIComponent(milestoneId)}/screenshots/order`,
    {
      method: 'POST',
      body: { ids: [...ids], expectedBundleRevision: bundleRevision },
    },
    signal,
  );
}

interface CurationRequest {
  readonly method: 'GET' | 'POST' | 'PATCH';
  readonly body?: unknown;
  /** The screenshot revision this request extends, sent as If-Match. */
  readonly revision?: number;
  readonly signal?: AbortSignal;
}

async function send<T>(path: string, request: CurationRequest, signal?: AbortSignal): Promise<T> {
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

  if (request.revision !== undefined) {
    headers['If-Match'] = String(request.revision);
  }

  const response = await fetch(path, {
    method: request.method,
    credentials: 'same-origin',
    headers,
    body: request.body === undefined ? undefined : JSON.stringify(request.body),
    signal: signal ?? request.signal,
  });

  if (!response.ok) {
    throw new CurationApiError(await toFailure(response), response.status);
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
    throw new CurationApiError(await toFailure(response), response.status);
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
