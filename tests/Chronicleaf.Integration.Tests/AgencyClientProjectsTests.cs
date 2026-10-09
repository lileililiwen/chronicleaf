using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// AGENCY-CLIENT-PROJECTS-001 and AGENCY-CLIENT-PROJECTS-002 against a real disposable PostgreSQL
/// started from a pinned Docker image, through the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// Nothing here is simulated. Clients, the project association, revisions, the foreign key, the
/// archived stamp and the audit rows are all produced by a real PostgreSQL server, and every claim
/// is read back over the wire - a status code, a header, a body - or over a raw connection as a row
/// or a column. There is no EF in-memory provider and no stubbed store: an unavailable container is
/// a failure, never a passing result.
/// <para>
/// The clock is injected and frozen, so every stored instant is deterministic. The identities that
/// have to be stable are constants: the installation and its scope, and the foreign scope a client
/// outside the caller's reach is stored under. The ids the service generates are read back from the
/// responses rather than assumed.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class AgencyClientProjectsTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The operator's setup token for these hosts, and the hash it is configured with.</summary>
    private const string AccessToken = "chronicleaf-operator-setup-token-0123456789abcdef";

    private static readonly string AccessTokenHash = Sha256.Sha256Hex(AccessToken);

    /// <summary>The single origin this installation accepts for mutations.</summary>
    private const string PublicOrigin = "https://chronicleaf.example";

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("9a3f1c5d-7b28-4e6a-8c19-4d5e6f708193");

    /// <summary>A second scope, used only to prove a record outside the caller's scope is not found.</summary>
    private static readonly Guid ForeignScopeId = new("b84e2d6e-1839-4f7b-9d20-5e6f70819404");

    // ===========================================================================================
    // AGENCY-CLIENT-PROJECTS-001 Success: two projects assigned to Client A read back under it,
    // while a third project stays personal.
    // ===========================================================================================

    [Fact]
    public async Task Owner_assigns_two_projects_to_a_client_and_the_hierarchy_lists_them()
    {
        Assert.True(postgres.IsRealPostgres, "the agency scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("agency_hierarchy");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        using var clientA = await CreateClientAsync(owner, "Client A", "First agency client.");
        var clientAId = await ReadIdAsync(clientA);
        Assert.Equal(1, await ReadRevisionAsync(clientA));
        Assert.Equal("\"1\"", clientA.Headers.ETag?.ToString());

        using var first = await CreateProjectAsync(owner, "Ledger");
        using var second = await CreateProjectAsync(owner, "Atlas");
        using var personal = await CreateProjectAsync(owner, "Sandbox");

        var firstId = await ReadIdAsync(first);
        var secondId = await ReadIdAsync(second);
        var personalId = await ReadIdAsync(personal);

        using var assignFirst = await owner.SendAsync(
            await owner.Patch($"/api/projects/{firstId}/client", 1, new { clientId = clientAId }));
        Assert.Equal(HttpStatusCode.OK, assignFirst.StatusCode);
        Assert.Equal(2, await ReadRevisionAsync(assignFirst));

        using var assignSecond = await owner.SendAsync(
            await owner.Patch($"/api/projects/{secondId}/client", 1, new { clientId = clientAId }));
        Assert.Equal(HttpStatusCode.OK, assignSecond.StatusCode);

        // The hierarchy lists exactly the two grouped projects, ordered by name.
        using var hierarchy = await owner.GetAsync($"/api/clients/{clientAId}/projects");
        Assert.Equal(HttpStatusCode.OK, hierarchy.StatusCode);
        using (var document = JsonDocument.Parse(await hierarchy.Content.ReadAsStringAsync()))
        {
            var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.Equal("Atlas", items[0].GetProperty("name").GetString());
            Assert.Equal("Ledger", items[1].GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nextCursor").ValueKind);
        }

        // The project read carries its client, and a project edit leaves the grouping alone.
        Assert.Equal(clientAId, await ReadClientIdAsync(firstId, owner));
        using var rename = await owner.SendAsync(
            await owner.Patch($"/api/projects/{firstId}", 2, new { name = "Ledger v2" }));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal(clientAId, await ReadClientIdAsync(firstId, owner));

        // The personal project is grouped nowhere and appears in no hierarchy.
        Assert.Null(await ReadClientIdAsync(personalId, owner));
        using var empty = await owner.GetAsync($"/api/clients/{clientAId}/context?projectIds={personalId}");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        using (var document = JsonDocument.Parse(await empty.Content.ReadAsStringAsync()))
        {
            Assert.Empty(document.RootElement.GetProperty("projects").EnumerateArray().ToArray());
        }
    }

    // ===========================================================================================
    // AGENCY-CLIENT-PROJECTS-001 Failure: a foreign-scope client is rejected without exposure.
    // ===========================================================================================

    [Fact]
    public async Task Foreign_scope_client_assignment_is_rejected_without_exposing_the_client()
    {
        Assert.True(postgres.IsRealPostgres, "the agency scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("agency_foreign");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var foreignClientId = Guid.NewGuid();
        await InsertForeignClientAsync(database, foreignClientId);

        using var project = await CreateProjectAsync(owner, "Ledger");
        var projectId = await ReadIdAsync(project);

        using var refused = await owner.SendAsync(
            await owner.Patch($"/api/projects/{projectId}/client", 1, new { clientId = foreignClientId.ToString("D") }));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("not_found", await ReadCodeAsync(refused));

        // An invented id is the same answer, so neither response says which ids exist elsewhere.
        using var unknown = await owner.SendAsync(
            await owner.Patch(
                $"/api/projects/{projectId}/client",
                1,
                new { clientId = Guid.NewGuid().ToString("D") }));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("not_found", await ReadCodeAsync(unknown));

        // The refused assignment wrote nothing: the project is still personal at revision 1.
        Assert.Null(await ReadClientIdAsync(projectId, owner));
        using var reread = await owner.GetAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
        Assert.Equal(1, await ReadRevisionAsync(reread));
    }

    // ===========================================================================================
    // AGENCY-CLIENT-PROJECTS-001 Boundary: no client means personal, and the client lists page.
    // ===========================================================================================

    [Fact]
    public async Task Project_without_client_remains_personal_and_client_lists_page_by_name()
    {
        Assert.True(postgres.IsRealPostgres, "the agency scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("agency_personal");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        using var clientB = await CreateClientAsync(owner, "Client B", string.Empty);
        using var clientASeed = await CreateClientAsync(owner, "Client A", string.Empty);
        using var clientC = await CreateClientAsync(owner, "Client C", string.Empty);

        using var page = await owner.GetAsync("/api/clients?limit=2");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        using (var document = JsonDocument.Parse(await page.Content.ReadAsStringAsync()))
        {
            var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.Equal("Client A", items[0].GetProperty("name").GetString());
            Assert.Equal("Client B", items[1].GetProperty("name").GetString());
            var cursor = document.RootElement.GetProperty("nextCursor").GetString();
            Assert.False(string.IsNullOrWhiteSpace(cursor));

            using var rest = await owner.GetAsync($"/api/clients?limit=2&cursor={Uri.EscapeDataString(cursor)}");
            Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
            using var restDocument = JsonDocument.Parse(await rest.Content.ReadAsStringAsync());
            var restItems = restDocument.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Single(restItems);
            Assert.Equal("Client C", restItems[0].GetProperty("name").GetString());
        }

        using var sandbox = await CreateProjectAsync(owner, "Sandbox");
        var sandboxId = await ReadIdAsync(sandbox);
        Assert.Null(await ReadClientIdAsync(sandboxId, owner));
    }

    // ===========================================================================================
    // AGENCY-CLIENT-PROJECTS-002 Success: a move follows Client B and invalidates both contexts.
    // ===========================================================================================

    [Fact]
    public async Task Project_moves_between_clients_and_report_context_follows_with_invalidation()
    {
        Assert.True(postgres.IsRealPostgres, "the agency scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("agency_reassign");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientAId = await CreateClientIdAsync(owner, "Client A", string.Empty);
        var clientBId = await CreateClientIdAsync(owner, "Client B", string.Empty);

        var firstId = await CreateProjectIdAsync(owner, "Ledger");
        var secondId = await CreateProjectIdAsync(owner, "Atlas");
        var thirdId = await CreateProjectIdAsync(owner, "Beacon");

        await AssignAsync(owner, firstId, 1, clientAId);
        await AssignAsync(owner, secondId, 1, clientAId);
        await AssignAsync(owner, thirdId, 1, clientBId);

        var contextABefore = await ReadContextRevisionAsync(owner, clientAId, firstId, secondId);
        var contextBBefore = await ReadContextRevisionAsync(owner, clientBId, thirdId);
        var clientABefore = await ReadClientRevisionAsync(owner, clientAId);
        var clientBBefore = await ReadClientRevisionAsync(owner, clientBId);

        // The move presents the project's own revision, which the first assignment advanced to 2.
        var movedRevision = await AssignAsync(owner, firstId, 2, clientBId);
        Assert.Equal(3, movedRevision);

        // New report context follows Client B: both members are depicted under its generation.
        using var contextB = await owner.GetAsync($"/api/clients/{clientBId}/context?projectIds={firstId},{thirdId}");
        Assert.Equal(HttpStatusCode.OK, contextB.StatusCode);
        using (var document = JsonDocument.Parse(await contextB.Content.ReadAsStringAsync()))
        {
            var depicted = document.RootElement.GetProperty("projects").EnumerateArray().ToArray();
            Assert.Equal(2, depicted.Length);
            Assert.Equal(contextBBefore + 1, document.RootElement.GetProperty("contextRevision").GetInt64());
        }

        // The old client's context no longer depicts the moved project, under its own generation.
        using var contextA = await owner.GetAsync($"/api/clients/{clientAId}/context?projectIds={firstId},{secondId}");
        Assert.Equal(HttpStatusCode.OK, contextA.StatusCode);
        using (var document = JsonDocument.Parse(await contextA.Content.ReadAsStringAsync()))
        {
            var depicted = document.RootElement.GetProperty("projects").EnumerateArray().ToArray();
            Assert.Single(depicted);
            Assert.Equal(secondId, depicted[0].GetProperty("projectId").GetString());
            Assert.Equal(contextABefore + 1, document.RootElement.GetProperty("contextRevision").GetInt64());
        }

        // The membership generations moved; the client edit revisions did not.
        Assert.Equal(contextABefore + 1, await ReadContextRevisionAsync(owner, clientAId, secondId));
        Assert.Equal(contextBBefore + 1, await ReadContextRevisionAsync(owner, clientBId, thirdId));
        Assert.Equal(clientABefore, await ReadClientRevisionAsync(owner, clientAId));
        Assert.Equal(clientBBefore, await ReadClientRevisionAsync(owner, clientBId));

        // A report that pinned the old generation is recognizably stale: the stored generation moved.
        Assert.NotEqual(contextABefore, await ReadContextRevisionAsync(owner, clientAId, secondId));
    }

    // ===========================================================================================
    // AGENCY-CLIENT-PROJECTS-002 Failure: an archived client refuses new work but keeps history.
    // ===========================================================================================

    [Fact]
    public async Task Archived_client_refuses_new_assignments_but_keeps_its_historical_tree()
    {
        Assert.True(postgres.IsRealPostgres, "the agency scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("agency_archive");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientAId = await CreateClientIdAsync(owner, "Client A", string.Empty);
        var clientBId = await CreateClientIdAsync(owner, "Client B", string.Empty);

        var memberId = await CreateProjectIdAsync(owner, "Ledger");
        var newcomerId = await CreateProjectIdAsync(owner, "Atlas");
        await AssignAsync(owner, memberId, 1, clientAId);

        using var archived = await owner.SendAsync(await owner.Archive($"/api/clients/{clientAId}", 1));
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(2, await ReadRevisionAsync(archived));

        // A new assignment to the archived client is refused, and the refusal names the condition.
        using var refused = await owner.SendAsync(
            await owner.Patch($"/api/projects/{newcomerId}/client", 1, new { clientId = clientAId }));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("client_archived", await ReadCodeAsync(refused));
        Assert.Null(await ReadClientIdAsync(newcomerId, owner));

        // The historical tree is preserved: the archived client still lists its member.
        using var tree = await owner.GetAsync($"/api/clients/{clientAId}/projects");
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        using (var document = JsonDocument.Parse(await tree.Content.ReadAsStringAsync()))
        {
            var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Single(items);
            Assert.Equal(memberId, items[0].GetProperty("id").GetString());
        }

        // A project is never stranded: moving away from (or clearing) an archived client is allowed.
        var movedRevision = await AssignAsync(owner, memberId, 2, clientBId);
        Assert.Equal(3, movedRevision);
        Assert.Equal(clientBId, await ReadClientIdAsync(memberId, owner));

        var clearedRevision = await ClearAsync(owner, memberId, movedRevision);
        Assert.Equal(4, clearedRevision);
        Assert.Null(await ReadClientIdAsync(memberId, owner));

        // The archived client itself is read-only: an edit is the same conflict as an assignment.
        using var editRefused = await owner.SendAsync(
            await owner.Patch($"/api/clients/{clientAId}", 2, new { name = "Client A renamed" }));
        Assert.Equal(HttpStatusCode.Conflict, editRefused.StatusCode);
        Assert.Equal("client_archived", await ReadCodeAsync(editRefused));
    }

    // ===========================================================================================
    // AGENCY-CLIENT-PROJECTS-002 Boundary: a context depicts exactly this client's projects.
    // ===========================================================================================

    [Fact]
    public async Task Report_context_excludes_unrelated_client_projects_and_unknown_ids()
    {
        Assert.True(postgres.IsRealPostgres, "the agency scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("agency_context_boundary");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientAId = await CreateClientIdAsync(owner, "Client A", string.Empty);
        var clientBId = await CreateClientIdAsync(owner, "Client B", string.Empty);

        var ownId = await CreateProjectIdAsync(owner, "Ledger");
        var otherId = await CreateProjectIdAsync(owner, "Atlas");
        var homelessId = await CreateProjectIdAsync(owner, "Sandbox");
        await AssignAsync(owner, ownId, 1, clientAId);
        await AssignAsync(owner, otherId, 1, clientBId);

        var unknownId = Guid.NewGuid().ToString("D");
        using var context = await owner.GetAsync(
            $"/api/clients/{clientAId}/context?projectIds={ownId},{otherId},{homelessId},{unknownId}");
        Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        using (var document = JsonDocument.Parse(await context.Content.ReadAsStringAsync()))
        {
            Assert.Equal(clientAId, document.RootElement.GetProperty("clientId").GetString());
            var depicted = document.RootElement.GetProperty("projects").EnumerateArray().ToArray();
            var depictedIds = depicted.Select(item => item.GetProperty("projectId").GetString()!).ToArray();
            Assert.Equal([ownId], depictedIds);
        }
    }

    // ===========================================================================================
    // Revisions, validation and the migration itself.
    // ===========================================================================================

    [Fact]
    public async Task Duplicate_assignment_is_a_no_op_and_stale_or_missing_revisions_are_refused()
    {
        Assert.True(postgres.IsRealPostgres, "the agency scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("agency_revisions");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientId = await CreateClientIdAsync(owner, "Client A", string.Empty);
        var projectId = await CreateProjectIdAsync(owner, "Ledger");
        await AssignAsync(owner, projectId, 1, clientId);

        var contextBefore = await ReadContextRevisionAsync(owner, clientId, projectId);

        // Repeating the stored association at the stored revision writes nothing.
        using var repeated = await owner.SendAsync(
            await owner.Patch($"/api/projects/{projectId}/client", 2, new { clientId }));
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(2, await ReadRevisionAsync(repeated));
        Assert.Equal(contextBefore, await ReadContextRevisionAsync(owner, clientId, projectId));

        // A stale project revision loses, on the assignment route as on every other edit.
        using var stale = await owner.SendAsync(
            await owner.Patch($"/api/projects/{projectId}/client", 1, new { clientId }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("revision_conflict", await ReadCodeAsync(stale));

        // A missing revision is 428, whether the body carries one or not.
        using var missing = await owner.SendAsync(
            await owner.Patch($"/api/projects/{projectId}/client", null, new { clientId }));
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);

        // A stale client revision loses, and a missing one is 428.
        using var staleClient = await owner.SendAsync(
            await owner.Patch($"/api/clients/{clientId}", 2, new { name = "Client A renamed" }));
        Assert.Equal(HttpStatusCode.Conflict, staleClient.StatusCode);
        Assert.Equal("revision_conflict", await ReadCodeAsync(staleClient));

        using var missingClient = await owner.SendAsync(
            await owner.Patch($"/api/clients/{clientId}", null, new { name = "Client A renamed" }));
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingClient.StatusCode);
    }

    [Fact]
    public async Task Client_and_assignment_validation_names_the_offending_field()
    {
        Assert.True(postgres.IsRealPostgres, "the agency scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("agency_validation");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        using var empty = await owner.SendAsync(
            await owner.Post("/api/clients", new { name = "   ", notes = "" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.NotNull(await ReadFieldErrorAsync(empty, "name"));

        using var tooLong = await owner.SendAsync(
            await owner.Post("/api/clients", new { name = new string('n', 161), notes = "" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.NotNull(await ReadFieldErrorAsync(tooLong, "name"));

        using var notesTooLong = await owner.SendAsync(
            await owner.Post("/api/clients", new { name = "Client A", notes = new string('n', 20001) }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notesTooLong.StatusCode);
        Assert.NotNull(await ReadFieldErrorAsync(notesTooLong, "notes"));

        var projectId = await CreateProjectIdAsync(owner, "Ledger");

        using var missingKey = await owner.SendAsync(
            await owner.Patch($"/api/projects/{projectId}/client", 1, new { }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingKey.StatusCode);
        Assert.NotNull(await ReadFieldErrorAsync(missingKey, "clientId"));

        using var malformed = await owner.SendAsync(
            await owner.Patch($"/api/projects/{projectId}/client", 1, new { clientId = "not-a-client" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
        Assert.NotNull(await ReadFieldErrorAsync(malformed, "clientId"));

        var clientId = await CreateClientIdAsync(owner, "Client A", string.Empty);
        using var malformedContext = await owner.GetAsync(
            $"/api/clients/{clientId}/context?projectIds=not-a-project");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformedContext.StatusCode);

        // Unknown ids on either side are the same 404 that hides other scopes.
        using var unknownClient = await owner.GetAsync($"/api/clients/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, unknownClient.StatusCode);

        using var unknownProject = await owner.SendAsync(
            await owner.Patch(
                $"/api/projects/{Guid.NewGuid():D}/client",
                1,
                new { clientId }));
        Assert.Equal(HttpStatusCode.NotFound, unknownProject.StatusCode);
    }

    [Fact]
    public async Task Private_client_routes_require_a_session()
    {
        Assert.True(postgres.IsRealPostgres, "the agency scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("agency_sessionless");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var anonymous = host.CreateClient();

        using var response = await anonymous.GetAsync("/api/clients");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_client_table_and_association_come_from_the_forward_migration_and_readiness_requires_it()
    {
        var database = await postgres.CreateDatabaseAsync("agency_migration");

        // Readiness requires the agency migration by id, and it is the newest required one by
        // position rather than by name - a named "newest" is a value somebody has to remember to
        // update, which is exactly what made the older scenarios in this repository go red.
        Assert.Contains(ChronicleafSchema.AgencyClientProjectsMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));

        // A database stopped at the public-project-journey schema is short of exactly this one.
        var missing = ChronicleafSchema.MissingMigrationIds(
            ChronicleafSchema.RequiredMigrationIds
                .Where(id => id != ChronicleafSchema.AgencyClientProjectsMigrationId)
                .ToArray());
        Assert.Equal([ChronicleafSchema.AgencyClientProjectsMigrationId], missing);

        // The columns this package owns exist with the bounds this package enforces.
        Assert.Equal(
            "160",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT character_maximum_length::text FROM information_schema.columns " +
                "WHERE table_name = 'clients' AND column_name = 'name'"));
        Assert.Equal(
            "20000",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT character_maximum_length::text FROM information_schema.columns " +
                "WHERE table_name = 'clients' AND column_name = 'notes'"));
        Assert.Equal(
            "uuid",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT data_type FROM information_schema.columns " +
                "WHERE table_name = 'projects' AND column_name = 'client_id'"));
        Assert.Equal(
            "YES",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT is_nullable FROM information_schema.columns " +
                "WHERE table_name = 'projects' AND column_name = 'client_id'"));

        // The association is a real foreign key, enforced by the database rather than by a caller
        // being careful, with the conventional index beside it.
        Assert.Equal(
            "fk_projects_client_id",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT conname FROM pg_constraint WHERE conname = 'fk_projects_client_id'"));
        Assert.Equal(
            "IX_projects_client_id",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT indexname FROM pg_indexes WHERE tablename = 'projects' " +
                "AND indexname = 'IX_projects_client_id'"));
        Assert.Equal(
            "ix_clients_scope_id_name",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT indexname FROM pg_indexes WHERE tablename = 'clients' " +
                "AND indexname = 'ix_clients_scope_id_name'"));
    }

    // ===========================================================================================
    // Fixture helpers. Every id under test is read back from a response; only the foreign-scope
    // client - which no route in this scope could ever create - is written by hand.
    // ===========================================================================================

    private async Task<string> ProvisionedDatabaseAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);

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

    private static async Task<HttpResponseMessage> CreateClientAsync(OwnerClient owner, string name, string notes)
    {
        var response = await owner.SendAsync(await owner.Post("/api/clients", new { name, notes }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return response;
    }

    private static async Task<string> CreateClientIdAsync(OwnerClient owner, string name, string notes)
    {
        using var response = await CreateClientAsync(owner, name, notes);
        return await ReadIdAsync(response);
    }

    private static async Task<HttpResponseMessage> CreateProjectAsync(OwnerClient owner, string name)
    {
        var response = await owner.SendAsync(
            await owner.Post("/api/projects", new { name, description = $"{name} project." }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return response;
    }

    private static async Task<string> CreateProjectIdAsync(OwnerClient owner, string name)
    {
        using var response = await CreateProjectAsync(owner, name);
        return await ReadIdAsync(response);
    }

    private static async Task<long> AssignAsync(OwnerClient owner, string projectId, long revision, string clientId)
    {
        using var response = await owner.SendAsync(
            await owner.Patch($"/api/projects/{projectId}/client", revision, new { clientId }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadRevisionAsync(response);
    }

    private static async Task<long> ClearAsync(OwnerClient owner, string projectId, long revision)
    {
        using var response = await owner.SendAsync(
            await owner.Patch($"/api/projects/{projectId}/client", revision, new { clientId = (string?)null }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadRevisionAsync(response);
    }

    private static async Task<string?> ReadClientIdAsync(string projectId, OwnerClient owner)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadStringAsync(response, "clientId");
    }

    private static async Task<long> ReadContextRevisionAsync(OwnerClient owner, string clientId, params string[] projectIds)
    {
        var query = string.Join(",", projectIds.Select(Uri.EscapeDataString));
        using var response = await owner.GetAsync($"/api/clients/{clientId}/context?projectIds={query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("contextRevision").GetInt64();
    }

    private static async Task<long> ReadClientRevisionAsync(OwnerClient owner, string clientId)
    {
        using var response = await owner.GetAsync($"/api/clients/{clientId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadRevisionAsync(response);
    }

    /// <summary>
    /// Stores one client under the foreign scope, the way no route in the caller's scope ever
    /// could. The row exists so the assignment can prove it is refused without being exposed.
    /// </summary>
    private static async Task InsertForeignClientAsync(string connectionString, Guid clientId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO clients (id, scope_id, name, notes, revision, context_revision, archived_at) " +
            "VALUES ($1, $2, 'Foreign', '', 1, 1, NULL)";
        command.Parameters.AddWithValue(clientId);
        command.Parameters.AddWithValue(ForeignScopeId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadIdAsync(HttpResponseMessage response) =>
        (await ReadStringAsync(response, "id"))!;

    private static async Task<string?> ReadStringAsync(HttpResponseMessage response, string field)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty(field).ValueKind == JsonValueKind.Null
            ? null
            : document.RootElement.GetProperty(field).GetString();
    }

    private static async Task<long> ReadRevisionAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("revision").GetInt64();
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
