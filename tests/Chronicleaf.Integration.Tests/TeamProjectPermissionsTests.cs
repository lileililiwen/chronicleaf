using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// TEAM-PROJECT-PERMISSIONS-001 and TEAM-PROJECT-PERMISSIONS-002 against a real disposable
/// PostgreSQL started from a pinned Docker image, through the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// Nothing here is simulated. Invitations, token hashes, memberships, project grants, revisions,
/// sessions and audit rows are all produced by a real PostgreSQL server, and every claim is read
/// back over the wire - a status code, a header, a body - or over a raw connection as a row or a
/// column. The one declared substitution is the OIDC issuer itself, inherited from the tenant
/// boundary package: a handler answers the discovery document, the key set and the token
/// endpoint from scripted bytes, while the state lookup, the checks and the signature
/// verification are the production code under test. An unavailable container is a failure, never
/// a passing result.
/// <para>
/// Hosted identity is an issuer/subject pair with no email claim, so the invitation email is the
/// owner's label for who the token is for - never an authenticator. The 32-byte bearer token is
/// the grant: it is stored as a hash, disclosed exactly once, single-use and bound to 7 days.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class TeamProjectPermissionsTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The installation every scenario provisions, and therefore the Community scope.</summary>
    private static readonly Guid InstallationId = new("c7d2a41f-9e03-4b5a-8d26-1f3e5a709211");

    // ===========================================================================================
    // TEAM-PROJECT-PERMISSIONS-001 Success: an editor edits an assigned project, but approval is
    // refused until the approver grant is separately held.
    // ===========================================================================================

    [Fact]
    public async Task Editor_edits_assigned_project_but_approval_is_refused_until_granted()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_editor");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var owner = await TeamClient.LoginAsync(host, oidc, "owner-1");
        var tenantId = await owner.CreateTenantAsync("Acme");
        var projectId = await owner.CreateProjectAsync("Ledger");

        using var editor = await TeamClient.LoginAsync(host, oidc, "editor-1");
        var token = await owner.InviteAsync(tenantId, "editor@example.com", "editor");
        await editor.AcceptAsync(token);
        await owner.GrantAsync(projectId, await UserIdAsync(database, "editor-1"), "editor");

        // The edit is saved: the compare-and-set moves the revision.
        var storedRevision = await ProjectRevisionAsync(editor, projectId);
        using var edited = await editor.SendAsync(await editor.Patch(
            $"/api/projects/{projectId}",
            new { name = "Ledger v2" },
            storedRevision));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        // Approval is refused while only the editor grant is held.
        using var approveDenied = await editor.SendAsync(await editor.Post(
            $"/api/projects/{projectId}/team-check",
            new { action = "approve", approved = false }));
        Assert.Equal(HttpStatusCode.Forbidden, approveDenied.StatusCode);
        Assert.Equal("team_access_denied", await ReadCodeAsync(approveDenied));

        // The approver grant is a separate, explicit act - and then approval is authorized.
        await owner.GrantAsync(projectId, await UserIdAsync(database, "editor-1"), "approver");
        using var approveAllowed = await editor.SendAsync(await editor.Post(
            $"/api/projects/{projectId}/team-check",
            new { action = "approve", approved = false }));
        Assert.Equal(HttpStatusCode.OK, approveAllowed.StatusCode);
    }

    // ===========================================================================================
    // TEAM-PROJECT-PERMISSIONS-001 Failure: a publisher cannot publish an unapproved draft.
    // ===========================================================================================

    [Fact]
    public async Task Publisher_cannot_publish_an_unapproved_head()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_publisher");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var owner = await TeamClient.LoginAsync(host, oidc, "owner-1");
        var tenantId = await owner.CreateTenantAsync("Acme");
        var projectId = await owner.CreateProjectAsync("Ledger");

        using var publisher = await TeamClient.LoginAsync(host, oidc, "publisher-1");
        var token = await owner.InviteAsync(tenantId, "publisher@example.com", "viewer");
        await publisher.AcceptAsync(token);
        var publisherId = await UserIdAsync(database, "publisher-1");
        await owner.GrantAsync(projectId, publisherId, "publisher");

        // The grant authorizes the act, but the unapproved head is a conflict with the record's
        // own state - never a publish.
        using var unapproved = await publisher.SendAsync(await publisher.Post(
            $"/api/projects/{projectId}/team-check",
            new { action = "publish", approved = false }));
        Assert.Equal(HttpStatusCode.Conflict, unapproved.StatusCode);
        Assert.Equal("approval_required", await ReadCodeAsync(unapproved));

        // The same grant on an approved head is authorized.
        using var approved = await publisher.SendAsync(await publisher.Post(
            $"/api/projects/{projectId}/team-check",
            new { action = "publish", approved = true }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        // And a publisher holds no edit grant on the side.
        using var editDenied = await publisher.SendAsync(await publisher.Post(
            $"/api/projects/{projectId}/team-check",
            new { action = "edit", approved = false }));
        Assert.Equal(HttpStatusCode.Forbidden, editDenied.StatusCode);
    }

    // ===========================================================================================
    // TEAM-PROJECT-PERMISSIONS-001 Boundary: a reader of one project sees nothing of another.
    // ===========================================================================================

    [Fact]
    public async Task Reader_of_one_project_sees_nothing_of_another()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_reader");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var owner = await TeamClient.LoginAsync(host, oidc, "owner-1");
        var tenantId = await owner.CreateTenantAsync("Acme");
        var assignedId = await owner.CreateProjectAsync("Assigned");
        var otherId = await owner.CreateProjectAsync("Other");

        using var reader = await TeamClient.LoginAsync(host, oidc, "reader-1");
        var token = await owner.InviteAsync(tenantId, "reader@example.com", "viewer");
        await reader.AcceptAsync(token);
        await owner.GrantAsync(assignedId, await UserIdAsync(database, "reader-1"), "reader");

        // The assigned project reads, through the real domain read path.
        using var assigned = await reader.GetAsync($"/api/projects/{assignedId}");
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        // The unassigned project is denied before any row is served - and the denial is at the
        // service layer, so the domain read and the probe agree.
        using var other = await reader.GetAsync($"/api/projects/{otherId}");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal("team_access_denied", await ReadCodeAsync(other));

        using var probe = await reader.SendAsync(await reader.Post(
            $"/api/projects/{otherId}/team-check",
            new { action = "read", approved = false }));
        Assert.Equal(HttpStatusCode.Forbidden, probe.StatusCode);

        // A reader reads but does not edit, even on the assigned project.
        var storedRevision = await ProjectRevisionAsync(reader, assignedId);
        using var edit = await reader.SendAsync(await reader.Patch(
            $"/api/projects/{assignedId}",
            new { name = "Renamed by reader" },
            storedRevision));
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        Assert.Equal("team_access_denied", await ReadCodeAsync(edit));
    }

    // ===========================================================================================
    // TEAM-PROJECT-PERMISSIONS-002 Success: the intended verified user accepts once, and exactly
    // one membership is created.
    // ===========================================================================================

    [Fact]
    public async Task Intended_user_accepts_once_and_exactly_one_membership_is_created()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_accept");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var owner = await TeamClient.LoginAsync(host, oidc, "owner-1");
        var tenantId = await owner.CreateTenantAsync("Acme");

        var token = await owner.InviteAsync(tenantId, "teammate@example.com", "editor");
        Assert.Equal(64, token.Length);

        // The invitation is pending exactly once, and the token exists nowhere in storage.
        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT count(*)::text FROM tenant_invitations WHERE tenant_id = '{tenantId}'"
                + " AND accepted_at IS NULL AND revoked_at IS NULL"));
        Assert.Equal(
            "0",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT count(*)::text FROM tenant_invitations WHERE token_hash = '{token}'"));

        using var member = await TeamClient.LoginAsync(host, oidc, "member-1");
        using var accepted = await member.SendAsync(await member.Post(
            "/api/hosted/invitations/accept",
            new { token }));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using (var document = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()))
        {
            Assert.Equal(tenantId, document.RootElement.GetProperty("tenantId").GetString());
            Assert.Equal("editor", document.RootElement.GetProperty("role").GetString());
        }

        // Exactly one membership exists, the invitation left the pending list, and accepting
        // selected the tenant onto the session - the new member resolves a domain scope.
        Assert.Equal(
            "2",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT count(*)::text FROM tenant_memberships WHERE tenant_id = '{tenantId}' AND revoked_at IS NULL"));
        Assert.Equal(
            "0",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT count(*)::text FROM tenant_invitations WHERE tenant_id = '{tenantId}'"
                + " AND accepted_at IS NULL AND revoked_at IS NULL"));
        var storedHash = await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT token_hash FROM tenant_invitations WHERE tenant_id = '{tenantId}'");
        Assert.NotNull(storedHash);
        Assert.Equal(64, storedHash!.Length);
        Assert.NotEqual(token, storedHash);

        using var tenants = await member.GetAsync("/api/hosted/tenants");
        Assert.Equal(HttpStatusCode.OK, tenants.StatusCode);
    }

    // ===========================================================================================
    // TEAM-PROJECT-PERMISSIONS-002 Failure: an expired or replayed invitation creates nothing.
    // ===========================================================================================

    [Fact]
    public async Task Expired_invitation_creates_no_membership()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_expired");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var owner = await TeamClient.LoginAsync(host, oidc, "owner-1");
        var tenantId = await owner.CreateTenantAsync("Acme");

        var token = await owner.InviteAsync(tenantId, "late@example.com", "viewer");

        // The 7-day life ran out before the token was presented.
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            $"UPDATE tenant_invitations SET expires_at = '{Clock.AddHours(-1):O}' WHERE tenant_id = '{tenantId}'");

        using var member = await TeamClient.LoginAsync(host, oidc, "member-1");
        using var refused = await member.SendAsync(await member.Post(
            "/api/hosted/invitations/accept",
            new { token }));
        Assert.Equal(HttpStatusCode.Gone, refused.StatusCode);
        Assert.Equal("invitation_expired", await ReadCodeAsync(refused));

        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT count(*)::text FROM tenant_memberships WHERE tenant_id = '{tenantId}' AND revoked_at IS NULL"));
    }

    [Fact]
    public async Task Replayed_invitation_creates_no_second_membership()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_replay");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var owner = await TeamClient.LoginAsync(host, oidc, "owner-1");
        var tenantId = await owner.CreateTenantAsync("Acme");

        var token = await owner.InviteAsync(tenantId, "teammate@example.com", "viewer");

        using var first = await TeamClient.LoginAsync(host, oidc, "member-1");
        using var accepted = await first.SendAsync(await first.Post(
            "/api/hosted/invitations/accept",
            new { token }));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // The token is single-use: the replay is refused and the membership count does not move.
        using var replay = await first.SendAsync(await first.Post(
            "/api/hosted/invitations/accept",
            new { token }));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("invitation_reused", await ReadCodeAsync(replay));

        using var stranger = await TeamClient.LoginAsync(host, oidc, "stranger-1");
        using var stolen = await stranger.SendAsync(await stranger.Post(
            "/api/hosted/invitations/accept",
            new { token }));
        Assert.Equal(HttpStatusCode.Conflict, stolen.StatusCode);
        Assert.Equal(
            "2",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT count(*)::text FROM tenant_memberships WHERE tenant_id = '{tenantId}' AND revoked_at IS NULL"));

        // An unknown token names no invitation at all: the same 404 a foreign id gets.
        using var unknown = await stranger.SendAsync(await stranger.Post(
            "/api/hosted/invitations/accept",
            new { token = new string('a', 128) }));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ===========================================================================================
    // TEAM-PROJECT-PERMISSIONS-002 Boundary: the last owner stays, and a revoked user is denied
    // while their history is retained.
    // ===========================================================================================

    [Fact]
    public async Task Last_owner_cannot_be_removed_or_demoted_and_revoked_user_is_denied()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_last_owner");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var owner = await TeamClient.LoginAsync(host, oidc, "owner-1");
        var tenantId = await owner.CreateTenantAsync("Acme");
        var ownerId = await UserIdAsync(database, "owner-1");

        // The sole owner can neither be demoted nor removed: both are the continuity conflict.
        using var demote = await owner.SendAsync(await owner.Patch(
            $"/api/hosted/tenants/{tenantId}/team-members/{ownerId}",
            new { role = "editor" },
            1));
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("team_last_owner", await ReadCodeAsync(demote));

        using var remove = await owner.SendAsync(await owner.Delete(
            $"/api/hosted/tenants/{tenantId}/team-members/{ownerId}"));
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        Assert.Equal("team_last_owner", await ReadCodeAsync(remove));

        // A second owner joins, is demoted, and is then removed: the removal revokes the
        // sessions that acted under the tenant and denies every later operation, while the
        // grant rows and the audit rows stay as history.
        using var second = await TeamClient.LoginAsync(host, oidc, "owner-2");
        await second.AcceptAsync(await owner.InviteAsync(tenantId, "second@example.com", "owner"));
        var secondId = await UserIdAsync(database, "owner-2");
        var projectId = await owner.CreateProjectAsync("Ledger");
        await owner.GrantAsync(projectId, secondId, "editor");

        using var demoted = await owner.SendAsync(await owner.Patch(
            $"/api/hosted/tenants/{tenantId}/team-members/{secondId}",
            new { role = "editor" },
            1));
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        using (var document = JsonDocument.Parse(await demoted.Content.ReadAsStringAsync()))
        {
            Assert.Equal(2, document.RootElement.GetProperty("revision").GetInt64());
        }

        using var removed = await owner.SendAsync(await owner.Delete(
            $"/api/hosted/tenants/{tenantId}/team-members/{secondId}"));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        using var denied = await second.GetAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("unauthenticated", await ReadCodeAsync(denied));

        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT count(*)::text FROM project_memberships WHERE project_id = '{projectId}' AND user_id = '{secondId}'"));
        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT count(*)::text FROM audit_events WHERE action = 'team_member_removed'"));

        // The first owner still administers the tenant.
        using var members = await owner.GetAsync($"/api/hosted/tenants/{tenantId}/team-members");
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
        using (var document = JsonDocument.Parse(await members.Content.ReadAsStringAsync()))
        {
            var single = Assert.Single(document.RootElement.GetProperty("members").EnumerateArray());
            Assert.Equal(ownerId, single.GetProperty("userId").GetString());
            Assert.Equal("owner", single.GetProperty("role").GetString());
        }
    }

    // ===========================================================================================
    // Invitation validation: malformed input is 422, duplicates are 409, and only an owner may
    // invite, list or revoke.
    // ===========================================================================================

    [Fact]
    public async Task Invitation_input_is_validated_and_only_owners_may_invite()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_invite_rules");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var owner = await TeamClient.LoginAsync(host, oidc, "owner-1");
        var tenantId = await owner.CreateTenantAsync("Acme");

        using var badEmail = await owner.SendAsync(await owner.Post(
            $"/api/hosted/tenants/{tenantId}/invitations",
            new { email = "not-an-email", role = "viewer" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badEmail.StatusCode);

        using var badRole = await owner.SendAsync(await owner.Post(
            $"/api/hosted/tenants/{tenantId}/invitations",
            new { email = "good@example.com", role = "superadmin" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badRole.StatusCode);

        var token = await owner.InviteAsync(tenantId, "teammate@example.com", "viewer");
        Assert.NotEmpty(token);

        using var duplicate = await owner.SendAsync(await owner.Post(
            $"/api/hosted/tenants/{tenantId}/invitations",
            new { email = "TEAMMATE@example.com", role = "viewer" }));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("invitation_exists", await ReadCodeAsync(duplicate));

        // A foreign tenant id is the same 404 a guessed id gets, and a non-owner is denied.
        using var foreign = await owner.SendAsync(await owner.Post(
            $"/api/hosted/tenants/{Guid.NewGuid():D}/invitations",
            new { email = "other@example.com", role = "viewer" }));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        using var member = await TeamClient.LoginAsync(host, oidc, "member-1");
        await member.AcceptAsync(await owner.InviteAsync(tenantId, "member@example.com", "viewer"));

        using var nonOwnerInvite = await member.SendAsync(await member.Post(
            $"/api/hosted/tenants/{tenantId}/invitations",
            new { email = "another@example.com", role = "viewer" }));
        Assert.Equal(HttpStatusCode.Forbidden, nonOwnerInvite.StatusCode);
        Assert.Equal("tenant_access_denied", await ReadCodeAsync(nonOwnerInvite));

        using var anonymous = host.CreateClient();
        using var unauthenticated = await anonymous.PostAsJsonAsync(
            $"/api/hosted/tenants/{tenantId}/invitations",
            new { email = "anon@example.com", role = "viewer" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    // ===========================================================================================
    // Membership revisions: a change without If-Match is 428, a stale one is 409 with the stored
    // revision, and a revoked invitation stays revoked.
    // ===========================================================================================

    [Fact]
    public async Task Member_role_changes_are_compare_and_set_and_revoked_invitations_stay_dead()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_revision");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var owner = await TeamClient.LoginAsync(host, oidc, "owner-1");
        var tenantId = await owner.CreateTenantAsync("Acme");

        using var member = await TeamClient.LoginAsync(host, oidc, "member-1");
        await member.AcceptAsync(await owner.InviteAsync(tenantId, "member@example.com", "editor"));
        var memberId = await UserIdAsync(database, "member-1");

        using var missing = await owner.SendAsync(await owner.PatchWithoutRevision(
            $"/api/hosted/tenants/{tenantId}/team-members/{memberId}",
            new { role = "viewer" }));
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);

        using var stale = await owner.SendAsync(await owner.Patch(
            $"/api/hosted/tenants/{tenantId}/team-members/{memberId}",
            new { role = "viewer" },
            999));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("revision_conflict", await ReadCodeAsync(stale));

        using var moved = await owner.SendAsync(await owner.Patch(
            $"/api/hosted/tenants/{tenantId}/team-members/{memberId}",
            new { role = "viewer" },
            1));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal("\"2\"", moved.Headers.ETag?.ToString());

        // A pending invitation is revoked by id, and the revoked token authorizes nothing.
        var token = await owner.InviteAsync(tenantId, "late@example.com", "viewer");
        var invitationId = await InvitationIdAsync(database, tenantId, "late@example.com");
        using var revoked = await owner.SendAsync(await owner.Delete(
            $"/api/hosted/tenants/{tenantId}/invitations/{invitationId}"));
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        using var stranger = await TeamClient.LoginAsync(host, oidc, "stranger-1");
        using var dead = await stranger.SendAsync(await stranger.Post(
            "/api/hosted/invitations/accept",
            new { token }));
        Assert.Equal(HttpStatusCode.Conflict, dead.StatusCode);
        Assert.Equal("invitation_reused", await ReadCodeAsync(dead));
    }

    // ===========================================================================================
    // Grant idempotency: a repeated grant writes nothing and audits nothing.
    // ===========================================================================================

    [Fact]
    public async Task Duplicate_project_grant_writes_nothing_and_a_revoked_grant_is_a_no_op()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_grant_idempotent");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var owner = await TeamClient.LoginAsync(host, oidc, "owner-1");
        var tenantId = await owner.CreateTenantAsync("Acme");
        var projectId = await owner.CreateProjectAsync("Ledger");

        using var member = await TeamClient.LoginAsync(host, oidc, "member-1");
        await member.AcceptAsync(await owner.InviteAsync(tenantId, "member@example.com", "viewer"));
        var memberId = await UserIdAsync(database, "member-1");

        await owner.GrantAsync(projectId, memberId, "reader");
        var auditsAfterFirst =
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT count(*)::text FROM audit_events WHERE action = 'team_project_granted'");
        Assert.Equal("1", auditsAfterFirst);

        await owner.GrantAsync(projectId, memberId, "reader");
        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT revision::text FROM project_memberships WHERE project_id = '{projectId}'"
                + $" AND user_id = '{memberId}' AND permission = 'reader'"));
        Assert.Equal(
            auditsAfterFirst,
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT count(*)::text FROM audit_events WHERE action = 'team_project_granted'"));

        // A grant needs a live tenant membership first, and an unknown permission is 422.
        using var stranger = await TeamClient.LoginAsync(host, oidc, "stranger-1");
        using var noMembership = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/members",
            new { userId = await UserIdAsync(database, "stranger-1"), permission = "reader" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noMembership.StatusCode);

        using var badPermission = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/members",
            new { userId = memberId, permission = "superuser" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badPermission.StatusCode);

        // Revoking the grant denies the read; revoking again writes nothing.
        using var revoke = await owner.SendAsync(await owner.Delete(
            $"/api/projects/{projectId}/members/{memberId}/reader"));
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var denied = await member.GetAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var again = await owner.SendAsync(await owner.Delete(
            $"/api/projects/{projectId}/members/{memberId}/reader"));
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT count(*)::text FROM audit_events WHERE action = 'team_project_grant_revoked'"));
    }

    // ===========================================================================================
    // The database itself closes the role and permission vocabularies.
    // ===========================================================================================

    [Fact]
    public async Task Database_refuses_a_role_and_a_permission_outside_the_vocabularies()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_check_constraints");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var owner = await TeamClient.LoginAsync(host, oidc, "owner-1");
        var tenantId = await owner.CreateTenantAsync("Acme");
        var projectId = await owner.CreateProjectAsync("Ledger");
        var ownerId = await UserIdAsync(database, "owner-1");

        var roleRefused = await Assert.ThrowsAsync<PostgresException>(() =>
            ChronicleafPostgresFixture.ExecuteAsync(
                database,
                "INSERT INTO tenant_invitations (id, tenant_id, email, role, token_hash, created_by,"
                + $" created_at, expires_at, accepted_at, revoked_at) VALUES ('{Guid.NewGuid():D}',"
                + $" '{tenantId}', 'rogue@example.com', 'superadmin',"
                + " 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',"
                + $" '{ownerId}', '{Clock:O}', '{Clock.AddDays(7):O}', NULL, NULL)"));
        Assert.Equal("23514", roleRefused.SqlState);
        Assert.Contains("ck_tenant_invitations_role_known", roleRefused.MessageText);

        var permissionRefused = await Assert.ThrowsAsync<PostgresException>(() =>
            ChronicleafPostgresFixture.ExecuteAsync(
                database,
                "INSERT INTO project_memberships (project_id, user_id, permission, revision, created_at)"
                + $" VALUES ('{projectId}', '{ownerId}', 'superuser', 1, '{Clock:O}')"));
        Assert.Equal("23514", permissionRefused.SqlState);
        Assert.Contains("ck_project_memberships_permission_known", permissionRefused.MessageText);
    }

    // ===========================================================================================
    // Community behavior is unchanged: the owner flow works exactly as before, and every team
    // route refuses exactly like the tenancy boundary.
    // ===========================================================================================

    [Fact]
    public async Task Community_owner_flow_is_unchanged_and_team_routes_refuse()
    {
        Assert.True(postgres.IsRealPostgres, "the team scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("team_community");
        await using var host = ChronicleafApiHost.StartWithAccess(
            database,
            Clock,
            OwnerClient.AccessTokenHash,
            OwnerClient.PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        using var created = await owner.SendAsync(await owner.Post("/api/projects", new { name = "Ledger" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var projectId = await ReadFieldAsync(created, "id");

        using var read = await owner.GetAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // Every team route refuses in Community, exactly like the tenancy boundary's own
        // Hosted-only paths: the single-user behavior gains nothing and loses nothing.
        using var invite = await owner.SendAsync(await owner.Post(
            $"/api/hosted/tenants/{InstallationId:D}/invitations",
            new { email = "someone@example.com", role = "viewer" }));
        Assert.Equal(HttpStatusCode.Unauthorized, invite.StatusCode);
        Assert.Equal("oidc_login_failed", await ReadCodeAsync(invite));

        using var access = await owner.GetAsync($"/api/projects/{projectId}/team-access");
        Assert.Equal(HttpStatusCode.Unauthorized, access.StatusCode);
        Assert.Equal("oidc_login_failed", await ReadCodeAsync(access));
    }

    // ===========================================================================================
    // Fixture helpers. Every id under test is read back from a response; only the clock, the
    // installation and the user ids the service generates are read back from the database.
    // ===========================================================================================

    private async Task<string> ProvisionedDatabaseAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using (var host = ChronicleafApiHost.Start(database, Clock))
        {
            using var scope = host.Services.CreateScope();
            var provider = scope.ServiceProvider;

            await provider.GetRequiredService<IUnitOfWork>().ExecuteAsync(
                token => provider.GetRequiredService<IInstallationStore>().ProvisionAsync(
                    InstallationId,
                    ChronicleafDbContext.RequiredSchemaVersion,
                    InstallationStore.ProvisionedAction,
                    actorId: null,
                    new OutboxPayload
                    {
                        Kind = InstallationStore.ProvisionedAction,
                        ScopeId = InstallationId,
                        AggregateId = InstallationId,
                        Revision = 1,
                    },
                    token));
        }

        return database;
    }

    private static async Task<string> UserIdAsync(string connectionString, string subject) =>
        (await ChronicleafPostgresFixture.ReadTextAsync(
            connectionString,
            $"SELECT id::text FROM hosted_users WHERE issuer = '{FakeOidcHandler.Issuer}'"
            + $" AND subject = '{subject}'"))!;

    private static async Task<string> InvitationIdAsync(string connectionString, string tenantId, string email) =>
        (await ChronicleafPostgresFixture.ReadTextAsync(
            connectionString,
            $"SELECT id::text FROM tenant_invitations WHERE tenant_id = '{tenantId}' AND email = '{email}'"))!;

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<string> ReadFieldAsync(HttpResponseMessage response, string field)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(field).GetString()!;
    }

    private static async Task<long> ProjectRevisionAsync(TeamClient client, string projectId)
    {
        using var response = await client.GetAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("revision").GetInt64();
    }
}

/// <summary>
/// An authenticated Hosted user, driving the real team routes the way the browser would: the
/// OIDC start and callback for real, then a real session cookie, the real origin and a real
/// antiforgery value, requested per mutation exactly as the delivered sign-out does it.
/// </summary>
internal sealed class TeamClient : IDisposable
{
    private const string CookieName = "chronicleaf_session";
    private const string AntiforgeryHeader = "X-Chronicleaf-Antiforgery";

    private readonly HttpClient _client;
    private readonly string _cookie;

    private TeamClient(HttpClient client, string cookie)
    {
        _client = client;
        _cookie = cookie;
    }

    /// <summary>Runs the verified login for one subject against the fake issuer.</summary>
    public static async Task<TeamClient> LoginAsync(
        WebApplicationFactory<Program> host,
        FakeOidcHandler oidc,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(oidc);

        oidc.Subject = subject;
        var client = host.CreateClient();

        using var start = await client.PostAsJsonAsync("/api/hosted/login/start", new { });
        start.EnsureSuccessStatusCode();
        string state;
        using (var document = JsonDocument.Parse(await start.Content.ReadAsStringAsync()))
        {
            state = document.RootElement.GetProperty("state").GetString()!;
            oidc.PresentedNonce = TeamLoginNonce(document.RootElement.GetProperty("authorizationUrl").GetString()!);
        }

        using var callback = await client.PostAsJsonAsync(
            "/api/hosted/login/callback",
            new { code = $"test-code-{Guid.NewGuid():N}", state });
        callback.EnsureSuccessStatusCode();

        var cookie = callback.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=', 2)[1];

        return new TeamClient(client, cookie);
    }

    private static string TeamLoginNonce(string authorizationUrl)
    {
        var query = new Uri(authorizationUrl).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "nonce")
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        throw new InvalidOperationException("The authorization URL carries no nonce.");
    }

    public Task<HttpResponseMessage> GetAsync(string path) =>
        _client.SendAsync(Decorate(new HttpRequestMessage(HttpMethod.Get, path)));

    /// <summary>Sends a request built by <see cref="Post"/>, <see cref="Patch"/> or <see cref="Delete"/>.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _client.SendAsync(request);

    /// <summary>A mutation with no revision, used for the team actions that do not take one.</summary>
    public Task<HttpRequestMessage> Post(string path, object? body) => Build(HttpMethod.Post, path, body);

    /// <summary>A membership change that must present the revision it is editing.</summary>
    public Task<HttpRequestMessage> Patch(string path, object? body, long revision) =>
        Build(new HttpMethod("PATCH"), path, body, revision);

    /// <summary>A membership change sent without its revision, which is always refused.</summary>
    public Task<HttpRequestMessage> PatchWithoutRevision(string path, object? body) =>
        Build(new HttpMethod("PATCH"), path, body, null);

    /// <summary>A revocation, which takes no body and no revision.</summary>
    public Task<HttpRequestMessage> Delete(string path) => Build(HttpMethod.Delete, path, null);

    /// <summary>Creates a tenant this user owns; creation selects it onto the session.</summary>
    public async Task<string> CreateTenantAsync(string name)
    {
        using var created = await SendAsync(await Post("/api/hosted/tenants", new { name }));
        created.EnsureSuccessStatusCode();
        return await ReadFieldAsync(created, "id");
    }

    /// <summary>Creates a project under the session's active tenant.</summary>
    public async Task<string> CreateProjectAsync(string name)
    {
        using var created = await SendAsync(await Post("/api/projects", new { name }));
        created.EnsureSuccessStatusCode();
        return await ReadFieldAsync(created, "id");
    }

    /// <summary>Invites one person and returns the once-disclosed bearer token.</summary>
    public async Task<string> InviteAsync(string tenantId, string email, string role)
    {
        using var invited = await SendAsync(await Post(
            $"/api/hosted/tenants/{tenantId}/invitations",
            new { email, role }));
        invited.EnsureSuccessStatusCode();
        return await ReadFieldAsync(invited, "token");
    }

    /// <summary>Accepts an invitation token.</summary>
    public async Task AcceptAsync(string token)
    {
        using var accepted = await SendAsync(await Post("/api/hosted/invitations/accept", new { token }));
        accepted.EnsureSuccessStatusCode();
    }

    /// <summary>Grants one project permission; the grant is explicit and stored.</summary>
    public async Task GrantAsync(string projectId, string userId, string permission)
    {
        using var granted = await SendAsync(await Post(
            $"/api/projects/{projectId}/members",
            new { userId, permission }));
        granted.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();

    private static async Task<string> ReadFieldAsync(HttpResponseMessage response, string field)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(field).GetString()!;
    }

    private async Task<HttpRequestMessage> Build(HttpMethod method, string path, object? body, long? revision = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body ?? new { }),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Add("Cookie", $"{CookieName}={_cookie}");
        request.Headers.TryAddWithoutValidation("Origin", OwnerClient.PublicOrigin);
        request.Headers.Add(AntiforgeryHeader, await ReadAntiforgeryAsync());

        if (revision is { } expected)
        {
            request.Headers.TryAddWithoutValidation(
                "If-Match",
                expected.ToString(CultureInfo.InvariantCulture));
        }

        return request;
    }

    private HttpRequestMessage Decorate(HttpRequestMessage request)
    {
        request.Headers.Add("Cookie", $"{CookieName}={_cookie}");

        return request;
    }

    private async Task<string> ReadAntiforgeryAsync()
    {
        using var request = Decorate(new HttpRequestMessage(HttpMethod.Get, "/api/session/antiforgery"));
        using var response = await _client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("token").GetString()!;
    }
}
