using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// PROJECTS-AND-MILESTONES-001 and PROJECTS-AND-MILESTONES-002 against a real disposable PostgreSQL
/// started from a pinned Docker image, through the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// Nothing here is simulated. Projects, milestones, revisions, the partial unique index, the archived
/// stamp and the audit rows are all produced by a real PostgreSQL server, and every claim is read
/// back over the wire - a status code, a header, a body - or over a raw connection as a row count.
/// There is no EF in-memory provider and no stubbed store: an unavailable container is a failure,
/// never a passing result.
/// <para>
/// The clock is injected and frozen, so every stored instant is deterministic. The identities that
/// have to be stable are constants: the installation and its scope, and the source keys an import is
/// idempotent on. The ids the service generates are read back from the responses rather than
/// assumed.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class ProjectsAndMilestonesTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The operator's setup token for these hosts, and the hash it is configured with.</summary>
    private const string AccessToken = "chronicleaf-operator-setup-token-0123456789abcdef";

    private static readonly string AccessTokenHash = Sha256.Sha256Hex(AccessToken);

    /// <summary>The single origin this installation accepts for mutations.</summary>
    private const string PublicOrigin = "https://chronicleaf.example";

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("6e1c8b3a-2f47-4d5e-9a10-2b3c4d5e6f71");

    /// <summary>A second scope, used only to prove a record outside the caller's scope is not found.</summary>
    private static readonly Guid ForeignScopeId = new("7f2d9c4b-3058-4e6f-8b21-3c4d5e6f7082");

    /// <summary>Deterministic source keys, so the idempotence of an import is a fixed fact.</summary>
    private const string ImportSource = "github";

    private const string FirstSourceKey = "release/v1.0.0";
    private const string RepeatedSourceKey = "release/v1.1.0";

    // ===========================================================================================
    // PROJECTS-AND-MILESTONES-001 Success: a manual milestone is confirmed the moment the owner
    // saves it, and it is listed under its project.
    // ===========================================================================================

    [Fact]
    public async Task Owner_creates_a_manual_milestone_and_it_appears_confirmed_under_its_project()
    {
        Assert.True(postgres.IsRealPostgres, "the milestone scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("milestone_success");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        // The installation starts empty. Nothing is invented for the owner to look at.
        using (var empty = await owner.GetAsync("/api/projects"))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);

            using var document = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray().ToArray());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nextCursor").ValueKind);
        }

        using var projectResponse = await owner.SendAsync(
            await owner.Post("/api/projects", new { name = "Ledger", description = "Double-entry accounting." }));

        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);

        var projectId = await ReadIdAsync(projectResponse);
        Assert.Equal($"/api/projects/{projectId}", projectResponse.Headers.Location?.ToString());
        Assert.Equal(1, await ReadRevisionAsync(projectResponse));
        Assert.Equal("\"1\"", projectResponse.Headers.ETag?.ToString());

        var occurredAt = Clock.AddDays(-3);
        using var milestoneResponse = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/milestones",
            new
            {
                title = "First import of the ledger",
                version = "v0.1.0",
                occurredAt,
                type = "feature",
                notes = "Double-entry postings reconcile from the first day.",
                knownLimitations = new[] { "No multi-currency yet." },
                nextSteps = new[] { "Add the reconciliation report." },
            }));

        Assert.Equal(HttpStatusCode.Created, milestoneResponse.StatusCode);

        var milestoneId = await ReadIdAsync(milestoneResponse);
        Assert.Equal($"/api/milestones/{milestoneId}", milestoneResponse.Headers.Location?.ToString());
        Assert.Equal(projectId, await ReadStringAsync(milestoneResponse, "projectId"));

        // The explicit save is the confirmation: a record the owner typed is stored confirmed.
        Assert.Equal(MilestoneStatus.Confirmed, await ReadStringAsync(milestoneResponse, "status"));
        Assert.Equal(MilestoneSource.Manual, await ReadStringAsync(milestoneResponse, "source"));
        Assert.Equal(MilestoneTypes.Feature, await ReadStringAsync(milestoneResponse, "type"));
        Assert.Equal(1, await ReadRevisionAsync(milestoneResponse));

        // And it is really there: listed under its project, with the instant the owner gave.
        using var list = await owner.GetAsync($"/api/projects/{projectId}/milestones");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using var listDocument = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var listed = Assert.Single(listDocument.RootElement.GetProperty("items").EnumerateArray().ToArray());

        Assert.Equal(milestoneId, listed.GetProperty("id").GetString());
        Assert.Equal(MilestoneStatus.Confirmed, listed.GetProperty("status").GetString());
        Assert.Equal(
            occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            listed.GetProperty("occurredAt").GetString());

        // The stored rows agree with the responses, read over a raw connection rather than through
        // the same context that answered the request.
        Assert.Equal(1L, await CountWhereAsync(database, $"id = '{projectId}'", "projects"));
        Assert.Equal(
            $"confirmed|manual|1",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT status || '|' || source || '|' || revision::text FROM milestones WHERE id = '{milestoneId}'"));

        // The exact known-limitation and next-step text the owner wrote survived the round trip,
        // arrays and all.
        Assert.Equal(
            "No multi-currency yet.|Add the reconciliation report.",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT known_limitations[1] || '|' || next_steps[1] FROM milestones WHERE id = '{milestoneId}'"));
    }

    // ===========================================================================================
    // PROJECTS-AND-MILESTONES-001 Failure: an imported candidate cannot be treated as confirmed
    // until the owner confirms it.
    // ===========================================================================================

    [Fact]
    public async Task An_imported_candidate_is_not_confirmed_until_the_owner_confirms_it()
    {
        var database = await ProvisionedDatabaseAsync("milestone_candidate");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var candidateId = await ImportCandidateAsync(owner, projectId, FirstSourceKey, "Released v1.0.0", "release");

        // The record that arrived is a candidate, and it says so everywhere it can be read.
        Assert.Equal(MilestoneStatus.Candidate, await ReadStatusAsync(owner, $"/api/milestones/{candidateId}"));
        Assert.Equal(MilestoneStatus.Candidate, await ReadFirstStatusAsync(owner, $"/api/projects/{projectId}/milestones"));
        Assert.Equal(
            MilestoneStatus.Candidate,
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT status FROM milestones WHERE id = '{candidateId}'"));

        // The status is not an editable field, so an edit cannot promote it. A body that carries one
        // anyway is accepted for the fields it does name and the status it claims is simply not one
        // of them, which is why the record is still a candidate afterwards.
        using var promotion = await owner.SendAsync(await owner.Patch(
            $"/api/milestones/{candidateId}",
            1,
            new { status = MilestoneStatus.Confirmed, title = "Released v1.0.0 (checked)" }));

        Assert.Equal(HttpStatusCode.OK, promotion.StatusCode);
        Assert.Equal("Released v1.0.0 (checked)", await ReadStringAsync(promotion, "title"));

        Assert.Equal(MilestoneStatus.Candidate, await ReadStatusAsync(owner, $"/api/milestones/{candidateId}"));
        Assert.Equal(
            MilestoneStatus.Candidate,
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT status FROM milestones WHERE id = '{candidateId}'"));

        // Only the owner's own confirmation moves it, and the stored record agrees afterwards.
        long confirmedRevision;
        using (var before = await owner.GetAsync($"/api/milestones/{candidateId}"))
        {
            confirmedRevision = await ReadRevisionAsync(before);
        }

        using var confirmed = await owner.SendAsync(
            await owner.Post($"/api/milestones/{candidateId}/confirm", null, confirmedRevision));

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(MilestoneStatus.Confirmed, await ReadStringAsync(confirmed, "status"));
        Assert.Equal(confirmedRevision + 1, await ReadRevisionAsync(confirmed));
        Assert.Equal(
            MilestoneStatus.Confirmed,
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT status FROM milestones WHERE id = '{candidateId}'"));
    }

    [Fact]
    public async Task An_import_that_claims_its_records_are_confirmed_still_creates_candidates()
    {
        var database = await ProvisionedDatabaseAsync("milestone_import_status");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");

        // Nothing on the wire can state a status, and a caller that sends one anyway gets a candidate:
        // the value it claimed is not a field the import reads.
        using var imported = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/milestones/import",
            new
            {
                source = ImportSource,
                candidates = new[]
                {
                    new
                    {
                        sourceKey = FirstSourceKey,
                        title = "Released v1.0.0",
                        occurredAt = Clock,
                        type = "release",
                        status = MilestoneStatus.Confirmed,
                    },
                },
            }));

        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);

        var candidateId = ReadSingleImportedId(imported);
        Assert.Equal(MilestoneStatus.Candidate, await ReadStatusAsync(owner, $"/api/milestones/{candidateId}"));
        Assert.Equal(
            MilestoneStatus.Candidate,
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT status FROM milestones WHERE id = '{candidateId}'"));
    }

    // ===========================================================================================
    // PROJECTS-AND-MILESTONES-001 Boundary: the same source candidate imported twice is one id.
    // ===========================================================================================

    [Fact]
    public async Task Importing_the_same_source_candidate_twice_yields_exactly_one_candidate_id()
    {
        var database = await ProvisionedDatabaseAsync("milestone_duplicate");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");

        var first = Assert.Single(await ImportAsync(owner, projectId, FirstSourceKey, "Released v1.0.0", "release"));
        Assert.True(first.Created);
        var candidateId = first.MilestoneId;

        // The same key a second time: the response names the record that is already there and says
        // plainly that this call created nothing.
        var second = Assert.Single(await ImportAsync(owner, projectId, FirstSourceKey, "Released v1.0.0 (re-tagged)", "release"));

        Assert.False(second.Created);
        Assert.Equal(candidateId, second.MilestoneId);
        Assert.NotEqual(HttpStatusCode.Created, second.Status);

        // One row, one id. The duplicate import did not overwrite the record either: the first title
        // is the one that is stored.
        Assert.Equal(1L, await CountWhereAsync(database, $"project_id = '{projectId}'", "milestones"));
        Assert.Equal(
            "Released v1.0.0",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT title FROM milestones WHERE id = '{candidateId}'"));

        // A batch that mixes a known key with a new one reports each honestly, and the new one is
        // created exactly once.
        var mixed = await ImportAsync(
            owner,
            projectId,
            RepeatedSourceKey,
            "Released v1.1.0",
            "release",
            FirstSourceKey);

        Assert.Equal(2, mixed.Count);
        Assert.False(Assert.Single(mixed, item => item.SourceKey == FirstSourceKey).Created);
        Assert.True(Assert.Single(mixed, item => item.SourceKey == RepeatedSourceKey).Created);
        Assert.Equal(
            candidateId,
            Assert.Single(mixed, item => item.SourceKey == FirstSourceKey).MilestoneId);
    }

    [Fact]
    public async Task A_repeated_import_of_a_key_in_two_projects_stays_two_records()
    {
        // The uniqueness is per project, not per installation: two projects may each hold the same
        // upstream key, because they are two different projects.
        var database = await ProvisionedDatabaseAsync("milestone_duplicate_scope");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var firstProject = await CreateProjectAsync(owner, "Ledger");
        var secondProject = await CreateProjectAsync(owner, "Reporting");

        var first = Assert.Single(await ImportAsync(owner, firstProject, FirstSourceKey, "T", "release"));
        var second = Assert.Single(await ImportAsync(owner, secondProject, FirstSourceKey, "T", "release"));

        Assert.True(first.Created);
        Assert.True(second.Created);
        Assert.NotEqual(first.MilestoneId, second.MilestoneId);
        Assert.Equal(2L, await ChronicleafPostgresFixture.CountRowsAsync(database, "milestones"));
    }

    // ===========================================================================================
    // PROJECTS-AND-MILESTONES-002 Success: an edit at the current revision persists and advances
    // the revision.
    // ===========================================================================================

    [Fact]
    public async Task Editing_a_current_revision_persists_the_values_and_advances_the_revision()
    {
        var database = await ProvisionedDatabaseAsync("edit_success");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var milestoneId = await CreateMilestoneAsync(owner, projectId, "First import of the ledger", "feature");

        using var edited = await owner.SendAsync(await owner.Patch(
            $"/api/milestones/{milestoneId}",
            1,
            new
            {
                title = "First import of the ledger, reconciled",
                version = "v0.2.0",
                notes = "Reconciliation now closes every account.",
                knownLimitations = new[] { "No multi-currency yet.", "No audit trail export." },
            }));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(2, await ReadRevisionAsync(edited));
        Assert.Equal("\"2\"", edited.Headers.ETag?.ToString());
        Assert.Equal("First import of the ledger, reconciled", await ReadStringAsync(edited, "title"));
        Assert.Equal("v0.2.0", await ReadStringAsync(edited, "version"));

        // The values are in the database, not just in the answer: read over a raw connection.
        Assert.Equal(
            "First import of the ledger, reconciled|v0.2.0|2",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT title || '|' || version || '|' || revision::text FROM milestones WHERE id = '{milestoneId}'"));

        Assert.Equal(
            "No audit trail export.",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT known_limitations[2] FROM milestones WHERE id = '{milestoneId}'"));

        // A second edit at the new revision also lands, so the revision really is the token.
        using var again = await owner.SendAsync(await owner.Patch(
            $"/api/milestones/{milestoneId}",
            2,
            new { notes = "Reconciliation closes every account and reports exceptions." }));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(3, await ReadRevisionAsync(again));

        // A project edit behaves the same way, and a partial edit leaves the fields it did not name
        // exactly as they were.
        using var projectEdit = await owner.SendAsync(await owner.Patch(
            $"/api/projects/{projectId}",
            1,
            new { description = "Double-entry accounting with reconciliation." }));

        Assert.Equal(HttpStatusCode.OK, projectEdit.StatusCode);
        Assert.Equal(2, await ReadRevisionAsync(projectEdit));
        Assert.Equal("Ledger", await ReadStringAsync(projectEdit, "name"));
        Assert.Equal(
            "Double-entry accounting with reconciliation.",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT description FROM projects WHERE id = '{projectId}'"));
    }

    // ===========================================================================================
    // PROJECTS-AND-MILESTONES-002 Failure: a stale revision is a 409 and the newer edit survives.
    // ===========================================================================================

    [Fact]
    public async Task A_stale_revision_is_refused_and_the_newer_edit_is_not_lost()
    {
        var database = await ProvisionedDatabaseAsync("edit_stale");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var milestoneId = await CreateMilestoneAsync(owner, projectId, "First import of the ledger", "feature");

        // One edit wins.
        using var winning = await owner.SendAsync(await owner.Patch(
            $"/api/milestones/{milestoneId}",
            1,
            new { title = "The title a second browser wrote" }));

        Assert.Equal(HttpStatusCode.OK, winning.StatusCode);
        Assert.Equal(2, await ReadRevisionAsync(winning));

        // The other browser is still holding revision 1. It is told the truth, and its own values
        // are discarded rather than merged over the newer edit.
        using var stale = await owner.SendAsync(await owner.Patch(
            $"/api/milestones/{milestoneId}",
            1,
            new { title = "The title a stale browser tried to write" }));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ApiError.RevisionConflict, await ReadCodeAsync(stale));
        Assert.Equal("the stored revision is 2", await ReadFieldErrorAsync(stale, "If-Match"));

        Assert.Equal(
            "The title a second browser wrote",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT title FROM milestones WHERE id = '{milestoneId}'"));

        Assert.Equal(
            "2",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT revision::text FROM milestones WHERE id = '{milestoneId}'"));
    }

    [Fact]
    public async Task An_edit_without_a_revision_is_refused_before_anything_is_written()
    {
        var database = await ProvisionedDatabaseAsync("edit_no_if_match");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var milestoneId = await CreateMilestoneAsync(owner, projectId, "First import of the ledger", "feature");

        using var refused = await owner.SendAsync(
            await owner.Patch($"/api/milestones/{milestoneId}", null, new { title = "No revision offered" }));

        Assert.Equal((HttpStatusCode)428, refused.StatusCode);
        Assert.Equal(ApiError.IfMatchRequired, await ReadCodeAsync(refused));
        Assert.Equal(
            "First import of the ledger",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT title FROM milestones WHERE id = '{milestoneId}'"));

        // A revision that is not a number is a 422 naming the header, not a silent "no revision".
        using var malformed = await owner.SendAsync(
            await owner.Patch(
                $"/api/milestones/{milestoneId}",
                null,
                new { title = "No revision offered" },
                ifMatch: "not-a-revision"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
        Assert.Equal(ApiError.ValidationFailed, await ReadCodeAsync(malformed));
        Assert.Contains("If-Match", await malformed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The same rule holds for the owner-only status actions.
        using var action = await owner.SendAsync(
            await owner.Post($"/api/milestones/{milestoneId}/dismiss", null, null));

        Assert.Equal((HttpStatusCode)428, action.StatusCode);
        Assert.Equal(1L, await CountWhereAsync(database, $"id = '{milestoneId}'", "milestones"));
    }

    // ===========================================================================================
    // PROJECTS-AND-MILESTONES-002 concurrency: two edits of the same revision, one winner.
    // ===========================================================================================

    [Fact]
    public async Task Two_concurrent_edits_of_the_same_revision_produce_exactly_one_winner()
    {
        var database = await ProvisionedDatabaseAsync("edit_concurrent");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var milestoneId = await CreateMilestoneAsync(owner, projectId, "First import of the ledger", "feature");

        // Two clients, both holding revision 1, writing in the same instant. Nothing coordinates them
        // and neither is favoured: the database decides, on the revision each of them presented.
        var sharedAntiforgery = await owner.ReadAntiforgeryAsync();
        var first = await owner.Patch($"/api/milestones/{milestoneId}", 1, new { title = "Concurrent edit A" }, antiforgery: sharedAntiforgery);
        var second = await owner.Patch($"/api/milestones/{milestoneId}", 1, new { title = "Concurrent edit B" }, antiforgery: sharedAntiforgery);

        var responses = await Task.WhenAll(owner.SendAsync(first), owner.SendAsync(second));

        try
        {
            var statuses = string.Join(", ", responses.Select(response => (int)response.StatusCode));

            Assert.True(
                responses.Count(response => response.StatusCode == HttpStatusCode.OK) == 1,
                $"exactly one concurrent edit may be accepted; statuses were {statuses}");

            Assert.True(
                responses.Count(response => response.StatusCode == HttpStatusCode.Conflict) == 1,
                $"the losing concurrent edit must be a 409; statuses were {statuses}");

            // The winner's values are the ones stored, and the revision advanced exactly once.
            var storedTitle = await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT title FROM milestones WHERE id = '{milestoneId}'");

            Assert.Contains(storedTitle, new[] { "Concurrent edit A", "Concurrent edit B" });

            Assert.Equal(
                "2",
                await ChronicleafPostgresFixture.ReadTextAsync(
                    database,
                    $"SELECT revision::text FROM milestones WHERE id = '{milestoneId}'"));

            // One row, still: nothing was duplicated by the losing attempt.
            Assert.Equal(1L, await CountWhereAsync(database, $"id = '{milestoneId}'", "milestones"));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    // ===========================================================================================
    // PROJECTS-AND-MILESTONES-002 Boundary: an archived project is read-only, and its history is
    // still there.
    // ===========================================================================================

    [Fact]
    public async Task An_archived_project_is_read_only_and_its_history_is_preserved()
    {
        var database = await ProvisionedDatabaseAsync("archived");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var confirmedId = await CreateMilestoneAsync(owner, projectId, "Confirmed milestone", "feature");
        var candidateId = await ImportCandidateAsync(owner, projectId, FirstSourceKey, "Imported candidate", "release");

        // Archiving is a soft archive: the project is stamped and its revision advances.
        using var archived = await owner.SendAsync(await owner.Archive($"/api/projects/{projectId}", 1));

        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(2, await ReadRevisionAsync(archived));
        Assert.Equal(Clock.ToString("O", CultureInfo.InvariantCulture), await ReadStringAsync(archived, "archivedAt"));

        // Every kind of modification is refused, and the refusal is the same 409 in each case.
        var attempts = new (string Name, Func<Task<HttpRequestMessage>> Build)[]
        {
            ("project patch", () => owner.Patch($"/api/projects/{projectId}", 2, new { name = "Renamed" })),
            ("milestone patch", () => owner.Patch($"/api/milestones/{confirmedId}", 1, new { title = "Edited" })),
            ("milestone confirm", () => owner.Post($"/api/milestones/{candidateId}/confirm", null, 1)),
            ("milestone dismiss", () => owner.Post($"/api/milestones/{confirmedId}/dismiss", null, 1)),
            ("new milestone", () => owner.Post(
                $"/api/projects/{projectId}/milestones",
                new { title = "Late", occurredAt = Clock, type = "custom" })),
            ("import", () => owner.Post(
                $"/api/projects/{projectId}/milestones/import",
                new
                {
                    source = ImportSource,
                    candidates = new[] { new { sourceKey = "late", title = "Late", occurredAt = Clock, type = "custom" } },
                })),
            ("archive again", () => owner.Archive($"/api/projects/{projectId}", 2)),
        };

        foreach (var (_, build) in attempts)
        {
            using var request = await build();
            using var response = await owner.SendAsync(request);

            Assert.Equal(
                HttpStatusCode.Conflict,
                response.StatusCode);

            Assert.Equal(MilestoneError.ProjectArchived, await ReadCodeAsync(response));
        }

        // The history is preserved: both milestones are still there, unchanged, and still readable.
        Assert.Equal(2L, await ChronicleafPostgresFixture.CountRowsAsync(database, "milestones"));

        Assert.Equal(
            "Confirmed milestone|confirmed",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT title || '|' || status FROM milestones WHERE id = '{confirmedId}'"));

        Assert.Equal(
            MilestoneStatus.Candidate,
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT status FROM milestones WHERE id = '{candidateId}'"));

        using var project = await owner.GetAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.OK, project.StatusCode);
        Assert.NotNull(await ReadStringAsync(project, "archivedAt"));

        using var milestones = await owner.GetAsync($"/api/projects/{projectId}/milestones");
        Assert.Equal(HttpStatusCode.OK, milestones.StatusCode);

        using var document = JsonDocument.Parse(await milestones.Content.ReadAsStringAsync());
        Assert.Equal(2, document.RootElement.GetProperty("items").GetArrayLength());
    }

    // ===========================================================================================
    // Restart: the records, their ids and their revisions are still there afterwards.
    // ===========================================================================================

    [Fact]
    public async Task Projects_and_milestones_survive_a_host_restart_with_the_same_ids_and_revisions()
    {
        var database = await ProvisionedDatabaseAsync("restart");

        string projectId;
        string confirmedId;
        string candidateId;
        long projectRevision;
        long confirmedRevision;
        long candidateRevision;

        // The first host writes and goes away.
        await using (var first = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin))
        {
            using var owner = await OwnerClient.CreateAsync(first);

            projectId = await CreateProjectAsync(owner, "Ledger");
            confirmedId = await CreateMilestoneAsync(owner, projectId, "First import of the ledger", "feature");
            candidateId = await ImportCandidateAsync(owner, projectId, FirstSourceKey, "Imported candidate", "release");

            using var edited = await owner.SendAsync(
                await owner.Patch($"/api/milestones/{confirmedId}", 1, new { notes = "Reconciled." }));
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

            using var projectRead = await owner.GetAsync($"/api/projects/{projectId}");
            projectRevision = await ReadRevisionAsync(projectRead);

            using var confirmedRead = await owner.GetAsync($"/api/milestones/{confirmedId}");
            confirmedRevision = await ReadRevisionAsync(confirmedRead);

            using var candidateRead = await owner.GetAsync($"/api/milestones/{candidateId}");
            candidateRevision = await ReadRevisionAsync(candidateRead);
        }

        // A second host is a different process reading the same database. Nothing was cached.
        await using var second = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var reopened = await OwnerClient.CreateAsync(second);

        using (var projectRead = await reopened.GetAsync($"/api/projects/{projectId}"))
        {
            Assert.Equal(projectId, await ReadIdAsync(projectRead));
            Assert.Equal(projectRevision, await ReadRevisionAsync(projectRead));
        }

        using (var confirmedRead = await reopened.GetAsync($"/api/milestones/{confirmedId}"))
        {
            Assert.Equal(confirmedId, await ReadIdAsync(confirmedRead));
            Assert.Equal(confirmedRevision, await ReadRevisionAsync(confirmedRead));
            Assert.Equal(MilestoneStatus.Confirmed, await ReadStringAsync(confirmedRead, "status"));
        }

        using (var candidateRead = await reopened.GetAsync($"/api/milestones/{candidateId}"))
        {
            Assert.Equal(candidateId, await ReadIdAsync(candidateRead));
            Assert.Equal(candidateRevision, await ReadRevisionAsync(candidateRead));
            Assert.Equal(MilestoneStatus.Candidate, await ReadStringAsync(candidateRead, "status"));
        }

        Assert.Equal(
            "Reconciled.",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT notes FROM milestones WHERE id = '{confirmedId}'"));

        // And the same key is still the same record, so an import after a restart cannot duplicate it.
        var repeated = Assert.Single(await ImportAsync(reopened, projectId, FirstSourceKey, "Imported candidate", "release"));

        Assert.False(repeated.Created);
        Assert.Equal(candidateId, repeated.MilestoneId);
    }

    // ===========================================================================================
    // The scoped, private, honest edge: 401, 404, 422, paging, and the audit trail.
    // ===========================================================================================

    [Fact]
    public async Task An_anonymous_caller_reaches_no_project_and_no_milestone()
    {
        var database = await ProvisionedDatabaseAsync("anonymous");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var anonymous = host.CreateClient();

        foreach (var path in new[]
        {
            "/api/projects",
            $"/api/projects/{Guid.NewGuid():D}",
            $"/api/projects/{Guid.NewGuid():D}/milestones",
            $"/api/milestones/{Guid.NewGuid():D}",
        })
        {
            using var response = await anonymous.GetAsync(path);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(AccessError.Unauthenticated, await ReadCodeAsync(response));
        }

        using var mutation = await anonymous.PostAsJsonAsync("/api/projects", new { name = "Anonymous" });

        Assert.Equal(HttpStatusCode.Unauthorized, mutation.StatusCode);
        Assert.Equal(0L, await ChronicleafPostgresFixture.CountRowsAsync(database, "projects"));
    }

    [Fact]
    public async Task An_unknown_id_and_a_foreign_scope_id_are_both_404()
    {
        var database = await ProvisionedDatabaseAsync("not_found");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var milestoneId = await CreateMilestoneAsync(owner, projectId, "First import of the ledger", "feature");

        // A record that is genuinely there in another installation. It is inserted directly, because
        // no route in this package can create one: the scope is not a caller's to choose.
        var foreignProject = Guid.NewGuid();
        var foreignMilestone = Guid.NewGuid();
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            $"""
             INSERT INTO projects (id, scope_id, name, description, revision)
             VALUES ('{foreignProject}', '{ForeignScopeId}', 'Foreign', '', 1);
             INSERT INTO milestones
                 (id, project_id, title, occurred_at, type, source, source_key, status, revision, notes,
                  known_limitations, next_steps)
             VALUES
                 ('{foreignMilestone}', '{foreignProject}', 'Foreign milestone', '{Clock:O}', 'release',
                  '{ImportSource}', 'foreign-key', 'confirmed', 1, '', ARRAY[]::text[], ARRAY[]::text[]);
             """);

        foreach (var path in new[]
        {
            $"/api/projects/{Guid.NewGuid():D}",
            $"/api/milestones/{Guid.NewGuid():D}",
            $"/api/projects/{foreignProject:D}",
            $"/api/milestones/{foreignMilestone:D}",
            $"/api/projects/{foreignProject:D}/milestones",
        })
        {
            using var response = await owner.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(MilestoneError.NotFound, await ReadCodeAsync(response));
        }

        // A mutation of somebody else's record is refused the same way, before anything is written.
        using (var mutation = await owner.SendAsync(
            await owner.Patch($"/api/milestones/{foreignMilestone}", 1, new { title = "Edited" })))
        {
            Assert.Equal(HttpStatusCode.NotFound, mutation.StatusCode);
        }

        Assert.Equal(
            "Foreign milestone",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT title FROM milestones WHERE id = '{foreignMilestone}'"));

        // The real records are untouched by all of that.
        Assert.Equal(
            "First import of the ledger",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT title FROM milestones WHERE id = '{milestoneId}'"));
    }

    [Fact]
    public async Task Invalid_values_are_422_with_the_offending_field_named()
    {
        var database = await ProvisionedDatabaseAsync("validation");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");

        using var refused = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/milestones",
            new
            {
                title = new string('t', 161),
                version = new string('v', 81),
                occurredAt = Clock,
                type = "not-a-type",
                notes = new string('n', 20_001),
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(ApiError.ValidationFailed, await ReadCodeAsync(refused));

        var body = await refused.Content.ReadAsStringAsync();
        foreach (var field in new[] { "title", "version", "type", "notes" })
        {
            Assert.Contains($"\"{field}\"", body, StringComparison.Ordinal);
        }

        // A project URL is stored as written and never fetched; a non-http scheme is still refused.
        using var badUrl = await owner.SendAsync(
            await owner.Post("/api/projects", new { name = "Ledger", projectUrl = "file:///etc/passwd" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, badUrl.StatusCode);
        Assert.Contains("\"projectUrl\"", await badUrl.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // An http URL is accepted and stored verbatim, with nothing fetched.
        using var goodUrl = await owner.SendAsync(
            await owner.Post("/api/projects", new { name = "Reporting", projectUrl = "https://example.test/reporting" }));

        Assert.Equal(HttpStatusCode.Created, goodUrl.StatusCode);
        Assert.Equal(
            "https://example.test/reporting",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT project_url FROM projects WHERE id = '{await ReadIdAsync(goodUrl)}'"));

        // Nothing the refusal named was stored.
        Assert.Equal(0L, await ChronicleafPostgresFixture.CountRowsAsync(database, "milestones"));
    }

    [Fact]
    public async Task A_list_is_bounded_ordered_and_paged_with_a_scope_bound_cursor()
    {
        var database = await ProvisionedDatabaseAsync("listing");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");

        // Three milestones with distinct instants, created newest first.
        var ids = new List<string>();
        foreach (var offset in new[] { -1, -2, -3 })
        {
            ids.Add(await CreateMilestoneAsync(owner, projectId, $"Milestone {offset}", "feature", Clock.AddDays(offset)));
        }

        using (var page = await owner.GetAsync($"/api/projects/{projectId}/milestones"))
        {
            using var document = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
            var ordered = document.RootElement.GetProperty("items").EnumerateArray().ToArray();

            // occurredAt desc: the newest first, the oldest last.
            Assert.Equal(new[] { ids[0], ids[1], ids[2] }, ordered.Select(item => item.GetProperty("id").GetString()));
        }

        // A bounded page and a cursor, which is the position of the last row of the first page.
        string? cursor;
        using (var firstPage = await owner.GetAsync($"/api/projects/{projectId}/milestones?limit=2"))
        {
            using var document = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
            Assert.Equal(2, document.RootElement.GetProperty("items").GetArrayLength());
            cursor = document.RootElement.GetProperty("nextCursor").GetString();
        }

        Assert.NotNull(cursor);

        using (var secondPage = await owner.GetAsync(
            $"/api/projects/{projectId}/milestones?limit=2&cursor={Uri.EscapeDataString(cursor!)}"))
        {
            using var document = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
            Assert.Equal(ids[2], document.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nextCursor").ValueKind);
        }

        // A page size above the maximum is clamped rather than refused, and so is a nonsense one.
        using (var clamped = await owner.GetAsync("/api/projects?limit=1000"))
        {
            Assert.Equal(HttpStatusCode.OK, clamped.StatusCode);
        }

        // A status filter narrows the list, and an unknown status is a 422.
        using (var confirmed = await owner.GetAsync($"/api/projects/{projectId}/milestones?status=confirmed"))
        {
            using var document = JsonDocument.Parse(await confirmed.Content.ReadAsStringAsync());
            Assert.Equal(3, document.RootElement.GetProperty("items").GetArrayLength());
        }

        using (var unknown = await owner.GetAsync($"/api/projects/{projectId}/milestones?status=nonsense"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
            Assert.Contains("\"status\"", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A cursor that is not one this repository issued is a 422, not a crash and not an ignored
        // filter.
        using var foreign = await owner.GetAsync(
            $"/api/projects/{projectId}/milestones?cursor={Uri.EscapeDataString("bm90LWEtY3Vyc29y")}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Contains("\"cursor\"", await foreign.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cursor_issued_for_another_scope_is_refused()
    {
        var database = await ProvisionedDatabaseAsync("cursor_scope");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        // Encoded exactly as this repository encodes a cursor, but for a different installation.
        var foreign = ScopeCursor.Encode(ForeignScopeId, "Ledger", Guid.NewGuid());

        using var refused = await owner.GetAsync($"/api/projects?cursor={Uri.EscapeDataString(foreign)}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("\"cursor\"", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_and_dismiss_are_idempotent_and_only_the_owner_can_undo_a_dismissal()
    {
        var database = await ProvisionedDatabaseAsync("status_actions");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var candidateId = await ImportCandidateAsync(owner, projectId, FirstSourceKey, "Imported candidate", "release");

        // Confirming a candidate moves it and advances the revision.
        using var confirmed = await owner.SendAsync(await owner.Post($"/api/milestones/{candidateId}/confirm", null, 1));

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(MilestoneStatus.Confirmed, await ReadStringAsync(confirmed, "status"));
        Assert.Equal(2, await ReadRevisionAsync(confirmed));

        // Confirming it again at the revision it now holds changes nothing at all: same revision,
        // no second write, no second audit row.
        var auditBefore = await CountMilestoneAuditRowsAsync(database, ProjectMilestoneService.MilestoneConfirmedAction);
        using var repeated = await owner.SendAsync(await owner.Post($"/api/milestones/{candidateId}/confirm", null, 2));

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(MilestoneStatus.Confirmed, await ReadStringAsync(repeated, "status"));
        Assert.Equal(2, await ReadRevisionAsync(repeated));
        Assert.Equal(auditBefore, await CountMilestoneAuditRowsAsync(database, ProjectMilestoneService.MilestoneConfirmedAction));

        // An action presented with a stale revision is refused, so a client cannot act on a view that
        // is out of date.
        using var stale = await owner.SendAsync(await owner.Post($"/api/milestones/{candidateId}/dismiss", null, 1));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(MilestoneStatus.Confirmed, await ReadStatusAsync(owner, $"/api/milestones/{candidateId}"));

        // Dismissing keeps the record and advances the revision.
        using var dismissed = await owner.SendAsync(await owner.Post($"/api/milestones/{candidateId}/dismiss", null, 2));

        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        Assert.Equal(MilestoneStatus.Dismissed, await ReadStringAsync(dismissed, "status"));
        Assert.Equal(3, await ReadRevisionAsync(dismissed));

        // A dismissed record is still stored and still readable; it is not deleted.
        Assert.Equal(
            MilestoneStatus.Dismissed,
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT status FROM milestones WHERE id = '{candidateId}'"));

        // Dismissing again is likewise a no-op.
        using var dismissedAgain = await owner.SendAsync(await owner.Post($"/api/milestones/{candidateId}/dismiss", null, 3));

        Assert.Equal(HttpStatusCode.OK, dismissedAgain.StatusCode);
        Assert.Equal(3, await ReadRevisionAsync(dismissedAgain));

        // Bringing it back needs the explicit owner action, and nothing else in the package does it.
        using var restored = await owner.SendAsync(await owner.Post($"/api/milestones/{candidateId}/confirm", null, 3));

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(MilestoneStatus.Confirmed, await ReadStringAsync(restored, "status"));
        Assert.Equal(4, await ReadRevisionAsync(restored));
    }

    [Fact]
    public async Task Every_mutation_is_audited_with_ids_action_and_revision_and_nothing_else()
    {
        var database = await ProvisionedDatabaseAsync("audit");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var milestoneId = await CreateMilestoneAsync(owner, projectId, "First import of the ledger", "feature");

        using var edited = await owner.SendAsync(
            await owner.Patch($"/api/milestones/{milestoneId}", 1, new { notes = "A note the audit trail must not keep." }));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        Assert.Equal(
            $"milestone.created|{milestoneId}|1,milestone.updated|{milestoneId}|2",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"""
                 SELECT string_agg(action || '|' || target_id::text || '|' || (safe_metadata->>'revision'), ',' ORDER BY action)
                 FROM audit_events
                 WHERE target_id = '{milestoneId}'
                 """));

        // The trail records the action, the id and the revision. It does not become a second copy of
        // the record the owner wrote.
        Assert.DoesNotContain(
            "A note the audit trail must not keep.",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT string_agg(safe_metadata::text, ',') FROM audit_events") ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_installed_schema_must_carry_this_packages_migration()
    {
        // The readiness contract now names this package's migration, so a database migrated by an
        // earlier build is a mismatch that says which migration is missing.
        Assert.Contains(ChronicleafSchema.ProjectsAndMilestonesMigrationId, ChronicleafSchema.RequiredMigrationIds);

        // LOCAL-MEDIA-STORAGE-001 added a later forward migration, so the newest required id moved on
        // again. What this scenario asserts is unchanged and is in fact wider than before: the
        // schema's own identity is the newest required migration, the applied history carries every
        // migration this build requires, nothing is missing, and a database stopped at the access
        // package's migration reports exactly the remaining gaps, by name and in requirement order.
        // Only the id it names is this build's newest rather than this package's.
        // MILESTONE-EVIDENCE-001 moved it on once more, for the same reason, and this scenario now
        // also requires the evidence migration to be applied and to be named when it is missing.
        // CREDENTIAL-VAULT-001/002 moved it on again, and the same is now required of its migration.
        // VOICE-PROFILES-001/002 moved it on once more, so the same is required of the voice
        // migration as well. Every step of this has made the scenario strictly wider: one more id
        // that readiness refuses to serve without. LLM-PROVIDER-CONTRACT-001/002 moved it on again
        // for the generation-previews table, on the same terms. GITHUB-EVIDENCE-001/002 moved it on
        // again for the github_connections and sync_sources tables, on the same terms.
        // SCREENSHOT-CAPTURE-001/002 moved it on again for the capture_requests and
        // screenshot_candidates tables, on the same terms, and GROUNDED-MARKDOWN-DRAFTS-001/002 moved it
        // on again for content_assets, content_revisions and content_draft_requests, on the same terms.
        Assert.Contains(ChronicleafSchema.LocalMediaMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Contains(ChronicleafSchema.MilestoneEvidenceMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Contains(ChronicleafSchema.CredentialVaultMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Contains(ChronicleafSchema.VoiceProfilesMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Contains(ChronicleafSchema.GenerationPreviewsMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Contains(ChronicleafSchema.GitHubEvidenceMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Contains(ChronicleafSchema.ScreenshotCaptureMigrationId, ChronicleafSchema.RequiredMigrationIds);
        // The newest required id is asserted by its position in the required list rather than by naming
        // it, because a named "newest" is a value somebody has to remember to update and it is exactly
        // what made each of these three scenarios go red the moment a later package added a migration.
        // The position cannot go stale - it is the definition - and it says the same thing.
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);

        var database = await ProvisionedDatabaseAsync("schema_mismatch");
        var applied = await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT string_agg(\"MigrationId\", '|') FROM \"__EFMigrationsHistory\"");

        Assert.NotNull(applied);

        // Structural, not a hand-written list. This was an explicit enumeration of every required
        // migration, and an explicit enumeration is a value somebody has to remember to update - which
        // is why it went red every time a later package added one, and why each of those failures was a
        // test to edit rather than a defect to fix. The stronger claim is the one that cannot go stale:
        // every id this build requires was applied.
        Assert.All(
            ChronicleafSchema.RequiredMigrationIds,
            id => Assert.Contains(id, applied, StringComparison.Ordinal));

        Assert.Empty(ChronicleafSchema.MissingMigrationIds(applied.Split('|')));

        // A database stopped at the access package's migration reports every later gap, by name and in
        // requirement order - and "every later gap" is derived from the required list's own order rather
        // than from a second copy of it that could disagree with the first.
        var appliedSoFar = new[] { ChronicleafSchema.InitialMigrationId, ChronicleafSchema.OwnerAccessMigrationId };

        Assert.Equal(
            [.. ChronicleafSchema.RequiredMigrationIds.Where(id => !appliedSoFar.Contains(id, StringComparer.Ordinal))],
            ChronicleafSchema.MissingMigrationIds(appliedSoFar).ToArray());
    }

    // ===========================================================================================
    // Helpers.
    // ===========================================================================================

    /// <summary>
    /// A migrated database with the singleton installation provisioned through the data foundation's
    /// own contract, which is the order a real deployment follows. The installation id is also the
    /// scope every project in these scenarios belongs to.
    /// </summary>
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

    /// <summary>Counts rows matching a predicate, over a raw connection to the real database.</summary>
    private static async Task<long> CountWhereAsync(string database, string predicate, string table) =>
        long.Parse((await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT count(*)::text FROM {table} WHERE {predicate}"))!);

    private static async Task<string> CreateProjectAsync(OwnerClient owner, string name)
    {
        using var response = await owner.SendAsync(
            await owner.Post("/api/projects", new { name, description = $"{name} description." }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadIdAsync(response);
    }

    private static async Task<string> CreateMilestoneAsync(
        OwnerClient owner,
        string projectId,
        string title,
        string type,
        DateTimeOffset? occurredAt = null)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/milestones",
            new { title, occurredAt = occurredAt ?? Clock, type }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadIdAsync(response);
    }

    private static async Task<string> ImportCandidateAsync(
        OwnerClient owner,
        string projectId,
        string sourceKey,
        string title,
        string type)
    {
        var imported = Assert.Single(await ImportAsync(owner, projectId, sourceKey, title, type));

        Assert.True(imported.Created);

        return imported.MilestoneId;
    }

    private static async Task<IReadOnlyList<ImportedCandidateResult>> ImportAsync(
        OwnerClient owner,
        string projectId,
        string firstKey,
        string firstTitle,
        string type,
        params string[] alsoTheseKeys)
    {
        var candidates = new List<object>
        {
            new { sourceKey = firstKey, title = firstTitle, occurredAt = Clock, type },
        };

        candidates.AddRange(alsoTheseKeys.Select(key => (object)new
        {
            sourceKey = key,
            title = $"T for {key}",
            occurredAt = Clock,
            type,
        }));

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/milestones/import",
            new { source = ImportSource, candidates }));

        Assert.True(
            response.IsSuccessStatusCode,
            $"the import must be accepted: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return
        [
            .. document.RootElement
                .GetProperty("items")
                .EnumerateArray()
                .Select(item => new ImportedCandidateResult(
                    item.GetProperty("sourceKey").GetString()!,
                    item.GetProperty("milestoneId").GetString()!,
                    item.GetProperty("created").GetBoolean(),
                    response.StatusCode)),
        ];
    }

    private static string ReadSingleImportedId(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        return Assert.Single(
            document.RootElement.GetProperty("items").EnumerateArray().ToArray())
            .GetProperty("milestoneId")
            .GetString()!;
    }

    private static async Task<long> CountMilestoneAuditRowsAsync(string database, string action) =>
        long.Parse((await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT count(*)::text FROM audit_events WHERE action = '{action}'"))!);

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

    /// <summary>The status of the first milestone in a list response.</summary>
    private static async Task<string> ReadFirstStatusAsync(OwnerClient owner, string path)
    {
        using var response = await owner.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("items")[0].GetProperty("status").GetString()!;
    }

    private static async Task<string> ReadStatusAsync(OwnerClient owner, string path)
    {
        using var response = await owner.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await ReadStringAsync(response, "status"))!;
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

    private sealed record ImportedCandidateResult(
        string SourceKey,
        string MilestoneId,
        bool Created,
        HttpStatusCode Status);
}

/// <summary>
/// An authenticated owner, driving the real routes the way the browser would: a real session cookie,
/// the real origin and a real antiforgery value, requested per mutation exactly as the delivered
/// sign-out does it.
/// </summary>
internal sealed class OwnerClient : IDisposable
{
    // Internal rather than private so another fixture in this assembly can host the same boundary
    // with the same token and origin. The value is unchanged.
    internal const string AccessToken = "chronicleaf-operator-setup-token-0123456789abcdef";

    /// <summary>The hash the operators of every host in this assembly configure the boundary with.</summary>
    internal static readonly string AccessTokenHash = Sha256.Sha256Hex(AccessToken);
    internal const string PublicOrigin = "https://chronicleaf.example";
    private const string CookieName = "chronicleaf_session";
    private const string AntiforgeryHeader = "X-Chronicleaf-Antiforgery";

    private readonly HttpClient _client;
    private readonly string _cookie;

    private OwnerClient(HttpClient client, string cookie)
    {
        _client = client;
        _cookie = cookie;
    }

    /// <summary>
    /// Signs in for real against any host. The parameter is the framework's factory rather than one
    /// concrete host, so a second fixture in this assembly can reuse the same real sign-in instead of
    /// writing another one.
    /// </summary>
    public static async Task<OwnerClient> CreateAsync(WebApplicationFactory<Program> host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var client = host.CreateClient();

        using var signIn = await client.PostAsJsonAsync("/api/session", new { accessToken = AccessToken });
        signIn.EnsureSuccessStatusCode();

        var cookie = signIn.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=', 2)[1];

        return new OwnerClient(client, cookie);
    }

    public Task<HttpResponseMessage> GetAsync(string path) =>
        _client.SendAsync(Decorate(new HttpRequestMessage(HttpMethod.Get, path)));

    /// <summary>Sends a request built by <see cref="Post"/>, <see cref="Patch"/> or <see cref="Archive"/>.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _client.SendAsync(request);

    /// <summary>
    /// A mutation whose body is raw bytes with its own content type, rather than JSON.
    /// </summary>
    /// <remarks>
    /// Added for the media upload, which takes the image itself as the request body. It is a fixture
    /// addition only: the sign-in, the cookie, the origin and the antiforgery value are produced by
    /// exactly the same code as every other mutation here, so a scenario that uses it is exercising
    /// the delivered boundary and not a way around it. The declared content type is the caller's
    /// claim and nothing more; the API decides what the bytes are.
    /// </remarks>
    public async Task<HttpRequestMessage> PostBytes(string path, byte[] body, string contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(body),
        };

        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        request.Headers.Add("Cookie", $"{CookieName}={_cookie}");
        request.Headers.TryAddWithoutValidation("Origin", PublicOrigin);
        request.Headers.Add(AntiforgeryHeader, await ReadAntiforgeryAsync());

        return request;
    }

    /// <summary>A mutation with no revision, used for the actions and creations that do not take one.</summary>
    public Task<HttpRequestMessage> Post(string path, object? body) => Build(HttpMethod.Post, path, body);

    /// <summary>
    /// A mutation that carries a valid session and a valid antiforgery value but arrives from
    /// another origin, which is what a cross-site request forgery attempt looks like.
    /// </summary>
    /// <remarks>
    /// A fixture addition only, and additive: nothing existing changes and no existing scenario is
    /// affected. Everything the delivered boundary inspects is produced by exactly the same code as
    /// every other mutation here - the same sign-in exchange, the same session cookie, the same
    /// per-mutation antiforgery value - and the single difference is the <c>Origin</c> header, which
    /// is the thing under test. Without a way to send a foreign origin, the origin check can only be
    /// exercised by dropping the cookie, which is a 401 and proves nothing about CSRF.
    /// </remarks>
    public Task<HttpRequestMessage> PostCrossOrigin(string path, object? body, string origin) =>
        Build(HttpMethod.Post, path, body, revision: null, ifMatch: null, antiforgery: null, origin: origin);

    /// <summary>A mutation that must present the revision it is editing.</summary>
    public Task<HttpRequestMessage> Post(string path, object? body, long? revision) =>
        Build(HttpMethod.Post, path, body, revision);

    /// <summary>
    /// A mutation that takes no body of its own, such as a stop request. An empty JSON object is
    /// sent because the delivered boundary requires a content type on every mutation, and the route
    /// reads no body from it.
    /// </summary>
    public Task<HttpRequestMessage> PostWithoutBody(string path) => Build(HttpMethod.Post, path, null);

    /// <summary>
    /// A mutation that replaces a stored value in full, such as a sealed provider credential.
    /// </summary>
    /// <remarks>
    /// A fixture addition only: the sign-in, the cookie, the origin, the per-mutation antiforgery
    /// value and the <c>If-Match</c> header are produced by exactly the same code as every other
    /// mutation here, so a scenario that uses it is exercising the delivered boundary and not a way
    /// around it.
    /// </remarks>
    public Task<HttpRequestMessage> Put(string path, object? body, long? revision = null) =>
        Build(HttpMethod.Put, path, body, revision);

    public Task<HttpRequestMessage> Patch(
        string path,
        long? revision,
        object? body,
        string? ifMatch = null,
        string? antiforgery = null) =>
        Build(HttpMethod.Patch, path, body, revision, ifMatch, antiforgery);

    public Task<HttpRequestMessage> Archive(string path, long? revision) =>
        Build(HttpMethod.Delete, path, null, revision);

    /// <summary>
    /// A deletion with no revision, such as detaching an App installation connection.
    /// A fixture addition only: the sign-in, the cookie, the origin and the per-mutation
    /// antiforgery value are produced by exactly the same code as every other mutation here.
    /// </summary>
    public Task<HttpRequestMessage> DeleteAsync(string path) =>
        Build(HttpMethod.Delete, path, null);

    /// <summary>
    /// A deletion that carries its own confirmation body, such as the archive-data delete that
    /// names the project being deleted. A fixture addition only: the sign-in, the cookie, the
    /// origin, the antiforgery value and the If-Match header are produced by exactly the same
    /// code as every other mutation here.
    /// </summary>
    public Task<HttpRequestMessage> DeleteWithBody(string path, object? body, long? revision) =>
        Build(HttpMethod.Delete, path, body, revision);

    public void Dispose() => _client.Dispose();

    private async Task<HttpRequestMessage> Build(
        HttpMethod method,
        string path,
        object? body,
        long? revision = null,
        string? ifMatch = null,
        string? antiforgery = null,
        string? origin = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body ?? new { }),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Add("Cookie", $"{CookieName}={_cookie}");
        request.Headers.TryAddWithoutValidation("Origin", origin ?? PublicOrigin);

        // A fresh antiforgery value per mutation, requested the way the delivered sign-out does it:
        // used once and dropped, never reused.
        request.Headers.Add(AntiforgeryHeader, antiforgery ?? await ReadAntiforgeryAsync());

        if (revision is { } expected)
        {
            request.Headers.TryAddWithoutValidation("If-Match", expected.ToString(CultureInfo.InvariantCulture));
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    private HttpRequestMessage Decorate(HttpRequestMessage request)
    {
        request.Headers.Add("Cookie", $"{CookieName}={_cookie}");

        return request;
    }

    public async Task<string> ReadAntiforgeryAsync()
    {
        using var request = Decorate(new HttpRequestMessage(HttpMethod.Get, "/api/session/antiforgery"));
        using var response = await _client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("token").GetString()!;
    }
}
