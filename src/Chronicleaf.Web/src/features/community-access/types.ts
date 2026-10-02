/**
 * The wire shapes of the single-user access boundary (COMMUNITY-ACCESS-001/002).
 *
 * These mirror the pinned API contracts. Nothing here is a credential: the session lives in an
 * HttpOnly cookie the browser owns, and the antiforgery value is a per-session token the owner
 * requests on demand. The owner identity is a UUID string, never a name the caller supplies.
 */

/** `GET /api/session` for a signed-in owner. */
export interface OwnerSession {
  readonly authenticated: true;
  readonly actorId: string;
}

/** The owner-only antiforgery metadata response. */
export interface AntiforgeryMetadata {
  /** The header a mutation must carry the value in. */
  readonly headerName: string;
  /** A value scoped to the session that requested it. */
  readonly token: string;
}

/**
 * The failure envelope every refused request uses. `code` is the only field the UI branches on;
 * the message is authored by the server and never describes a token, a session or a cause.
 */
export interface SessionError {
  readonly code: string;
  readonly message: string;
  readonly traceId: string;
}

/** The code a login failure always carries, whatever was actually wrong. */
export const INVALID_CREDENTIALS = 'invalid_credentials';

/** The code a client is throttled with, after too many failed logins in the window. */
export const TOO_MANY_REQUESTS = 'too_many_requests';

/** The code an unowned request to a private route is refused with. */
export const UNAUTHENTICATED = 'unauthenticated';

/** How long a local session may sit idle before the server stops accepting it: 12 hours. */
export const IDLE_TIMEOUT_HOURS = 12;

/** How long a session may exist in total, whatever the owner does with it: 7 days. */
export const ABSOLUTE_LIFETIME_DAYS = 7;
