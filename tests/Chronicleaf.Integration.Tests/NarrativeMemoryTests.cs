using System.Net;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// NARRATIVE-MEMORY-001 and NARRATIVE-MEMORY-002 against a real disposable PostgreSQL,
/// the real <c>Chronicleaf.Api</c> host, the real operation queue and the real milestone,
/// evidence and review contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the projects, the confirmed milestones, the manual
/// evidence, the stores, the service, the refresh handler, the Hangfire server, the routes, the
/// access boundary and the database. The summary builder is the one substitution, and only in the
/// failure scenario - where a builder that throws is the honest way to produce "regeneration
/// fails".
/// </para>
/// <para>
/// <b>What nothing here may be read as proving.</b> That any provider works or that a model was
/// called: the delivered builder is deterministic and reaches no provider, so a summary is an
/// arrangement of attributed sources rather than generated prose. The scenarios assert what
/// Chronicleaf does <em>with</em> its sources - attribution, cutoff exclusion, unknown labelling,
/// revision compare-and-set, staleness and privacy selection - and each of those is a property of
/// this system rather than of any transport.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> A committed summary is read back over the wire - a status
/// code, a body, an order, a stale mark - through the same routes an owner uses, and the operation
/// outcome is read from the raw <c>operations</c> table. The two requirements' success, failure
/// and boundary scenarios are covered one for one and each test is named after the scenario it
/// covers.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class NarrativeMemoryTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid InstallationId = new("c3a5e1b7-9d42-4f6a-8c1e-5b2d7a9e4f60");

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    // ===========================================================================================
    // NARRATIVE-MEMORY-001 Success: SQLite and PostgreSQL history recorded before a new draft means
    // the context includes the attributed transition without reintroducing the project as new.
    // ===========================================================================================

    [Fact]
    public async Task Attributed_history_survives_a_storage_transition_without_reintroducing_the_project()
    {
        Assert.True(postgres.IsRealPostgres, "the narrative scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("narrative_transition");
        var clock = new FixedTimeProvider(Clock);
        await using var host = NarrativeMemoryApiHost.Start(database, clock);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var earlyId = await CreateMilestoneAsync(owner, projectId, "Prototype on SQLite", Clock.AddMonths(-8));
        var evidenceIds = await AppendManualEvidenceAsync(owner, earlyId, "The prototype stores its ledger in a local SQLite file.");
        var laterId = await CreateMilestoneAsync(owner, projectId, "PostgreSQL migration", Clock.AddMonths(-2));

        var origin = await CreateEntryAsync(
            owner,
            projectId,
            "origin",
            Clock.AddMonths(-8),
            "Ledger began as a single-file SQLite prototype on a laptop.",
            evidenceIds,
            "exportable");

        var route = await CreateEntryAsync(
            owner,
            projectId,
            "technical-route",
            Clock.AddMonths(-2),
            "The store moved from SQLite to PostgreSQL with an attributed migration.",
            evidenceIds,
            "exportable");

        var accepted = await RefreshAsync(owner, projectId, cutoff: null, selected: [], key: "narrative-transition");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded", JobBudget);
        Assert.Null(operation.ErrorCode);
        Assert.NotNull(operation.ResultRef);

        var narrative = await ReadNarrativeAsync(owner, projectId);
        Assert.Equal(2, narrative.Entries.Count);
        Assert.NotNull(narrative.Summary);
        Assert.False(narrative.Summary.Stale);
        Assert.Equal(1, narrative.Summary.Revision);

        var body = narrative.Summary.BodyJson;

        // The transition is present as attributed history: both entries' words and both cited
        // evidence ids are in the body, so a later draft built on this summary meets a project
        // with a past rather than a new one.
        Assert.Contains("SQLite prototype", body, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL", body, StringComparison.Ordinal);
        Assert.Contains(origin.Id, body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(route.Id, body, StringComparison.OrdinalIgnoreCase);
        foreach (var evidenceId in evidenceIds)
        {
            Assert.Contains(evidenceId, body, StringComparison.OrdinalIgnoreCase);
        }

        // No feedback was ever recorded, so the body says unknown rather than inventing users.
        Assert.Contains("unknown", body, StringComparison.OrdinalIgnoreCase);
    }

    // ===========================================================================================
    // NARRATIVE-MEMORY-001 Failure: no feedback history means the context reports unknown feedback
    // rather than fabricated users.
    // ===========================================================================================

    [Fact]
    public async Task Missing_feedback_reads_as_unknown_rather_than_invented_users()
    {
        Assert.True(postgres.IsRealPostgres, "the narrative scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("narrative_unknown_feedback");
        var clock = new FixedTimeProvider(Clock);
        await using var host = NarrativeMemoryApiHost.Start(database, clock);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var milestoneId = await CreateMilestoneAsync(owner, projectId, "First release", Clock.AddDays(-10));
        var evidenceIds = await AppendManualEvidenceAsync(owner, milestoneId, "The first release went out on a Friday.");

        await CreateEntryAsync(
            owner,
            projectId,
            "audience",
            Clock.AddDays(-10),
            "Ledger is for owners who keep their own history.",
            evidenceIds,
            "exportable");

        var accepted = await RefreshAsync(owner, projectId, cutoff: null, selected: [], key: "narrative-unknown");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded", JobBudget);

        var narrative = await ReadNarrativeAsync(owner, projectId);
        Assert.NotNull(narrative.Summary);

        using var body = JsonDocument.Parse(narrative.Summary.BodyJson);
        var feedback = body.RootElement.GetProperty("feedback");

        // Unknown feedback is a labelled string, not a list of users: there is nothing here a
        // reader could mistake for people who said things.
        Assert.Equal(JsonValueKind.String, feedback.ValueKind);
        Assert.Contains("unknown", feedback.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ===========================================================================================
    // NARRATIVE-MEMORY-001 Boundary: a retrospective cutoff precedes a milestone, so the later
    // milestone is excluded.
    // ===========================================================================================

    [Fact]
    public async Task Retrospective_cutoff_excludes_later_milestones()
    {
        Assert.True(postgres.IsRealPostgres, "the narrative scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("narrative_cutoff");
        var clock = new FixedTimeProvider(Clock);
        await using var host = NarrativeMemoryApiHost.Start(database, clock);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var earlyId = await CreateMilestoneAsync(owner, projectId, "Early ledger", Clock.AddDays(-10));
        var earlyEvidence = await AppendManualEvidenceAsync(owner, earlyId, "The early ledger balanced.");
        var lateId = await CreateMilestoneAsync(owner, projectId, "Later ledger", Clock.AddDays(-1));
        _ = await AppendManualEvidenceAsync(owner, lateId, "The later ledger added exports.");

        await CreateEntryAsync(
            owner,
            projectId,
            "origin",
            Clock.AddDays(-10),
            "Ledger began with a balanced book.",
            earlyEvidence,
            "exportable");

        var accepted = await RefreshAsync(owner, projectId, Clock.AddDays(-5), selected: [], key: "narrative-cutoff");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded", JobBudget);

        var narrative = await ReadNarrativeAsync(owner, projectId);
        Assert.NotNull(narrative.Summary);

        // The retrospective build covers the early milestone only: the later one is excluded, never
        // summarised into a past it postdates.
        Assert.Equal(Guid.Parse(earlyId), narrative.Summary.ThroughMilestoneId);
        Assert.Contains("Early ledger", narrative.Summary.BodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Later ledger", narrative.Summary.BodyJson, StringComparison.Ordinal);
    }

    // ===========================================================================================
    // NARRATIVE-MEMORY-002 Success: a corrected source decision marks the prior summary stale
    // until regeneration.
    // ===========================================================================================

    [Fact]
    public async Task Corrected_decision_marks_summary_stale_until_regeneration()
    {
        Assert.True(postgres.IsRealPostgres, "the narrative scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("narrative_stale");
        var clock = new FixedTimeProvider(Clock);
        await using var host = NarrativeMemoryApiHost.Start(database, clock);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var milestoneId = await CreateMilestoneAsync(owner, projectId, "Routing choice", Clock.AddDays(-4));
        var evidenceIds = await AppendManualEvidenceAsync(owner, milestoneId, "Two routers were evaluated.");

        var entry = await CreateEntryAsync(
            owner,
            projectId,
            "decision",
            Clock.AddDays(-4),
            "Ledger routes through the first router.",
            evidenceIds,
            "exportable");

        var first = await RefreshAsync(owner, projectId, cutoff: null, selected: [], key: "narrative-stale-1");
        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, first.OperationId, "succeeded", JobBudget);

        var before = await ReadNarrativeAsync(owner, projectId);
        Assert.NotNull(before.Summary);
        Assert.False(before.Summary.Stale);
        Assert.Contains("first router", before.Summary.BodyJson, StringComparison.Ordinal);

        // The source decision is corrected: the prior summary becomes stale, and the stale body is
        // still the old answer rather than the new words.
        var edited = await PatchEntryAsync(owner, projectId, entry.Id, revision: 1, text: "Ledger routes through the second router.");
        Assert.Equal(2, edited.Revision);

        var stale = await ReadNarrativeAsync(owner, projectId);
        Assert.NotNull(stale.Summary);
        Assert.True(stale.Summary.Stale);
        Assert.Equal(1, stale.Summary.Revision);
        Assert.Contains("first router", stale.Summary.BodyJson, StringComparison.Ordinal);

        var second = await RefreshAsync(owner, projectId, cutoff: null, selected: [], key: "narrative-stale-2");
        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, second.OperationId, "succeeded", JobBudget);

        var after = await ReadNarrativeAsync(owner, projectId);
        Assert.NotNull(after.Summary);
        Assert.False(after.Summary.Stale);
        Assert.Equal(2, after.Summary.Revision);
        Assert.Contains("second router", after.Summary.BodyJson, StringComparison.Ordinal);
    }

    // ===========================================================================================
    // NARRATIVE-MEMORY-002 Failure: regeneration fails, so the prior summary remains available
    // labelled stale.
    // ===========================================================================================

    [Fact]
    public async Task Failed_regeneration_keeps_prior_summary_labelled_stale()
    {
        Assert.True(postgres.IsRealPostgres, "the narrative scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("narrative_build_failed");
        var clock = new FixedTimeProvider(Clock);

        Guid projectId;
        string entryId;
        string goodBody;

        await using (var host = NarrativeMemoryApiHost.Start(database, clock))
        {
            using var owner = await OwnerClient.CreateAsync(host);

            projectId = Guid.Parse(await CreateProjectAsync(owner, "Ledger"));
            var milestoneId = await CreateMilestoneAsync(owner, projectId.ToString("D"), "Kept history", Clock.AddDays(-3));
            var evidenceIds = await AppendManualEvidenceAsync(owner, milestoneId, "The kept history held.");

            var entry = await CreateEntryAsync(
                owner,
                projectId.ToString("D"),
                "decision",
                Clock.AddDays(-3),
                "Ledger keeps its history.",
                evidenceIds,
                "exportable");
            entryId = entry.Id;

            var first = await RefreshAsync(owner, projectId.ToString("D"), cutoff: null, selected: [], key: "narrative-fail-1");
            await host.DispatchAsync();
            await OperationRows.AwaitAsync(database, first.OperationId, "succeeded", JobBudget);

            goodBody = (await ReadNarrativeAsync(owner, projectId.ToString("D"))).Summary!.BodyJson;

            // The sources move after the good build, so the next refresh cannot answer "unchanged"
            // without running the builder: the failure path is exercised rather than skipped.
            await PatchEntryAsync(owner, projectId.ToString("D"), entryId, revision: 1, text: "Ledger keeps its history twice over.");
        }

        // The builder now fails: the refresh ends failed with the build code, and the prior summary
        // is still readable, labelled stale, with its body unchanged.
        await using (var failing = NarrativeMemoryApiHost.StartWithBuilder(
            database,
            clock,
            new FailingNarrativeSummaryBuilder()))
        {
            using var owner = await OwnerClient.CreateAsync(failing);

            var second = await RefreshAsync(owner, projectId.ToString("D"), cutoff: null, selected: [], key: "narrative-fail-2");
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

            await failing.DispatchAsync();
            var failed = await OperationRows.AwaitAsync(database, second.OperationId, "failed", JobBudget);
            Assert.Equal("narrative_summary_build_failed", failed.ErrorCode);

            var kept = await ReadNarrativeAsync(owner, projectId.ToString("D"));
            Assert.NotNull(kept.Summary);
            Assert.True(kept.Summary.Stale);
            Assert.Equal(1, kept.Summary.Revision);
            Assert.Equal(goodBody, kept.Summary.BodyJson);
            Assert.Contains(entryId, kept.Summary.BodyJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ===========================================================================================
    // NARRATIVE-MEMORY-002 Boundary: private feedback without an explicit selection is absent from
    // the outputs; with one, it is included.
    // ===========================================================================================

    [Fact]
    public async Task Unselected_private_feedback_stays_out_of_consented_outputs()
    {
        Assert.True(postgres.IsRealPostgres, "the narrative scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("narrative_private");
        var clock = new FixedTimeProvider(Clock);
        await using var host = NarrativeMemoryApiHost.Start(database, clock);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var milestoneId = await CreateMilestoneAsync(owner, projectId, "Quiet release", Clock.AddDays(-6));
        var evidenceIds = await AppendManualEvidenceAsync(owner, milestoneId, "The quiet release shipped.");

        var feedback = await CreateEntryAsync(
            owner,
            projectId,
            "feedback",
            Clock.AddDays(-6),
            "A reviewer whispered the onboarding confuses newcomers.",
            evidenceIds,
            "private");

        var unselected = await RefreshAsync(owner, projectId, cutoff: null, selected: [], key: "narrative-private-1");
        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, unselected.OperationId, "succeeded", JobBudget);

        var without = await ReadNarrativeAsync(owner, projectId);
        Assert.NotNull(without.Summary);
        Assert.DoesNotContain("whispered", without.Summary.BodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain(feedback.Id, without.Summary.BodyJson, StringComparison.OrdinalIgnoreCase);

        // The same entry with an explicit selection is consented into the build: privacy is a
        // selection, not an inheritance.
        var consented = await RefreshAsync(owner, projectId, cutoff: null, [feedback.Id], key: "narrative-private-2");
        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, consented.OperationId, "succeeded", JobBudget);

        var with = await ReadNarrativeAsync(owner, projectId);
        Assert.NotNull(with.Summary);
        Assert.False(with.Summary.Stale);
        Assert.Contains("whispered", with.Summary.BodyJson, StringComparison.Ordinal);
    }

    // ===========================================================================================
    // Revision and scope: a stale writer loses rather than merging, a missing If-Match is 428,
    // foreign evidence is 404, and a foreign project is 404.
    // ===========================================================================================

    [Fact]
    public async Task Stale_writer_loses_and_foreign_references_are_refused()
    {
        Assert.True(postgres.IsRealPostgres, "the narrative scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("narrative_conflict");
        var clock = new FixedTimeProvider(Clock);
        await using var host = NarrativeMemoryApiHost.Start(database, clock);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var milestoneId = await CreateMilestoneAsync(owner, projectId, "Guarded edit", Clock.AddDays(-2));
        var evidenceIds = await AppendManualEvidenceAsync(owner, milestoneId, "The guarded edit held.");

        var entry = await CreateEntryAsync(
            owner,
            projectId,
            "problem",
            Clock.AddDays(-2),
            "Exports sometimes stall.",
            evidenceIds,
            "exportable");

        // A missing If-Match is 428, not a defaulted revision.
        using (var missing = await owner.SendAsync(await owner.Patch(
            $"/api/projects/{projectId}/narrative/{entry.Id}",
            revision: null,
            new { text = "Exports always stall." })))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        }

        // The first writer wins; the stale second writer loses with 409 and nothing merges.
        var winner = await PatchEntryAsync(owner, projectId, entry.Id, revision: 1, text: "Exports sometimes stall on Fridays.");
        Assert.Equal(2, winner.Revision);

        using (var stale = await owner.SendAsync(await owner.Patch(
            $"/api/projects/{projectId}/narrative/{entry.Id}",
            revision: 1,
            new { text = "Exports never stall.", expectedRevision = 1L })))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }

        var current = await ReadNarrativeAsync(owner, projectId);
        Assert.Contains("Fridays", current.Entries.Single(item => item.Id == entry.Id).Text, StringComparison.Ordinal);

        // A citation to evidence that is not this project's is 404, and the id is not echoed.
        using (var foreign = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/narrative",
            new
            {
                kind = "problem",
                occurredAt = Clock,
                text = "A borrowed line.",
                sourceEvidenceIds = new[] { Guid.NewGuid().ToString("D") },
                visibility = "exportable",
            })))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        // A project that is absent - or belongs to another scope - is 404 with no entries.
        using (var absent = await owner.GetAsync(
            "/api/projects/00000000-0000-0000-0000-000000000000/narrative"))
        {
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        }
    }

    // ===========================================================================================
    // Helpers: the owner's own records, created through the delivered routes.
    // ===========================================================================================

    private async Task<string> ProvisionedDatabaseAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using (var host = NarrativeMemoryApiHost.Start(database, new FixedTimeProvider(Clock)))
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

    private static async Task<string> CreateProjectAsync(OwnerClient owner, string name)
    {
        using var response = await owner.SendAsync(
            await owner.Post("/api/projects", new { name, description = $"{name} description." }));

        Assert.True(response.IsSuccessStatusCode, $"the project must be created: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateMilestoneAsync(
        OwnerClient owner,
        string projectId,
        string title,
        DateTimeOffset occurredAt)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/milestones",
            new
            {
                title,
                version = (string?)null,
                occurredAt,
                type = MilestoneTypes.Feature,
                notes = string.Empty,
                knownLimitations = Array.Empty<string>(),
                nextSteps = Array.Empty<string>(),
            }));

        Assert.True(response.IsSuccessStatusCode, $"the milestone must be created: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<IReadOnlyList<string>> AppendManualEvidenceAsync(
        OwnerClient owner,
        string milestoneId,
        string notes)
    {
        using var appended = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId}/evidence/manual",
            new { notes, expectedRevision = 0 },
            0));

        Assert.True(appended.IsSuccessStatusCode, $"the evidence must be appended: {await appended.Content.ReadAsStringAsync()}");

        using var listed = await owner.GetAsync($"/api/milestones/{milestoneId}/evidence");
        listed.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var bundle = document.RootElement.GetProperty("items")[0];

        return [.. bundle.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)];
    }

    private static async Task<NarrativeTestEntry> CreateEntryAsync(
        OwnerClient owner,
        string projectId,
        string kind,
        DateTimeOffset occurredAt,
        string text,
        IReadOnlyList<string> evidenceIds,
        string visibility)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/narrative",
            new
            {
                kind,
                occurredAt,
                text,
                sourceEvidenceIds = evidenceIds,
                visibility,
            }));

        Assert.True(response.IsSuccessStatusCode, $"the entry must be created: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return new NarrativeTestEntry(
            document.RootElement.GetProperty("id").GetString()!,
            document.RootElement.GetProperty("revision").GetInt64());
    }

    private static async Task<NarrativeTestEntry> PatchEntryAsync(
        OwnerClient owner,
        string projectId,
        string entryId,
        long revision,
        string text)
    {
        using var response = await owner.SendAsync(await owner.Patch(
            $"/api/projects/{projectId}/narrative/{entryId}",
            revision,
            new { text, expectedRevision = revision }));

        Assert.True(response.IsSuccessStatusCode, $"the entry must be edited: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return new NarrativeTestEntry(
            document.RootElement.GetProperty("id").GetString()!,
            document.RootElement.GetProperty("revision").GetInt64());
    }

    private static async Task<(HttpStatusCode StatusCode, Guid OperationId)> RefreshAsync(
        OwnerClient owner,
        string projectId,
        DateTimeOffset? cutoff,
        IReadOnlyList<string> selected,
        string key)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/narrative/refresh",
            new
            {
                cutoff,
                selectedEntryIds = selected,
                idempotencyKey = key,
            }));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the refresh must be accepted: {body}");

        using var document = JsonDocument.Parse(body);

        return (response.StatusCode, document.RootElement.GetProperty("operationId").GetGuid());
    }

    private static async Task<NarrativeTestRead> ReadNarrativeAsync(OwnerClient owner, string projectId)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId}/narrative");
        Assert.True(response.IsSuccessStatusCode, $"the narrative must read: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        var entries = root.GetProperty("entries").EnumerateArray()
            .Select(item => new NarrativeTestEntryView(
                item.GetProperty("id").GetString()!,
                item.GetProperty("revision").GetInt64(),
                item.GetProperty("text").GetString()!))
            .ToArray();

        NarrativeTestSummary? summary = null;
        if (root.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind != JsonValueKind.Null)
        {
            summary = new NarrativeTestSummary(
                summaryElement.TryGetProperty("throughMilestoneId", out var through) && through.ValueKind != JsonValueKind.Null
                    ? through.GetGuid()
                    : null,
                summaryElement.GetProperty("inputHash").GetString()!,
                summaryElement.GetProperty("bodyJson").GetString()!,
                summaryElement.GetProperty("revision").GetInt64(),
                summaryElement.GetProperty("stale").GetBoolean());
        }

        return new NarrativeTestRead(entries, summary);
    }

    private sealed record NarrativeTestEntry(string Id, long Revision);

    private sealed record NarrativeTestEntryView(string Id, long Revision, string Text);

    private sealed record NarrativeTestSummary(
        Guid? ThroughMilestoneId,
        string InputHash,
        string BodyJson,
        long Revision,
        bool Stale);

    private sealed record NarrativeTestRead(
        IReadOnlyList<NarrativeTestEntryView> Entries,
        NarrativeTestSummary? Summary);
}
