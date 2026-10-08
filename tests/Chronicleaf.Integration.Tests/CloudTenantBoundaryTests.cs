using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Application.Tenancy;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// CLOUD-TENANT-BOUNDARY-001 and CLOUD-TENANT-BOUNDARY-002 against a real disposable PostgreSQL
/// started from a pinned Docker image, through the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// Nothing here is simulated. Tenants, memberships, scopes, sessions, projects, media, credentials
/// and operations are all produced by a real PostgreSQL server, and every claim is read back over
/// the wire - a status code, a header, a body - or over a raw connection as a row or a column.
/// The one declared substitution is the OIDC issuer itself: a <see cref="FakeOidcHandler"/> answers
/// the discovery document, the key set and the token endpoint from scripted bytes, while the state
/// lookup, the issuer/audience/lifetime/nonce checks and the RSA signature verification are the
/// production code under test. An unavailable container is a failure, never a passing result.
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class CloudTenantBoundaryTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The installation every scenario provisions, and therefore the Community scope.</summary>
    private static readonly Guid InstallationId = new("c7d2a41f-9e03-4b5a-8d26-1f3e5a709211");

    // ===========================================================================================
    // CLOUD-TENANT-BOUNDARY-001 Success: a verified user creates and selects an owned tenant, and
    // private requests resolve the server-derived tenant scope.
    // ===========================================================================================

    [Fact]
    public async Task Verified_user_creates_owned_tenant_and_private_requests_resolve_its_scope()
    {
        Assert.True(postgres.IsRealPostgres, "the tenancy scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("tenant_success");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var user = await HostedClient.LoginAsync(host, oidc);

        using var created = await user.SendAsync(await user.Post("/api/hosted/tenants", new { name = "Acme" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string tenantId;
        string scopeId;
        using (var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
        {
            tenantId = document.RootElement.GetProperty("id").GetString()!;
            scopeId = (await ReadTenantScopeAsync(database, tenantId))!;
            Assert.Equal("Acme", document.RootElement.GetProperty("name").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal("owner", document.RootElement.GetProperty("role").GetString());
        }

        // Creating the tenant selected it: the tenant's own scope - a fresh id, never the
        // installation scope - is what domain rows carry from here on.
        Assert.NotEqual(InstallationId.ToString("D"), scopeId);

        using var project = await user.SendAsync(await user.Post("/api/projects", new { name = "Ledger" }));
        Assert.Equal(HttpStatusCode.Created, project.StatusCode);
        var projectId = await ReadFieldAsync(project, "id");

        Assert.Equal(
            scopeId,
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT scope_id::text FROM projects WHERE id = '{projectId}'"));

        // The membership list depicts exactly this tenant with its scope, and a second login of
        // the same verified subject provisions no second user: the tenant is still listed once.
        using var listed = await user.GetAsync("/api/hosted/tenants");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using (var document = JsonDocument.Parse(await listed.Content.ReadAsStringAsync()))
        {
            var tenants = document.RootElement.GetProperty("tenants").EnumerateArray().ToArray();
            var single = Assert.Single(tenants);
            Assert.Equal(tenantId, single.GetProperty("tenantId").GetString());
            Assert.Equal(scopeId, single.GetProperty("scopeId").GetString());
            Assert.Equal("owner", single.GetProperty("role").GetString());
        }

        using var second = await HostedClient.LoginAsync(host, oidc);
        using var relisted = await second.GetAsync("/api/hosted/tenants");
        Assert.Equal(HttpStatusCode.OK, relisted.StatusCode);
        using (var document = JsonDocument.Parse(await relisted.Content.ReadAsStringAsync()))
        {
            Assert.Single(document.RootElement.GetProperty("tenants").EnumerateArray().ToArray());
        }

        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT count(*)::text FROM hosted_users"));
    }

    // ===========================================================================================
    // CLOUD-TENANT-BOUNDARY-001 Failure: an invalid OIDC exchange or operator token issues no
    // Hosted session.
    // ===========================================================================================

    [Fact]
    public async Task Invalid_oidc_exchange_or_operator_token_issues_no_hosted_session()
    {
        Assert.True(postgres.IsRealPostgres, "the tenancy scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("tenant_failures");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var anonymous = host.CreateClient();

        // An unknown state completes nothing and sets no cookie.
        using (var refused = await anonymous.PostAsJsonAsync(
            "/api/hosted/login/callback",
            new { code = "some-code", state = "no-such-state" }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("oidc_login_failed", await ReadCodeAsync(refused));
            Assert.False(refused.Headers.Contains("Set-Cookie"));
        }

        // A state is single-use: the second completion of a successful login is refused.
        var (firstState, _) = await StartLoginAsync(anonymous, oidc);
        using (var first = await anonymous.PostAsJsonAsync(
            "/api/hosted/login/callback",
            new { code = "code-one", state = firstState }))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        using (var replay = await anonymous.PostAsJsonAsync(
            "/api/hosted/login/callback",
            new { code = "code-two", state = firstState }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
            Assert.Equal("oidc_login_failed", await ReadCodeAsync(replay));
        }

        // A nonce the login did not start is refused.
        oidc.NonceOverride = "attacker-nonce";
        try
        {
            var (nonceState, _) = await StartLoginAsync(anonymous, oidc);
            using var refused = await anonymous.PostAsJsonAsync(
                "/api/hosted/login/callback",
                new { code = "code-nonce", state = nonceState });
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("oidc_login_failed", await ReadCodeAsync(refused));
        }
        finally
        {
            oidc.NonceOverride = null;
        }

        // A token from another issuer is refused.
        oidc.IssuerOverride = "http://127.0.0.1:8";
        try
        {
            var (issuerState, _) = await StartLoginAsync(anonymous, oidc);
            using var refused = await anonymous.PostAsJsonAsync(
                "/api/hosted/login/callback",
                new { code = "code-issuer", state = issuerState });
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("oidc_login_failed", await ReadCodeAsync(refused));
        }
        finally
        {
            oidc.IssuerOverride = null;
        }

        // A token signed by a key outside the issuer's key set is refused.
        oidc.UseWrongKey = true;
        try
        {
            var (keyState, _) = await StartLoginAsync(anonymous, oidc);
            using var refused = await anonymous.PostAsJsonAsync(
                "/api/hosted/login/callback",
                new { code = "code-key", state = keyState });
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("oidc_login_failed", await ReadCodeAsync(refused));
        }
        finally
        {
            oidc.UseWrongKey = false;
        }

        // An expired login start is refused: the challenge is short-lived by stored instant.
        var (expiredState, _) = await StartLoginAsync(anonymous, oidc);
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            "UPDATE hosted_login_requests SET expires_at = '2026-10-16T11:00:00+00:00'::timestamptz");
        using (var refused = await anonymous.PostAsJsonAsync(
            "/api/hosted/login/callback",
            new { code = "code-expired", state = expiredState }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("oidc_login_failed", await ReadCodeAsync(refused));
        }

        // An operator token presented in Hosted mode is refused: there is no shared
        // manager/operator token here, and the failure is the generic credential refusal.
        using (var refused = await anonymous.PostAsJsonAsync(
            "/api/session",
            new { accessToken = OwnerClient.AccessToken }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("invalid_credentials", await ReadCodeAsync(refused));
        }

        // Exactly one session exists: the single successful login. Every refusal above wrote
        // no session and no user beyond the first.
        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT count(*)::text FROM hosted_sessions"));
        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT count(*)::text FROM hosted_users"));
    }

    // ===========================================================================================
    // CLOUD-TENANT-BOUNDARY-001 Boundary: a revoked membership denies subsequent access to that
    // tenant.
    // ===========================================================================================

    [Fact]
    public async Task Revoked_membership_denies_subsequent_access_to_that_tenant()
    {
        Assert.True(postgres.IsRealPostgres, "the tenancy scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("tenant_revocation");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        oidc.Subject = "owner-1";
        using var owner = await HostedClient.LoginAsync(host, oidc);
        oidc.Subject = "member-1";
        using var member = await HostedClient.LoginAsync(host, oidc);

        var tenantId = await CreateTenantIdAsync(owner, "Acme");
        var memberUserId = await ReadUserIdAsync(database, "http://127.0.0.1:7", "member-1");

        // Teams own invitations; this package owns revocation. The membership row below is
        // written the way no route in this scope could create it, so the revocation can prove
        // what it denies.
        await InsertMembershipAsync(database, tenantId, memberUserId, "editor");
        using var selected = await member.SendAsync(
            await member.Post("/api/hosted/tenants/select", new { tenantId }));
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);

        using var memberProject = await member.SendAsync(
            await member.Post("/api/projects", new { name = "Atlas" }));
        Assert.Equal(HttpStatusCode.Created, memberProject.StatusCode);
        var memberProjectId = await ReadFieldAsync(memberProject, "id");

        // The owner revokes the member: the member's session under this tenant dies with it.
        using var revoked = await owner.SendAsync(
            await owner.Delete($"/api/hosted/tenants/{tenantId}/members/{memberUserId}"));
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        using var denied = await member.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        // Re-logged in, the former member lists no tenant and reaches neither the project nor
        // the tenant: the membership row is stamped, not deleted, and nothing of the tenant's
        // data answers.
        oidc.Subject = "member-1";
        using var relogged = await HostedClient.LoginAsync(host, oidc);
        using var empty = await relogged.GetAsync("/api/hosted/tenants");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        using (var document = JsonDocument.Parse(await empty.Content.ReadAsStringAsync()))
        {
            Assert.Empty(document.RootElement.GetProperty("tenants").EnumerateArray().ToArray());
        }

        using var projectDenied = await relogged.GetAsync($"/api/projects/{memberProjectId}");
        Assert.Equal(HttpStatusCode.Conflict, projectDenied.StatusCode);
        Assert.Equal("tenant_selection_required", await ReadCodeAsync(projectDenied));

        // The owner's access is untouched: the tenant, its project and the member's old
        // project all still resolve under the owner's session.
        using var ownerProject = await owner.GetAsync($"/api/projects/{memberProjectId}");
        Assert.Equal(HttpStatusCode.OK, ownerProject.StatusCode);

        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT count(*)::text FROM tenant_memberships WHERE tenant_id = '{tenantId}' " +
                $"AND user_id = '{memberUserId}' AND revoked_at IS NOT NULL"));
    }

    // ===========================================================================================
    // CLOUD-TENANT-BOUNDARY-002 Success: two tenants sharing one pooled connection each see only
    // their own scoped rows.
    // ===========================================================================================

    [Fact]
    public async Task Two_tenants_on_one_pooled_connection_each_see_only_their_own_rows()
    {
        Assert.True(postgres.IsRealPostgres, "the tenancy scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("tenant_rls");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        oidc.Subject = "tenant-a-user";
        using var userA = await HostedClient.LoginAsync(host, oidc);
        oidc.Subject = "tenant-b-user";
        using var userB = await HostedClient.LoginAsync(host, oidc);

        var tenantA = await CreateTenantIdAsync(userA, "Tenant A");
        var tenantB = await CreateTenantIdAsync(userB, "Tenant B");

        // The fixture's own role is a superuser, and PostgreSQL superusers bypass row-level
        // security unconditionally - FORCE affects ordinary owners, never superusers. The
        // policies are therefore exercised as a least-privilege role holding exactly the table
        // grants a Hosted deployment connects with, which is the role the policies constrain.
        var reader = $"tr_{Guid.NewGuid():N}";
        const string readerPassword = "tenant-reader-test-password";
        var builder = new NpgsqlConnectionStringBuilder(database);
        await using (var admin = new NpgsqlConnection(database))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText =
                $"CREATE ROLE \"{reader}\" WITH LOGIN PASSWORD '{readerPassword}';" +
                $"GRANT CONNECT ON DATABASE \"{builder.Database}\" TO \"{reader}\";" +
                $"GRANT USAGE ON SCHEMA public TO \"{reader}\";" +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public " +
                $"TO \"{reader}\"";
            await command.ExecuteNonQueryAsync();
        }

        builder.Username = reader;
        builder.Password = readerPassword;

        // One pooled connection, borrowed for both tenants in turn. SET LOCAL dies with each
        // transaction, so the second tenant cannot inherit the first tenant's context - and the
        // row-level policies filter what each transaction reads.
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await AssertTenantSeesOnlyAsync(connection, database, tenantA, tenantB);
        await AssertTenantSeesOnlyAsync(connection, database, tenantB, tenantA);

        // With no tenant context - every Community transaction, and any Hosted read outside a
        // tenant context - both tenants' rows read as before, so a deployment without tenants
        // behaves exactly as it always has. This also proves the pooled connection carries no
        // usable residue: a custom variable first touched by SET LOCAL reads back empty rather
        // than NULL afterwards, and the policies treat empty as unset rather than as a tenant.
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.Equal(2, await CountAsync(connection, transaction, "SELECT count(*) FROM tenants"));
            Assert.Equal(2, await CountAsync(connection, transaction, "SELECT count(*) FROM tenant_memberships"));
            Assert.Equal(2, await CountAsync(connection, transaction, "SELECT count(*) FROM tenant_scopes"));
            await transaction.RollbackAsync();
        }
    }

    // ===========================================================================================
    // CLOUD-TENANT-BOUNDARY-002 Failure: a guessed foreign project, media, credential or
    // operation id returns no foreign data.
    // ===========================================================================================

    [Fact]
    public async Task Guessed_foreign_project_media_credential_and_operation_ids_return_no_foreign_data()
    {
        Assert.True(postgres.IsRealPostgres, "the tenancy scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("tenant_guessed");
        using var oidc = new FakeOidcHandler(Clock);
        using var keyFile = OperatorKeyFile.Create();
        var mediaRoot = Path.Combine(Path.GetTempPath(), $"chronicleaf-tenant-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mediaRoot);
        try
        {
            await using var host = CloudTenantBoundaryApiHost.Start(
                database, Clock, oidc, keyFile.FilePath, mediaRoot);
            oidc.Subject = "guesser-a";
            using var userA = await HostedClient.LoginAsync(host, oidc);
            oidc.Subject = "guesser-b";
            using var userB = await HostedClient.LoginAsync(host, oidc);

            var tenantA = await CreateTenantIdAsync(userA, "Tenant A");
            _ = await CreateTenantIdAsync(userB, "Tenant B");

            var projectId = await CreateProjectIdAsync(userA, "Ledger");

            var png = TestPng.Create(2, 2);
            using var uploaded = await userA.SendAsync(
                await userA.PostBytes($"/api/media?projectId={projectId}", png, "image/png"));
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var mediaId = await ReadFieldAsync(uploaded, "id");

            // The storage key carries the tenant's scope, so bytes on disk are namespaced before
            // any lookup runs.
            var tenantScope = await ReadTenantScopeAsync(database, tenantA);
            Assert.Contains($"/{tenantScope}/", (await ReadFieldAsync(uploaded, "storageKey"))!);

            await SaveCredentialAsync(userA, "github", "tenant-a-secret", 0);

            var operationA = Guid.NewGuid();
            var operationB = Guid.NewGuid();
            await InsertOperationAsync(database, operationA, tenantScope);
            await InsertOperationAsync(database, operationB, await ReadTenantScopeAsync(database, await TenantOfAsync(userB)));

            // Every guessed foreign id is the same refusal as an invented one: the scoped lookup
            // finds nothing, and the envelope says so without naming the tenant.
            using var projectDenied = await userB.GetAsync($"/api/projects/{projectId}");
            Assert.Equal(HttpStatusCode.NotFound, projectDenied.StatusCode);
            Assert.Equal("not_found", await ReadCodeAsync(projectDenied));

            using var invented = await userB.GetAsync($"/api/projects/{Guid.NewGuid():D}");
            Assert.Equal(HttpStatusCode.NotFound, invented.StatusCode);
            Assert.Equal("not_found", await ReadCodeAsync(invented));

            using var mediaDenied = await userB.GetAsync($"/api/media/{mediaId}");
            Assert.Equal(HttpStatusCode.NotFound, mediaDenied.StatusCode);

            using var operationDenied = await userB.GetAsync($"/api/operations/{operationA:D}");
            Assert.Equal(HttpStatusCode.NotFound, operationDenied.StatusCode);

            using var operations = await userB.GetAsync("/api/operations");
            Assert.Equal(HttpStatusCode.OK, operations.StatusCode);
            using (var document = JsonDocument.Parse(await operations.Content.ReadAsStringAsync()))
            {
                var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
                var single = Assert.Single(items);
                Assert.Equal(operationB.ToString("D"), single.GetProperty("id").GetString());
            }

            // Credentials have no get-by-id route; the scoped describe and list carry the same
            // property: the foreign credential is absent from both.
            using var describe = await userB.GetAsync("/api/settings/credentials/github");
            Assert.Equal(HttpStatusCode.OK, describe.StatusCode);
            using (var document = JsonDocument.Parse(await describe.Content.ReadAsStringAsync()))
            {
                Assert.False(document.RootElement.GetProperty("configured").GetBoolean());
            }

            // The foreign credential stays sealed to its own tenant while each tenant keeps an
            // independent vault under the same provider.
            await SaveCredentialAsync(userB, "github", "tenant-b-secret", 0);
            using var ownDescribe = await userB.GetAsync("/api/settings/credentials/github");
            Assert.Equal(HttpStatusCode.OK, ownDescribe.StatusCode);
            using (var document = JsonDocument.Parse(await ownDescribe.Content.ReadAsStringAsync()))
            {
                Assert.True(document.RootElement.GetProperty("configured").GetBoolean());
            }

            using var ownListed = await userB.GetAsync("/api/settings/credentials");
            Assert.Equal(HttpStatusCode.OK, ownListed.StatusCode);
            using (var document = JsonDocument.Parse(await ownListed.Content.ReadAsStringAsync()))
            {
                Assert.Single(document.RootElement.GetProperty("items").EnumerateArray().ToArray());
            }

            using var stillConfigured = await userA.GetAsync("/api/settings/credentials/github");
            Assert.Equal(HttpStatusCode.OK, stillConfigured.StatusCode);
            using (var document = JsonDocument.Parse(await stillConfigured.Content.ReadAsStringAsync()))
            {
                Assert.True(document.RootElement.GetProperty("configured").GetBoolean());
            }

            // Both credentials exist as sealed rows under different scopes: two rows, two
            // scopes, no shared bytes.
            Assert.Equal(
                "2",
                await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT count(*)::text FROM credentials"));
            Assert.Equal(
                "2",
                await ChronicleafPostgresFixture.ReadTextAsync(
                    database,
                    "SELECT count(DISTINCT scope_id)::text FROM credentials"));
        }
        finally
        {
            Directory.Delete(mediaRoot, recursive: true);
        }
    }

    // ===========================================================================================
    // CLOUD-TENANT-BOUNDARY-002 Boundary: a Community database opened in Hosted mode rejects
    // silent reassignment until the explicit import is recorded.
    // ===========================================================================================

    [Fact]
    public async Task Community_database_in_hosted_mode_rejects_silent_reassignment_until_explicit_import()
    {
        Assert.True(postgres.IsRealPostgres, "the tenancy scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("tenant_import");

        // The Community volume first: an owner project under the installation scope.
        await using (var community = ChronicleafApiHost.StartWithAccess(
            database, Clock, OwnerClient.AccessTokenHash, OwnerClient.PublicOrigin))
        {
            using var owner = await OwnerClient.CreateAsync(community);
            using var project = await owner.SendAsync(
                await owner.Post("/api/projects", new { name = "Legacy" }));
            Assert.Equal(HttpStatusCode.Created, project.StatusCode);
        }

        // The same database, now in Hosted mode. Logging in works and creating a tenant works -
        // neither addresses a Community row - but acting under the tenant stops.
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        using var user = await HostedClient.LoginAsync(host, oidc);
        var tenantId = await CreateTenantIdAsync(user, "Acme");

        using var refused = await user.SendAsync(
            await user.Post("/api/projects", new { name = "Acme Ledger" }));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("community_import_required", await ReadCodeAsync(refused));

        // The explicit import records the acknowledgment without moving any Community row.
        using var imported = await user.SendAsync(
            await user.Post("/api/hosted/import", new { tenantId }));
        Assert.Equal(HttpStatusCode.NoContent, imported.StatusCode);

        using var created = await user.SendAsync(
            await user.Post("/api/projects", new { name = "Acme Ledger" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var tenantScope = await ReadTenantScopeAsync(database, tenantId);
        Assert.NotEqual(InstallationId.ToString("D"), tenantScope);
        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT count(*)::text FROM projects WHERE scope_id = '{InstallationId:D}'"));
        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT count(*)::text FROM projects WHERE scope_id = '{tenantScope}'"));
        Assert.Equal(
            "1",
            await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT count(*)::text FROM hosted_imports"));

        // The Community project is still unreachable to the tenant: its scope belongs to nobody
        // a hosted session can address.
        var legacyId = await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT id::text FROM projects WHERE scope_id = '{InstallationId:D}'");
        using var legacyDenied = await user.GetAsync($"/api/projects/{legacyId}");
        Assert.Equal(HttpStatusCode.NotFound, legacyDenied.StatusCode);
        Assert.Equal("not_found", await ReadCodeAsync(legacyDenied));
    }

    // ===========================================================================================
    // Community behavior is unchanged: the owner boundary works as before and Hosted identity
    // refuses in Community mode.
    // ===========================================================================================

    [Fact]
    public async Task Community_behavior_is_unchanged_without_tenants()
    {
        Assert.True(postgres.IsRealPostgres, "the tenancy scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("tenant_community");
        await using var host = ChronicleafApiHost.StartWithAccess(
            database, Clock, OwnerClient.AccessTokenHash, OwnerClient.PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        using var project = await owner.SendAsync(
            await owner.Post("/api/projects", new { name = "Ledger" }));
        Assert.Equal(HttpStatusCode.Created, project.StatusCode);

        // Hosted identity does not exist here: starting a login is the generic refusal, and a
        // private tenancy route under an owner session is the same refusal rather than a scope.
        using var anonymous = host.CreateClient();
        using var startRefused = await anonymous.PostAsJsonAsync("/api/hosted/login/start", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, startRefused.StatusCode);
        Assert.Equal("oidc_login_failed", await ReadCodeAsync(startRefused));

        using var tenantsRefused = await owner.GetAsync("/api/hosted/tenants");
        Assert.Equal(HttpStatusCode.Unauthorized, tenantsRefused.StatusCode);
        Assert.Equal("oidc_login_failed", await ReadCodeAsync(tenantsRefused));

        // No tenancy table holds a row: the Community flow wrote nothing anywhere else.
        foreach (var table in new[]
            {
                "hosted_users", "tenants", "tenant_memberships", "tenant_scopes",
                "hosted_sessions", "hosted_login_requests", "hosted_imports",
            })
        {
            Assert.Equal(
                "0",
                await ChronicleafPostgresFixture.ReadTextAsync(database, $"SELECT count(*)::text FROM {table}"));
        }
    }

    // ===========================================================================================
    // Validation, quota and the migration itself.
    // ===========================================================================================

    [Fact]
    public async Task Tenant_validation_quota_and_membership_rules_name_their_failure()
    {
        Assert.True(postgres.IsRealPostgres, "the tenancy scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("tenant_rules");
        using var oidc = new FakeOidcHandler(Clock);
        await using var host = CloudTenantBoundaryApiHost.Start(database, Clock, oidc);
        oidc.Subject = "rules-a";
        using var userA = await HostedClient.LoginAsync(host, oidc);
        oidc.Subject = "rules-b";
        using var userB = await HostedClient.LoginAsync(host, oidc);

        // A name is required and bounded.
        using var blank = await userA.SendAsync(await userA.Post("/api/hosted/tenants", new { name = "   " }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.NotNull(await ReadFieldErrorAsync(blank, "name"));

        using var tooLong = await userA.SendAsync(
            await userA.Post("/api/hosted/tenants", new { name = new string('n', 161) }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.NotNull(await ReadFieldErrorAsync(tooLong, "name"));

        var tenantA = await CreateTenantIdAsync(userA, "Tenant A");

        // One tenant per account until an operator quota says otherwise.
        using var second = await userA.SendAsync(await userA.Post("/api/hosted/tenants", new { name = "Tenant A2" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("tenant_limit_reached", await ReadCodeAsync(second));

        // A fresh session selects nothing, so domain access names the missing selection.
        oidc.Subject = "rules-b";
        using var fresh = await HostedClient.LoginAsync(host, oidc);
        using var unselected = await fresh.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Conflict, unselected.StatusCode);
        Assert.Equal("tenant_selection_required", await ReadCodeAsync(unselected));

        // A foreign tenant and an invented id are the same 404.
        using var foreign = await fresh.SendAsync(
            await fresh.Post("/api/hosted/tenants/select", new { tenantId = tenantA }));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("not_found", await ReadCodeAsync(foreign));

        using var invented = await fresh.SendAsync(
            await fresh.Post("/api/hosted/tenants/select", new { tenantId = Guid.NewGuid().ToString("D") }));
        Assert.Equal(HttpStatusCode.NotFound, invented.StatusCode);
        Assert.Equal("not_found", await ReadCodeAsync(invented));

        // A malformed tenant id is a validation failure naming its field.
        using var malformed = await fresh.SendAsync(
            await fresh.Post("/api/hosted/tenants/select", new { tenantId = "not-a-tenant" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
        Assert.NotNull(await ReadFieldErrorAsync(malformed, "tenantId"));

        var tenantB = await CreateTenantIdAsync(userB, "Tenant B");

        // A caller with no live membership cannot administer one: the tenant answers
        // denial rather than a lookup, whether the tenant is foreign or merely not theirs.
        var userAId = await ReadUserIdAsync(database, FakeOidcHandler.Issuer, "rules-a");
        using var nonOwner = await userB.SendAsync(
            await userB.Delete($"/api/hosted/tenants/{tenantA}/members/{userAId}"));
        Assert.Equal(HttpStatusCode.Forbidden, nonOwner.StatusCode);
        Assert.Equal("tenant_access_denied", await ReadCodeAsync(nonOwner));

        // Revoking a user who holds no live membership is the same 404 as an invented tenant.
        using var nonMember = await userB.SendAsync(
            await userB.Delete($"/api/hosted/tenants/{tenantB}/members/{userAId}"));
        Assert.Equal(HttpStatusCode.NotFound, nonMember.StatusCode);
        Assert.Equal("not_found", await ReadCodeAsync(nonMember));

        using var selfRevoke = await userA.SendAsync(
            await userA.Delete($"/api/hosted/tenants/{tenantA}/members/{userAId}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, selfRevoke.StatusCode);
        Assert.NotNull(await ReadFieldErrorAsync(selfRevoke, "userId"));
    }

    [Fact]
    public async Task The_tenant_tables_and_policies_come_from_the_forward_migration_and_readiness_requires_it()
    {
        var database = await postgres.CreateDatabaseAsync("tenant_migration");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        // Readiness requires the tenancy migration by id, and it is the newest required one by
        // position rather than by name.
        Assert.Contains(
            ChronicleafSchema.CloudTenantBoundaryMigrationId,
            ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));

        // A database stopped at the agency schema is short of exactly this one.
        var missing = ChronicleafSchema.MissingMigrationIds(
            ChronicleafSchema.RequiredMigrationIds
                .Where(id => id != ChronicleafSchema.CloudTenantBoundaryMigrationId)
                .ToArray());
        Assert.Equal([ChronicleafSchema.CloudTenantBoundaryMigrationId], missing);

        // The tables this package owns exist with the bounds this package enforces.
        Assert.Equal(
            "160",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT character_maximum_length::text FROM information_schema.columns " +
                "WHERE table_name = 'tenants' AND column_name = 'name'"));
        Assert.Equal(
            "uuid",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT data_type FROM information_schema.columns " +
                "WHERE table_name = 'tenant_scopes' AND column_name = 'tenant_id'"));
        Assert.Equal(
            "fk_tenant_memberships_tenant_id",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT conname FROM pg_constraint WHERE conname = 'fk_tenant_memberships_tenant_id'"));
        Assert.Equal(
            "ux_hosted_users_issuer_subject",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT indexname FROM pg_indexes WHERE tablename = 'hosted_users' " +
                "AND indexname = 'ux_hosted_users_issuer_subject'"));
        Assert.Equal(
            "ux_hosted_sessions_id_hash",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT indexname FROM pg_indexes WHERE tablename = 'hosted_sessions' " +
                "AND indexname = 'ux_hosted_sessions_id_hash'"));

        // Row-level security is enforced on the tenant tables, including for the table owner:
        // the policies the migration installs are what the pooled-connection scenario exercises.
        foreach (var table in new[] { "tenants", "tenant_memberships", "tenant_scopes" })
        {
            Assert.Equal(
                "true",
                await ChronicleafPostgresFixture.ReadTextAsync(
                    database,
                    $"SELECT relforcerowsecurity::text FROM pg_class WHERE relname = '{table}'"));
            Assert.Equal(
                "tenant_isolation",
                await ChronicleafPostgresFixture.ReadTextAsync(
                    database,
                    $"SELECT policyname FROM pg_policies WHERE tablename = '{table}'"));
        }
    }

    // ===========================================================================================
    // Fixture helpers. Every id under test is read back from a response; only the membership a
    // later package will invite through - which no route in this scope can create - is written
    // by hand.
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

    private static async Task<(string State, string AuthorizationUrl)> StartLoginAsync(
        HttpClient anonymous,
        FakeOidcHandler oidc)
    {
        using var start = await anonymous.PostAsJsonAsync("/api/hosted/login/start", new { });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var document = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var state = document.RootElement.GetProperty("state").GetString()!;
        var authorizationUrl = document.RootElement.GetProperty("authorizationUrl").GetString()!;
        Assert.Contains(FakeOidcHandler.Issuer, authorizationUrl);
        Assert.Contains(Uri.EscapeDataString(FakeOidcHandler.ClientId), authorizationUrl);
        oidc.PresentedNonce = ParseQuery(authorizationUrl, "nonce");
        Assert.False(string.IsNullOrWhiteSpace(oidc.PresentedNonce));
        return (state, authorizationUrl);
    }

    private static string? ParseQuery(string url, string name)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == name)
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    private static async Task<string> CreateTenantIdAsync(HostedClient user, string name)
    {
        using var response = await user.SendAsync(await user.Post("/api/hosted/tenants", new { name }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadFieldAsync(response, "id");
    }

    private static async Task<string> CreateProjectIdAsync(HostedClient user, string name)
    {
        using var response = await user.SendAsync(
            await user.Post("/api/projects", new { name, description = $"{name} project." }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadFieldAsync(response, "id");
    }

    private static async Task SaveCredentialAsync(HostedClient user, string provider, string value, long revision)
    {
        using var request = await user.Put($"/api/settings/credentials/{provider}", new { value }, revision);
        using var response = await user.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<string> TenantOfAsync(HostedClient user)
    {
        using var response = await user.GetAsync("/api/hosted/tenants");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("tenants").EnumerateArray().First().GetProperty("tenantId").GetString()!;
    }

    private static async Task<string> ReadTenantScopeAsync(string connectionString, string tenantId) =>
        (await ChronicleafPostgresFixture.ReadTextAsync(
            connectionString,
            $"SELECT id::text FROM tenant_scopes WHERE tenant_id = '{tenantId}'"))!;

    private static async Task<string> ReadUserIdAsync(string connectionString, string issuer, string subject) =>
        (await ChronicleafPostgresFixture.ReadTextAsync(
            connectionString,
            $"SELECT id::text FROM hosted_users WHERE issuer = '{issuer}' AND subject = '{subject}'"))!;

    /// <summary>
    /// Stores one membership the way a future invitation would: no route in this scope creates
    /// one, so the revocation scenario seeds it by hand and the revocation proves what it denies.
    /// </summary>
    private static async Task InsertMembershipAsync(string connectionString, string tenantId, string userId, string role)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO tenant_memberships (tenant_id, user_id, role, created_at, revoked_at) " +
            "VALUES ($1, $2, $3, $4, NULL)";
        command.Parameters.AddWithValue(Guid.Parse(tenantId));
        command.Parameters.AddWithValue(Guid.Parse(userId));
        command.Parameters.AddWithValue(role);
        command.Parameters.AddWithValue(Clock);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Stores one operation row under a tenant scope the way the queue would: the submitting
    /// request's scope is what the row carries, so the list routes can prove they filter by it.
    /// </summary>
    private static async Task InsertOperationAsync(string connectionString, Guid operationId, string scopeId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO operations (id, scope_id, kind, target_id, input_revision, idempotency_key, " +
            "fingerprint, revision, status, attempt, created_at, updated_at, lease_recoveries) " +
            "VALUES ($1, $2, 'test-probe', $3, 1, $4, 'probe', 1, 'queued', 0, $5, $5, 0)";
        command.Parameters.AddWithValue(operationId);
        command.Parameters.AddWithValue(Guid.Parse(scopeId));
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue($"probe-{operationId:N}");
        command.Parameters.AddWithValue(Clock);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Proves one tenant's view over the shared pooled connection: exactly its own rows under
    /// its own context, and nothing of the other tenant's.
    /// </summary>
    private static async Task AssertTenantSeesOnlyAsync(
        NpgsqlConnection connection,
        string database,
        string tenant,
        string other)
    {
        var otherScope = await ReadTenantScopeAsync(database, other);

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = connection.CreateCommand())
        {
            context.Transaction = transaction;
            context.CommandText = $"SET LOCAL app.tenant_id = '{tenant}'";
            await context.ExecuteNonQueryAsync();
        }

        var seenTenants = await QueryIdsAsync(connection, transaction, "SELECT id::text FROM tenants");
        var seenMemberships = await QueryIdsAsync(connection, transaction, "SELECT tenant_id::text FROM tenant_memberships");
        var seenScopes = await QueryIdsAsync(connection, transaction, "SELECT tenant_id::text FROM tenant_scopes");
        var seenScopeIds = await QueryIdsAsync(connection, transaction, "SELECT id::text FROM tenant_scopes");

        Assert.Equal(tenant, Assert.Single(seenTenants));
        Assert.Equal(tenant, Assert.Single(seenMemberships));
        Assert.Equal(tenant, Assert.Single(seenScopes));
        Assert.DoesNotContain(otherScope, seenScopeIds);

        await transaction.RollbackAsync();
    }

    private static async Task<List<string>> QueryIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadFieldAsync(HttpResponseMessage response, string field)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(field).GetString()!;
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<string?> ReadFieldErrorAsync(HttpResponseMessage response, string field)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("fieldErrors").GetProperty(field)[0].GetString();
    }
}

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application in Hosted mode against a real disposable
/// PostgreSQL, with the OIDC issuer standing in as scripted wire bytes.
/// </summary>
/// <remarks>
/// The connection string, the public origin, the tenancy mode and the issuer arrive through the
/// same configuration keys production reads, and the clock is the same fixed instant the
/// assertions use. The <see cref="IOidcTransport"/> singleton is the one production registration
/// a test replaces - over a <see cref="FakeOidcHandler"/> that answers only the three OIDC
/// resources - so the discovery read, the code exchange, the key-set read, the exact issuer and
/// audience comparison and the signature verification are the production code path.
/// </remarks>
public sealed class CloudTenantBoundaryApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly FakeOidcHandler _issuer;
    private readonly string? _keyFile;
    private readonly string? _mediaRoot;

    private CloudTenantBoundaryApiHost(
        string connectionString,
        TimeProvider clock,
        FakeOidcHandler issuer,
        string? keyFile,
        string? mediaRoot)
    {
        _connectionString = connectionString;
        _clock = clock;
        _issuer = issuer;
        _keyFile = keyFile;
        _mediaRoot = mediaRoot;
    }

    /// <summary>Starts a Hosted host whose OIDC issuer answers from <paramref name="issuer"/>.</summary>
    public static CloudTenantBoundaryApiHost Start(
        string connectionString,
        DateTimeOffset clock,
        FakeOidcHandler issuer,
        string? keyFile = null,
        string? mediaRoot = null) =>
        new(connectionString, new FixedTimeProvider(clock), issuer, keyFile, mediaRoot);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseSetting("Chronicleaf:Tenancy:Mode", "Hosted");
        builder.UseSetting("Chronicleaf:Tenancy:OidcIssuer", FakeOidcHandler.Issuer);
        builder.UseSetting("Chronicleaf:Tenancy:OidcClientId", FakeOidcHandler.ClientId);

        if (_keyFile is not null)
        {
            builder.UseSetting(SecretVaultSettings.EncryptionKeyFileSettingName, _keyFile);
        }

        if (_mediaRoot is not null)
        {
            builder.UseSetting("Chronicleaf:MediaRoot", _mediaRoot);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);

            // The production transport replaced whole, over the fake issuer: the only thing a
            // double stands in for is the issuer itself. The discovery read, the code exchange,
            // the key-set read and every check above the socket are the production code path.
            services.RemoveAll<IOidcTransport>();
            services.AddSingleton<IOidcTransport>(_ => new OidcHttpTransport(
                new HttpClient(_issuer, disposeHandler: false)
                {
                    Timeout = OidcHttpTransport.Timeout,
                }));
        });
    }
}

/// <summary>
/// A real <see cref="HttpMessageHandler"/> that answers the three OIDC resources from scripted
/// bytes: the discovery document, the RSA key set and the token endpoint.
/// </summary>
/// <remarks>
/// The handler mints real RS256 ID tokens with its own generated key and serves the matching
/// key set, so the production validator verifies a genuine signature against a genuine key
/// document. Failure injection - a wrong nonce, a foreign issuer, an expired token, a key
/// outside the served set - works by minting a token the production checks then refuse, which
/// is what proves the checks run rather than the handler.
/// </remarks>
public sealed class FakeOidcHandler : HttpMessageHandler
{
    /// <summary>The issuer every scenario configures: loopback, so the transport dials nothing real.</summary>
    public const string Issuer = "http://127.0.0.1:7";

    /// <summary>This site's client id at the fake issuer.</summary>
    public const string ClientId = "chronicleaf-hosted-test";

    private const string KeyId = "test-key";

    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _wrongKey = RSA.Create(2048);
    private readonly DateTimeOffset _clock;

    /// <summary>The subject the next minted token carries.</summary>
    public string Subject { get; set; } = "user-1";

    /// <summary>The nonce the authorization request carried, learned from the start response.</summary>
    public string? PresentedNonce { get; set; }

    /// <summary>When set, the minted token carries this nonce instead of the presented one.</summary>
    public string? NonceOverride { get; set; }

    /// <summary>When set, the minted token names this issuer instead of the configured one.</summary>
    public string? IssuerOverride { get; set; }

    /// <summary>When set, the minted token expires at this instant instead of an hour out.</summary>
    public DateTimeOffset? ExpiresAtOverride { get; set; }

    /// <summary>When set, the token is signed by a key outside the served key set.</summary>
    public bool UseWrongKey { get; set; }

    public FakeOidcHandler(DateTimeOffset clock)
    {
        _clock = clock;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (request.Method == HttpMethod.Get && path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            return Json(new
            {
                issuer = Issuer,
                token_endpoint = Issuer + "/token",
                jwks_uri = Issuer + "/jwks",
            });
        }

        if (request.Method == HttpMethod.Get && path.EndsWith("/jwks", StringComparison.Ordinal))
        {
            var parameters = _key.ExportParameters(false);
            return Json(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        kid = KeyId,
                        use = "sig",
                        alg = "RS256",
                        n = Base64Url(parameters.Modulus!),
                        e = Base64Url(parameters.Exponent!),
                    },
                },
            });
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/token", StringComparison.Ordinal))
        {
            _ = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Json(new { id_token = MintIdToken() });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private string MintIdToken()
    {
        var header = Base64Url("""{"alg":"RS256","kid":"test-key","typ":"JWT"}""");
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["iss"] = IssuerOverride ?? Issuer,
            ["sub"] = Subject,
            ["aud"] = ClientId,
            ["exp"] = (ExpiresAtOverride ?? _clock.AddHours(1)).ToUnixTimeSeconds(),
            ["iat"] = _clock.ToUnixTimeSeconds(),
            ["nonce"] = NonceOverride ?? PresentedNonce,
        });
        var signingInput = header + "." + Base64Url(payload);
        var key = UseWrongKey ? _wrongKey : _key;
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"),
        };

    private static string Base64Url(string value) => Base64Url(Encoding.UTF8.GetBytes(value));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _key.Dispose();
            _wrongKey.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// An authenticated Hosted user, driving the real routes the way the browser would: the OIDC
/// start and callback for real, then a real session cookie, the real origin and a real
/// antiforgery value, requested per mutation exactly as the delivered sign-out does it.
/// </summary>
internal sealed class HostedClient : IDisposable
{
    private const string CookieName = "chronicleaf_session";
    private const string AntiforgeryHeader = "X-Chronicleaf-Antiforgery";

    private readonly HttpClient _client;
    private readonly string _cookie;

    private HostedClient(HttpClient client, string cookie)
    {
        _client = client;
        _cookie = cookie;
    }

    /// <summary>Runs the verified login against the fake issuer and returns the session client.</summary>
    public static async Task<HostedClient> LoginAsync(WebApplicationFactory<Program> host, FakeOidcHandler oidc)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(oidc);

        var client = host.CreateClient();

        using var start = await client.PostAsJsonAsync("/api/hosted/login/start", new { });
        start.EnsureSuccessStatusCode();
        string state;
        using (var document = JsonDocument.Parse(await start.Content.ReadAsStringAsync()))
        {
            state = document.RootElement.GetProperty("state").GetString()!;
            oidc.PresentedNonce = HostedLoginNonce(document.RootElement.GetProperty("authorizationUrl").GetString()!);
        }

        using var callback = await client.PostAsJsonAsync(
            "/api/hosted/login/callback",
            new { code = $"test-code-{Guid.NewGuid():N}", state });
        callback.EnsureSuccessStatusCode();

        var cookie = callback.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=', 2)[1];

        return new HostedClient(client, cookie);
    }

    private static string HostedLoginNonce(string authorizationUrl)
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

    /// <summary>Sends a request built by <see cref="Post"/>, <see cref="Put"/> or <see cref="Delete"/>.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _client.SendAsync(request);

    /// <summary>A mutation with no revision, used for the tenant actions that do not take one.</summary>
    public Task<HttpRequestMessage> Post(string path, object? body) => Build(HttpMethod.Post, path, body);

    /// <summary>
    /// A mutation that replaces a stored value in full, such as a sealed provider credential.
    /// </summary>
    public Task<HttpRequestMessage> Put(string path, object? body, long? revision = null) =>
        Build(HttpMethod.Put, path, body, revision);

    /// <summary>A membership revocation, which takes no body and no revision.</summary>
    public Task<HttpRequestMessage> Delete(string path) => Build(HttpMethod.Delete, path, null);

    /// <summary>A mutation whose body is raw bytes with its own content type, rather than JSON.</summary>
    public async Task<HttpRequestMessage> PostBytes(string path, byte[] body, string contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(body),
        };

        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        request.Headers.Add("Cookie", $"{CookieName}={_cookie}");
        request.Headers.TryAddWithoutValidation("Origin", OwnerClient.PublicOrigin);
        request.Headers.Add(AntiforgeryHeader, await ReadAntiforgeryAsync());

        return request;
    }

    public void Dispose() => _client.Dispose();

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
            request.Headers.TryAddWithoutValidation("If-Match", expected.ToString(CultureInfo.InvariantCulture));
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
