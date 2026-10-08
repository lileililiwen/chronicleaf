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
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// MILESTONE-TIMELINE-001 and MILESTONE-TIMELINE-002 against a real disposable PostgreSQL
/// started from a pinned Docker image, through the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// Nothing here is simulated. Projects, milestones, confirmations, dismissals and the timeline
/// pages are all produced by a real PostgreSQL server, and every claim is read back over the wire -
/// a status code, a body, an order - rather than from a store this process built. There is no EF
/// in-memory provider and no stubbed store: an unavailable container is a failure, never a passing
/// result.
/// <para>
/// The clock is injected and frozen, so every stored instant is deterministic. The cursor fixture
/// is the design's own: 52 confirmed records and 3 candidates across equal timestamps and two
/// projects, paged and concatenated without a duplicated id, with candidates toggled and a foreign
/// cursor refused.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class MilestoneTimelineTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly string AccessTokenHash = Sha256.Sha256Hex(OwnerClient.AccessToken);

    private static readonly Guid InstallationId = new("6e1c8b3a-2f47-4d5e-9a10-2b3c4d5e6f71");

    private const string ImportSource = "github";

    // ===========================================================================================
    // MILESTONE-TIMELINE-001 Failure: a project with no confirmed milestones reads as an empty
    // timeline rather than as an error or an invented history.
    // ===========================================================================================

    [Fact]
    public async Task A_project_with_no_confirmed_milestones_reads_as_an_empty_timeline()
    {
        Assert.True(postgres.IsRealPostgres, "the timeline scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("timeline_empty");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, OwnerClient.PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");

        var page = await ReadTimelineAsync(owner, projectId);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    // ===========================================================================================
    // MILESTONE-TIMELINE-001 Success: 52 confirmed records across equal timestamps page
    // deterministically oldest first, with no duplicated id.
    // ===========================================================================================

    [Fact]
    public async Task Paging_concatenates_without_duplicates_in_stable_chronological_order()
    {
        Assert.True(postgres.IsRealPostgres, "the timeline scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("timeline_pages");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, OwnerClient.PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");

        // 52 confirmed records, in thirteen groups of four sharing one instant each, so the order
        // within an instant is decided by id alone and must still be deterministic.
        for (var index = 0; index < 52; index++)
        {
            await CreateMilestoneAsync(
                owner,
                projectId,
                $"Milestone {index + 1:00}",
                Clock.AddHours(-(index / 4)));
        }

        var seen = new List<TimelineEntry>();
        string? cursor = null;

        do
        {
            var page = await ReadTimelineAsync(owner, projectId, cursor, limit: 7);

            seen.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(52, seen.Count);
        Assert.Equal(52, seen.Select(item => item.Id).Distinct().Count());

        var ordered = seen.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        Assert.Equal(ordered.Select(item => item.Id), seen.Select(item => item.Id));

        // The same history reads identically twice: equal timestamps have one order, not a new one
        // per read.
        var reread = new List<TimelineEntry>();
        string? again = null;

        do
        {
            var page = await ReadTimelineAsync(owner, projectId, again, limit: 9);
            reread.AddRange(page.Items);
            again = page.NextCursor;
        }
        while (again is not null);

        Assert.Equal(seen.Select(item => item.Id), reread.Select(item => item.Id));
    }

    // ===========================================================================================
    // MILESTONE-TIMELINE-001 Boundary: candidates appear labeled when asked for and never as
    // confirmed; dismissed records are omitted in both views.
    // ===========================================================================================

    [Fact]
    public async Task Candidates_appear_labeled_when_asked_for_and_dismissed_records_are_omitted()
    {
        Assert.True(postgres.IsRealPostgres, "the timeline scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("timeline_candidates");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, OwnerClient.PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var confirmedId = await CreateMilestoneAsync(owner, projectId, "Shipped", Clock.AddDays(-2));
        var candidateId = await ImportCandidateAsync(owner, projectId, "release/v2.0.0", "Release candidate");
        var dismissedId = await CreateMilestoneAsync(owner, projectId, "Abandoned", Clock.AddDays(-1));
        await DismissMilestoneAsync(owner, dismissedId);

        var confirmed = await ReadTimelineAsync(owner, projectId);

        Assert.DoesNotContain(confirmed.Items, item => item.Id == candidateId);
        Assert.DoesNotContain(confirmed.Items, item => item.Id == dismissedId);
        var kept = Assert.Single(confirmed.Items);
        Assert.Equal(confirmedId, kept.Id);
        Assert.Equal(MilestoneStatus.Confirmed, kept.Status);

        var withCandidates = await ReadTimelineAsync(owner, projectId, includeCandidates: true);

        Assert.DoesNotContain(withCandidates.Items, item => item.Id == dismissedId);
        Assert.Equal(2, withCandidates.Items.Count);

        var candidate = Assert.Single(withCandidates.Items, item => item.Id == candidateId);
        Assert.Equal(MilestoneStatus.Candidate, candidate.Status);

        // The confirmed row is still confirmed when candidates are shown: the view labels what is
        // stored rather than promoting anything.
        Assert.Equal(
            MilestoneStatus.Confirmed,
            Assert.Single(withCandidates.Items, item => item.Id == confirmedId).Status);
    }

    // ===========================================================================================
    // MILESTONE-TIMELINE-002 Failure: a cursor from another project is refused without foreign
    // records.
    // ===========================================================================================

    [Fact]
    public async Task A_cursor_from_another_project_is_refused_without_foreign_records()
    {
        Assert.True(postgres.IsRealPostgres, "the timeline scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("timeline_foreign_cursor");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, OwnerClient.PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var firstProject = await CreateProjectAsync(owner, "Ledger");
        var secondProject = await CreateProjectAsync(owner, "Reporting");
        await CreateMilestoneAsync(owner, firstProject, "First history", Clock.AddDays(-3));
        await CreateMilestoneAsync(owner, secondProject, "Second history", Clock.AddDays(-2));
        await CreateMilestoneAsync(owner, secondProject, "Second history continued", Clock.AddDays(-1));

        var foreign = await ReadTimelineAsync(owner, secondProject, limit: 1);
        Assert.NotNull(foreign.NextCursor);

        using var refused = await owner.GetAsync(
            $"/api/projects/{firstProject}/timeline?limit=25&cursor={Uri.EscapeDataString(foreign.NextCursor)}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("cursor", await ReadFirstFieldErrorAsync(refused));

        using var unreadable = await owner.GetAsync("/api/projects/00000000-0000-0000-0000-000000000000/timeline");
        Assert.Equal(HttpStatusCode.NotFound, unreadable.StatusCode);

        using var malformed = await owner.GetAsync($"/api/projects/{firstProject}/timeline?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
        Assert.Equal("cursor", await ReadFirstFieldErrorAsync(malformed));
    }

    // ===========================================================================================
    // MILESTONE-TIMELINE-002 Failure: a cursor is bound to its filter and is not a milestone-list
    // cursor.
    // ===========================================================================================

    [Fact]
    public async Task A_cursor_is_bound_to_its_filter_and_not_to_another_list()
    {
        Assert.True(postgres.IsRealPostgres, "the timeline scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("timeline_cursor_scope");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, OwnerClient.PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");

        for (var index = 0; index < 6; index++)
        {
            await CreateMilestoneAsync(owner, projectId, $"Milestone {index + 1}", Clock.AddHours(-index));
        }

        var page = await ReadTimelineAsync(owner, projectId, limit: 2);
        Assert.NotNull(page.NextCursor);

        // The confirmed-history cursor does not open the candidate view.
        using var refiltered = await owner.GetAsync(
            $"/api/projects/{projectId}/timeline?limit=25&includeCandidates=true&cursor={Uri.EscapeDataString(page.NextCursor)}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refiltered.StatusCode);
        Assert.Equal("cursor", await ReadFirstFieldErrorAsync(refiltered));

        // A milestone-list cursor is a different shape and is refused rather than misread.
        using var listed = await owner.GetAsync($"/api/projects/{projectId}/milestones?limit=1");
        listed.EnsureSuccessStatusCode();
        using var listedDocument = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var listCursor = listedDocument.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(listCursor);

        using var crossed = await owner.GetAsync(
            $"/api/projects/{projectId}/timeline?limit=25&cursor={Uri.EscapeDataString(listCursor)}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossed.StatusCode);
        Assert.Equal("cursor", await ReadFirstFieldErrorAsync(crossed));
    }

    // ===========================================================================================
    // MILESTONE-TIMELINE-002 Boundary: a backdated edit moves its row on the next fresh read.
    // ===========================================================================================

    [Fact]
    public async Task A_backdated_milestone_moves_to_its_new_chronological_position()
    {
        Assert.True(postgres.IsRealPostgres, "the timeline scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("timeline_backdate");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, OwnerClient.PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var earlyId = await CreateMilestoneAsync(owner, projectId, "Early work", Clock.AddDays(-10));
        var lateId = await CreateMilestoneAsync(owner, projectId, "Later work", Clock.AddDays(-1));

        var before = await ReadTimelineAsync(owner, projectId);
        Assert.Equal([earlyId, lateId], before.Items.Select(item => item.Id));

        await BackdateMilestoneAsync(owner, lateId, Clock.AddDays(-20));

        var after = await ReadTimelineAsync(owner, projectId);
        Assert.Equal([lateId, earlyId], after.Items.Select(item => item.Id));
    }

    // ===========================================================================================
    // MILESTONE-TIMELINE-002 Success: an archived project's history stays readable.
    // ===========================================================================================

    [Fact]
    public async Task An_archived_project_timeline_stays_readable()
    {
        Assert.True(postgres.IsRealPostgres, "the timeline scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("timeline_archived");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, OwnerClient.PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, "Ledger");
        var milestoneId = await CreateMilestoneAsync(owner, projectId, "Shipped", Clock.AddDays(-2));

        var revision = await ReadProjectRevisionAsync(owner, projectId);
        using var archived = await owner.SendAsync(await owner.Archive($"/api/projects/{projectId}", revision));
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var page = await ReadTimelineAsync(owner, projectId);
        Assert.Equal(milestoneId, Assert.Single(page.Items).Id);
    }

    private static async Task<string> CreateProjectAsync(OwnerClient owner, string name)
    {
        using var response = await owner.SendAsync(
            await owner.Post("/api/projects", new { name, description = $"{name} description." }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

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

        Assert.True(
            response.IsSuccessStatusCode,
            $"the milestone must be created: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> ImportCandidateAsync(
        OwnerClient owner,
        string projectId,
        string sourceKey,
        string title)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/milestones/import",
            new
            {
                source = ImportSource,
                candidates = new[] { new { sourceKey, title, occurredAt = Clock, type = MilestoneTypes.Release } },
            }));

        Assert.True(
            response.IsSuccessStatusCode,
            $"the candidate must be imported: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = document.RootElement.GetProperty("items").EnumerateArray().Single();

        Assert.True(item.GetProperty("created").GetBoolean());

        return item.GetProperty("milestoneId").GetString()!;
    }

    private static async Task DismissMilestoneAsync(OwnerClient owner, string milestoneId)
    {
        var revision = await ReadMilestoneRevisionAsync(owner, milestoneId);

        using var response = await owner.SendAsync(
            await owner.Post($"/api/milestones/{milestoneId}/dismiss", new { }, revision));

        Assert.True(
            response.IsSuccessStatusCode,
            $"the milestone must be dismissed: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task BackdateMilestoneAsync(OwnerClient owner, string milestoneId, DateTimeOffset occurredAt)
    {
        var revision = await ReadMilestoneRevisionAsync(owner, milestoneId);

        using var response = await owner.SendAsync(
            await owner.Patch($"/api/milestones/{milestoneId}", revision, new { occurredAt }));

        Assert.True(
            response.IsSuccessStatusCode,
            $"the milestone must be backdated: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<TimelineResult> ReadTimelineAsync(
        OwnerClient owner,
        string projectId,
        string? cursor = null,
        int? limit = null,
        bool includeCandidates = false)
    {
        var query = new List<string>();

        if (limit is { } pageSize)
        {
            query.Add($"limit={pageSize.ToString(CultureInfo.InvariantCulture)}");
        }

        if (cursor is not null)
        {
            query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        if (includeCandidates)
        {
            query.Add("includeCandidates=true");
        }

        var suffix = query.Count == 0 ? string.Empty : "?" + string.Join("&", query);

        using var response = await owner.GetAsync($"/api/projects/{projectId}/timeline{suffix}");

        Assert.True(
            response.IsSuccessStatusCode,
            $"the timeline must be readable: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return new TimelineResult(
            [
                .. document.RootElement
                    .GetProperty("items")
                    .EnumerateArray()
                    .Select(item => new TimelineEntry(
                        item.GetProperty("id").GetString()!,
                        item.GetProperty("title").GetString()!,
                        DateTimeOffset.Parse(
                            item.GetProperty("occurredAt").GetString()!,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                        item.GetProperty("status").GetString()!)),
            ],
            document.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : document.RootElement.GetProperty("nextCursor").GetString());
    }

    private static async Task<long> ReadMilestoneRevisionAsync(OwnerClient owner, string milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId}");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("revision").GetInt64();
    }

    private static async Task<long> ReadProjectRevisionAsync(OwnerClient owner, string projectId)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId}");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("revision").GetInt64();
    }

    private static async Task<string?> ReadFirstFieldErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        if (!document.RootElement.TryGetProperty("fieldErrors", out var fields))
        {
            return null;
        }

        foreach (var field in fields.EnumerateObject())
        {
            return field.Name;
        }

        return null;
    }

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

    private sealed record TimelineEntry(string Id, string Title, DateTimeOffset OccurredAt, string Status);

    private sealed record TimelineResult(IReadOnlyList<TimelineEntry> Items, string? NextCursor);
}
