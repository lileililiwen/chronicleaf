import type { ApiFailure } from '../background-jobs/types';
import {
  TeamApiError,
  type AcceptedTeamInvitation,
  type CreatedTeamInvitation,
  type ProjectAccess,
  type ProjectGrant,
  type TeamInvitationList,
  type TeamMember,
  type TeamMemberList,
} from './types';

/**
 * The only client of the team invitation and project-role routes in this repository.
 *
 * It follows the same rules as the delivered capture, evidence, credential and operation
 * clients: requests are `same-origin`, so the browser attaches the session cookie and this
 * code never sees it, and every mutation carries a fresh antiforgery value that is used
 * once and dropped.
 *
 * <b>What this module never holds.</b> The invitation bearer token is disclosed exactly once
 * in the issuance response for the owner to copy manually: this client shows it and keeps
 * no copy beyond the rendered answer. A role change takes its revision from If-Match, and a
 * grant that is already held is the server's own no-op.
 */

/** Invites one person into a tenant the caller owns. The token arrives exactly once. */
export async function inviteMember(
  tenantId: string,
  email: string,
  role: string,
  signal?: AbortSignal,
): Promise<CreatedTeamInvitation> {
  return write<CreatedTeamInvitation>(
    `/api/hosted/tenants/${encodeURIComponent(tenantId)}/invitations`,
    { method: 'POST', body: { email, role } },
    signal,
  );
}

/** Lists the pending invitations of a tenant the caller owns. */
export async function listInvitations(
  tenantId: string,
  signal?: AbortSignal,
): Promise<TeamInvitationList> {
  return read<TeamInvitationList>(
    `/api/hosted/tenants/${encodeURIComponent(tenantId)}/invitations`,
    { method: 'GET', signal },
  );
}

/** Accepts a live invitation with its bearer token. */
export async function acceptInvitation(token: string, signal?: AbortSignal): Promise<AcceptedTeamInvitation> {
  return write<AcceptedTeamInvitation>(
    '/api/hosted/invitations/accept',
    { method: 'POST', body: { token } },
    signal,
  );
}

/** Revokes a pending invitation in a tenant the caller owns. The row is kept. */
export async function revokeInvitation(
  tenantId: string,
  invitationId: string,
  signal?: AbortSignal,
): Promise<void> {
  await writeVoid(
    `/api/hosted/tenants/${encodeURIComponent(tenantId)}/invitations/${encodeURIComponent(invitationId)}`,
    { method: 'DELETE' },
    signal,
  );
}

/** Lists the live memberships of a tenant the caller owns, oldest first. */
export async function listMembers(tenantId: string, signal?: AbortSignal): Promise<TeamMemberList> {
  return read<TeamMemberList>(
    `/api/hosted/tenants/${encodeURIComponent(tenantId)}/team-members`,
    { method: 'GET', signal },
  );
}

/** Changes one member's tenant role at the revision If-Match names. */
export async function updateMember(
  tenantId: string,
  userId: string,
  role: string,
  revision: number,
  signal?: AbortSignal,
): Promise<TeamMember> {
  return write<TeamMember>(
    `/api/hosted/tenants/${encodeURIComponent(tenantId)}/team-members/${encodeURIComponent(userId)}`,
    { method: 'PATCH', body: { role }, revision },
    signal,
  );
}

/** Revokes one member's tenant membership and the sessions that acted under it. */
export async function removeMember(
  tenantId: string,
  userId: string,
  signal?: AbortSignal,
): Promise<void> {
  await writeVoid(
    `/api/hosted/tenants/${encodeURIComponent(tenantId)}/team-members/${encodeURIComponent(userId)}`,
    { method: 'DELETE' },
    signal,
  );
}

/** Grants one project permission to a live member of the project's tenant. */
export async function grantProjectPermission(
  projectId: string,
  userId: string,
  permission: string,
  signal?: AbortSignal,
): Promise<ProjectGrant> {
  return write<ProjectGrant>(
    `/api/projects/${encodeURIComponent(projectId)}/members`,
    { method: 'POST', body: { userId, permission } },
    signal,
  );
}

/** Reads the caller's effective project permissions, evaluated server-side. */
export async function getProjectAccess(
  projectId: string,
  signal?: AbortSignal,
): Promise<ProjectAccess> {
  return read<ProjectAccess>(
    `/api/projects/${encodeURIComponent(projectId)}/team-access`,
    { method: 'GET', signal },
  );
}

interface TeamRequest {
  readonly method: 'GET' | 'POST' | 'PATCH' | 'DELETE';
  readonly body?: unknown;
  /** The membership revision this request extends, sent as If-Match. */
  readonly revision?: number;
  readonly signal?: AbortSignal;
}

async function read<T>(path: string, request: TeamRequest): Promise<T> {
  return send<T>(path, request, undefined);
}

async function write<T>(path: string, request: TeamRequest, signal?: AbortSignal): Promise<T> {
  return send<T>(path, request, signal);
}

async function writeVoid(path: string, request: TeamRequest, signal?: AbortSignal): Promise<void> {
  await send<unknown>(path, request, signal);
}

async function send<T>(path: string, request: TeamRequest, signal?: AbortSignal): Promise<T> {
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
    throw new TeamApiError(await toFailure(response), response.status);
  }

  if (response.status === 204) {
    return undefined as T;
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
    throw new TeamApiError(await toFailure(response), response.status);
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
