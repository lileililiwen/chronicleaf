import type { AntiforgeryMetadata, OwnerSession, SessionError } from './types';

/**
 * The only client of the access boundary in this repository.
 *
 * Every request is `same-origin`, so the session cookie is attached by the browser and never by
 * this code: there is no token in JavaScript, no token in storage, and nothing here that a
 * cross-site page could read. A mutation additionally carries the antiforgery value the owner-only
 * metadata response issued for this session, which is what stops a same-origin cookie alone from
 * being enough to change anything.
 */

const sessionPath = '/api/session';
const antiforgeryPath = '/api/session/antiforgery';

/** A refusal the UI can show, or `null` when the request succeeded. */
export class SessionApiError extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = 'SessionApiError';
    this.code = code;
  }
}

async function toFailure(response: Response): Promise<SessionApiError> {
  let code = 'internal_error';
  let message = 'The request could not be completed.';

  try {
    const body = (await response.json()) as Partial<SessionError>;
    if (typeof body.code === 'string') {
      code = body.code;
    }
    if (typeof body.message === 'string') {
      message = body.message;
    }
  } catch {
    // A body that is not the envelope is not worth a second error path: the status still decided
    // the outcome, and the fallback message says nothing about the server.
  }

  return new SessionApiError(code, message);
}

/**
 * The current owner session, or `null` when there is none. A 401 is the normal answer for a
 * browser that has never signed in, so it is not an error here.
 */
export async function readSession(signal?: AbortSignal): Promise<OwnerSession | null> {
  const response = await fetch(sessionPath, { method: 'GET', credentials: 'same-origin', signal });

  if (response.status === 401) {
    return null;
  }

  if (!response.ok) {
    throw await toFailure(response);
  }

  return (await response.json()) as OwnerSession;
}

/**
 * Exchanges the owner's setup token for a session. The token lives in the request body for this
 * one call and is never stored, echoed or logged by this application.
 */
export async function signIn(accessToken: string, signal?: AbortSignal): Promise<void> {
  const response = await fetch(sessionPath, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ accessToken }),
    signal,
  });

  if (!response.ok) {
    throw await toFailure(response);
  }
}

/** Requests a fresh antiforgery value for the current session. */
export async function readAntiforgery(signal?: AbortSignal): Promise<AntiforgeryMetadata> {
  const response = await fetch(antiforgeryPath, { method: 'GET', credentials: 'same-origin', signal });

  if (!response.ok) {
    throw await toFailure(response);
  }

  return (await response.json()) as AntiforgeryMetadata;
}

/**
 * Revokes the session. It is a mutation, so it carries the antiforgery value the server issued for
 * this session, and the browser drops the cookie from the response.
 */
export async function signOut(antiforgery: AntiforgeryMetadata, signal?: AbortSignal): Promise<void> {
  const response = await fetch(sessionPath, {
    method: 'DELETE',
    credentials: 'same-origin',
    headers: { [antiforgery.headerName]: antiforgery.token },
    signal,
  });

  if (!response.ok) {
    throw await toFailure(response);
  }
}
