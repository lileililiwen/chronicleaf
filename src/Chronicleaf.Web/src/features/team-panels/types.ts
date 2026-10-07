/**
 * The wire shapes of the team invitation and project-role routes, read from the
 * team-project-permissions contracts rather than guessed.
 *
 * Invitations carry labels and expiry, never the token, except for the one
 * issuance response that discloses the 32-byte bearer token exactly once for
 * the owner to copy manually. Membership rows carry the role and the
 * optimistic revision a role change must name in If-Match.
 */
import type { ApiFailure } from '../background-jobs/types';

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class TeamApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(`${failure.code}: ${failure.message}`);
    this.name = 'TeamApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }
}

/** One pending invitation as served: ids, labels and expiry, never the token. */
export interface TeamInvitation {
  readonly id: string;
  readonly email: string;
  readonly role: string;
  readonly expiresAt: string;
}

/** A newly issued invitation. The token is disclosed exactly once, in this response. */
export interface CreatedTeamInvitation extends TeamInvitation {
  readonly token: string;
}

/** The pending invitations of one tenant. */
export interface TeamInvitationList {
  readonly invitations: readonly TeamInvitation[];
}

/** One accepted invitation: the tenant joined and the role granted. */
export interface AcceptedTeamInvitation {
  readonly tenantId: string;
  readonly role: string;
}

/** One live membership as served: who, which role, and at which revision. */
export interface TeamMember {
  readonly userId: string;
  readonly role: string;
  readonly revision: number;
  readonly createdAt: string;
}

/** The live memberships of one tenant. */
export interface TeamMemberList {
  readonly members: readonly TeamMember[];
}

/** One stored project grant as served: who may do what on which project. */
export interface ProjectGrant {
  readonly projectId: string;
  readonly userId: string;
  readonly permission: string;
  readonly revision: number;
}

/** The caller's effective project permissions, evaluated server-side. */
export interface ProjectAccess {
  readonly canRead: boolean;
  readonly canEdit: boolean;
  readonly canApprove: boolean;
  readonly canPublish: boolean;
  readonly permissions: readonly string[];
}
