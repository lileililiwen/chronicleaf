import { useState } from 'react';

import {
  acceptInvitation,
  getProjectAccess,
  grantProjectPermission,
  inviteMember,
  listInvitations,
  listMembers,
  removeMember,
  revokeInvitation,
  updateMember,
} from './api';
import { TeamApiError, type AcceptedTeamInvitation, type CreatedTeamInvitation, type ProjectAccess, type TeamInvitation, type TeamMember } from './types';

/**
 * The team invitation and project-role triggers (FEATURE-TRIGGER-PANELS-001).
 *
 * Four states are visible here and each is shown as what it is. A tenant with no
 * invitations or members beyond the owner says so rather than drawing rows that do
 * not exist. A refusal shows the server's own code and message verbatim, never a
 * paraphrase, and no row is invented before the server answers. The invitation
 * token is disclosed exactly once in the issuance answer for the owner to copy
 * manually: this panel shows it and stores no copy beyond the rendered answer.
 *
 * Nothing here fires on render: every route call below is behind an explicit
 * owner act, and the tenant id is typed by the owner because tenancy is the
 * server's own answer rather than something this client selects from.
 */
export function TeamPanel() {
  const [tenantId, setTenantId] = useState('');
  const [email, setEmail] = useState('');
  const [role, setRole] = useState('reader');
  const [invitations, setInvitations] = useState<readonly TeamInvitation[]>([]);
  const [members, setMembers] = useState<readonly TeamMember[]>([]);
  const [issued, setIssued] = useState<CreatedTeamInvitation | null>(null);
  const [acceptToken, setAcceptToken] = useState('');
  const [accepted, setAccepted] = useState<AcceptedTeamInvitation | null>(null);
  const [grantProjectId, setGrantProjectId] = useState('');
  const [grantUserId, setGrantUserId] = useState('');
  const [grantPermission, setGrantPermission] = useState('reader');
  const [access, setAccess] = useState<ProjectAccess | null>(null);
  const [loading, setLoading] = useState(false);
  const [failure, setFailure] = useState<TeamApiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const tenant = tenantId.trim();

  const run = async (work: () => Promise<string | null>) => {
    setLoading(true);
    setFailure(null);
    setNotice(null);

    try {
      const message = await work();
      setNotice(message);
    } catch (thrown) {
      setFailure(thrown instanceof TeamApiError ? thrown : null);
    } finally {
      setLoading(false);
    }
  };

  const load = async (id: string) => {
    // The list is re-read rather than patched locally: the rows now stored are the ones the
    // owner sees, including any revision a change advanced.
    const [listedInvitations, listedMembers] = await Promise.all([
      listInvitations(id),
      listMembers(id),
    ]);
    setInvitations(listedInvitations.invitations);
    setMembers(listedMembers.members);
  };

  const refresh = () =>
    run(async () => {
      if (tenant.length === 0) {
        throw new TeamApiError(
          { code: 'tenant_required', message: 'A tenant id names whose team this is.', traceId: '' },
          400,
        );
      }

      await load(tenant);

      return null;
    });

  const invite = () =>
    run(async () => {
      const created = await inviteMember(tenant, email.trim(), role);
      setIssued(created);
      await load(tenant);
      return `An invitation for ${created.email} is pending. Its token is shown once, below.`;
    });

  const accept = () =>
    run(async () => {
      const result = await acceptInvitation(acceptToken.trim());
      setAccepted(result);
      return `Joined tenant ${result.tenantId} as ${result.role}.`;
    });

  const revoke = (invitationId: string) =>
    run(async () => {
      await revokeInvitation(tenant, invitationId);
      await load(tenant);
      return 'The invitation is revoked. Its row is kept as history.';
    });

  const reRole = (userId: string, currentRevision: number, nextRole: string) =>
    run(async () => {
      const saved = await updateMember(tenant, userId, nextRole, currentRevision);
      await load(tenant);
      return `${saved.userId} is now ${saved.role} at revision ${saved.revision}.`;
    });

  const remove = (userId: string) =>
    run(async () => {
      if (!window.confirm('Revoke this membership and its sessions? The grant rows stay as history.')) {
        return null;
      }

      await removeMember(tenant, userId);
      await load(tenant);
      return 'The membership is revoked. Its sessions no longer act.';
    });

  const grant = () =>
    run(async () => {
      const saved = await grantProjectPermission(
        grantProjectId.trim(),
        grantUserId.trim(),
        grantPermission,
      );
      return `${saved.userId} may ${saved.permission} on ${saved.projectId}.`;
    });

  const readAccess = () =>
    run(async () => {
      setAccess(await getProjectAccess(grantProjectId.trim()));
      return null;
    });

  return (
    <section className="view" aria-label="Team invitations and members" data-testid="team-panel">
      <h3 className="view__heading" data-testid="team-heading">
        Team invitations and members
      </h3>
      <p className="view__hint" data-testid="team-hint">
        Invitations and memberships belong to one tenant, named by its id. Every change below is
        an explicit act, and every answer is the server&apos;s own.
      </p>

      <label className="form__label">
        Tenant id
        <input
          type="text"
          className="form__input"
          value={tenantId}
          onChange={(event) => setTenantId(event.target.value)}
          data-testid="team-tenant-input"
        />
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action"
          onClick={() => void refresh()}
          disabled={loading}
          data-testid="team-refresh"
        >
          {loading ? 'Reading…' : 'List invitations and members'}
        </button>
      </div>

      {notice ? (
        <p className="notice notice--ok" role="status" data-testid="team-notice">
          {notice}
        </p>
      ) : null}

      {failure ? (
        <p className="notice notice--error" role="alert" data-testid="team-error">
          {failure.message}
        </p>
      ) : null}

      {issued ? (
        <p className="notice" role="status" data-testid="team-issued">
          The token for {issued.email} is <code data-testid="team-issued-token">{issued.token}</code>.
          It is shown once: copy it now, because no route serves it again.
        </p>
      ) : null}

      <h4 className="view__heading">Invite</h4>
      <label className="form__label">
        Email label
        <input
          type="text"
          className="form__input"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          data-testid="team-invite-email"
        />
      </label>
      <label className="form__label">
        Tenant role
        <select
          className="form__input"
          value={role}
          onChange={(event) => setRole(event.target.value)}
          data-testid="team-invite-role"
        >
          <option value="owner">owner</option>
          <option value="member">member</option>
        </select>
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action action--primary"
          onClick={() => void invite()}
          disabled={loading}
          data-testid="team-invite"
        >
          Invite
        </button>
      </div>

      <h4 className="view__heading">Pending invitations</h4>
      {invitations.length === 0 ? (
        <p className="view__state" data-testid="team-invitations-empty">
          No pending invitations. Nothing has been invited that has not yet joined.
        </p>
      ) : (
        <ul className="milestones" data-testid="team-invitations">
          {invitations.map((invitation) => (
            <li key={invitation.id} className="milestone" data-testid="team-invitation">
              <span data-testid="team-invitation-email">{invitation.email}</span>{' '}
              <span data-testid="team-invitation-role">{invitation.role}</span>
              <button
                type="button"
                className="action action--danger"
                onClick={() => void revoke(invitation.id)}
                disabled={loading}
                data-testid="team-invitation-revoke"
              >
                Revoke
              </button>
            </li>
          ))}
        </ul>
      )}

      <h4 className="view__heading">Members</h4>
      {members.length === 0 ? (
        <p className="view__state" data-testid="team-members-empty">
          No members beyond the owner. Nobody has joined this tenant yet.
        </p>
      ) : (
        <ul className="milestones" data-testid="team-members">
          {members.map((member) => (
            <li key={member.userId} className="milestone" data-testid="team-member">
              <span data-testid="team-member-id">{member.userId}</span>{' '}
              <span data-testid="team-member-role">{member.role}</span>{' '}
              <span data-testid="team-member-revision">revision {member.revision}</span>
              <button
                type="button"
                className="action"
                onClick={() =>
                  void reRole(
                    member.userId,
                    member.revision,
                    member.role === 'owner' ? 'member' : 'owner',
                  )
                }
                disabled={loading}
                data-testid="team-member-rerole"
              >
                Make {member.role === 'owner' ? 'member' : 'owner'}
              </button>
              <button
                type="button"
                className="action action--danger"
                onClick={() => void remove(member.userId)}
                disabled={loading}
                data-testid="team-member-remove"
              >
                Remove
              </button>
            </li>
          ))}
        </ul>
      )}

      <h4 className="view__heading">Accept an invitation</h4>
      <label className="form__label">
        Bearer token
        <input
          type="text"
          className="form__input"
          value={acceptToken}
          onChange={(event) => setAcceptToken(event.target.value)}
          data-testid="team-accept-token"
        />
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action action--primary"
          onClick={() => void accept()}
          disabled={loading}
          data-testid="team-accept"
        >
          Accept
        </button>
      </div>
      {accepted ? (
        <p className="notice notice--ok" role="status" data-testid="team-accepted">
          Joined {accepted.tenantId} as {accepted.role}.
        </p>
      ) : null}

      <h4 className="view__heading">Project grants</h4>
      <label className="form__label">
        Project id
        <input
          type="text"
          className="form__input"
          value={grantProjectId}
          onChange={(event) => setGrantProjectId(event.target.value)}
          data-testid="team-grant-project"
        />
      </label>
      <label className="form__label">
        Member id
        <input
          type="text"
          className="form__input"
          value={grantUserId}
          onChange={(event) => setGrantUserId(event.target.value)}
          data-testid="team-grant-user"
        />
      </label>
      <label className="form__label">
        Permission
        <select
          className="form__input"
          value={grantPermission}
          onChange={(event) => setGrantPermission(event.target.value)}
          data-testid="team-grant-permission"
        >
          <option value="reader">reader</option>
          <option value="editor">editor</option>
          <option value="approver">approver</option>
          <option value="publisher">publisher</option>
        </select>
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action action--primary"
          onClick={() => void grant()}
          disabled={loading}
          data-testid="team-grant"
        >
          Grant
        </button>
        <button
          type="button"
          className="action"
          onClick={() => void readAccess()}
          disabled={loading}
          data-testid="team-access"
        >
          Read my access
        </button>
      </div>
      {access ? (
        <p className="view__state" data-testid="team-access-result">
          Read {String(access.canRead)}, edit {String(access.canEdit)}, approve{' '}
          {String(access.canApprove)}, publish {String(access.canPublish)}: {access.permissions.join(', ')}
        </p>
      ) : null}
    </section>
  );
}
