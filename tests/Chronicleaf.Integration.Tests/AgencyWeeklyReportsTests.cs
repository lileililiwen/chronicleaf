using System.Net;
using System.Net.Http.Json;
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
/// AGENCY-WEEKLY-REPORTS-001 and AGENCY-WEEKLY-REPORTS-002 against a real disposable PostgreSQL
/// started from a pinned Docker image, through the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// Nothing here is simulated. Clients, the project association, confirmed milestones, evidence
/// heads, report rows, the attribution hash, the approval stamp and the audit rows are all
/// produced by a real PostgreSQL server, and every claim is read back over the wire - a status
/// code, a header, a body - or over a raw connection as a row or a column. There is no EF
/// in-memory provider and no stubbed store: an unavailable container is a failure, never a
/// passing result.
/// <para>
/// The clock is injected and frozen, so every stored instant is deterministic. The screenshots
/// scenario runs on the capture package's own host with its declared seam - a recording
/// screenshot provider in place of the browser - and only the act of photographing a page is
/// the test's; the candidate write, the curation, the evidence append and the report are the
/// production code paths.
/// </para>
/// <para>
/// There is no provider behind a weekly report, so no scenario performs a generation call and
/// none could: the sections are confirmed milestones, evidence heads and the owner's own words
/// rendered through a deterministic template.
/// </para>
/// </remarks>
public sealed class AgencyWeeklyReportsTests(ChronicleafPostgresFixture postgres)
    : IClassFixture<ChronicleafPostgresFixture>
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The operator's setup token for these hosts, and the hash it is configured with.</summary>
    private const string AccessToken = "chronicleaf-operator-setup-token-0123456789abcdef";

    private static readonly string AccessTokenHash = Sha256.Sha256Hex(AccessToken);

    /// <summary>The single origin this installation accepts for mutations.</summary>
    private const string PublicOrigin = "https://chronicleaf.example";

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("9a3f1c5d-7b28-4e6a-8c19-4d5e6f708193");

    private const string Week = "2026-10-12";

    private const string TimeZone = "America/New_York";

    // ===========================================================================================
    // AGENCY-WEEKLY-REPORTS-001 Success: two projects' confirmed window milestones aggregate per
    // project with sources, while Client B's same-titled milestone stays out of the report.
    // ===========================================================================================

    [Fact]
    public async Task Weekly_001_Success_selected_confirmed_work_aggregates_per_project_with_sources()
    {
        Assert.True(postgres.IsRealPostgres, "the weekly-report scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("weekly_001_success");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientA = await CreateClientIdAsync(owner, "Client A", "First agency client.");
        var clientB = await CreateClientIdAsync(owner, "Client B", "Second agency client.");

        var ledger = await CreateProjectIdAsync(owner, "Ledger");
        var atlas = await CreateProjectIdAsync(owner, "Atlas");
        var other = await CreateProjectIdAsync(owner, "Other");
        await AssignAsync(owner, ledger, 1, clientA);
        await AssignAsync(owner, atlas, 1, clientA);
        await AssignAsync(owner, other, 1, clientB);

        // The same title under both clients, so a leak would read as B's words in A's report.
        var ledgerMilestone = await CreateMilestoneIdAsync(
            owner,
            ledger,
            "Shipped the ledger export",
            "2026-10-13T09:00:00Z",
            "Ledger's export landed.",
            ["The CSV dialect is untested."],
            ["Verify the CSV dialect."]);
        var atlasMilestone = await CreateMilestoneIdAsync(
            owner,
            atlas,
            "Shipped the atlas export",
            "2026-10-15T09:00:00Z",
            "Atlas's export landed.",
            [],
            []);
        await CreateMilestoneIdAsync(
            owner,
            other,
            "Shipped the ledger export",
            "2026-10-13T09:00:00Z",
            "B's private ledger note that must never appear.",
            [],
            []);

        // Outside the window in both directions: the report bounds what it covers.
        await CreateMilestoneIdAsync(owner, ledger, "Too early", "2026-10-12T03:59:00Z", "Before the window.", [], []);
        await CreateMilestoneIdAsync(owner, ledger, "Too late", "2026-10-19T04:00:00Z", "After the window.", [], []);

        var ledgerHead = await AppendManualEvidenceAsync(owner, ledgerMilestone, "Ledger's recorded note.", 0);
        var atlasHead = await AppendManualEvidenceAsync(owner, atlasMilestone, "Atlas's recorded note.", 0);

        using var generated = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports",
            new
            {
                projectIds = new[] { ledger, atlas },
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes = "Ledger pagination is half done.",
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));

        Assert.Equal(HttpStatusCode.Created, generated.StatusCode);
        Assert.Equal("\"1\"", generated.Headers.ETag?.ToString());

        var report = await ReadReportAsync(generated);
        Assert.Equal(clientA, report.ClientId);
        Assert.Equal(1, report.Revision);
        Assert.False(report.Approved);
        Assert.Equal("2026-10-12T04:00:00.0000000+00:00", report.WindowStartUtc);
        Assert.Equal("2026-10-19T04:00:00.0000000+00:00", report.WindowEndUtc);

        // The window, the projects and the per-project evidence bindings the body was rendered from.
        Assert.Contains("Shipped the ledger export", report.Body, StringComparison.Ordinal);
        Assert.Contains("Shipped the atlas export", report.Body, StringComparison.Ordinal);
        Assert.Contains("Ledger pagination is half done.", report.Body, StringComparison.Ordinal);
        Assert.Contains("Verify the CSV dialect.", report.Body, StringComparison.Ordinal);
        Assert.Contains("The CSV dialect is untested.", report.Body, StringComparison.Ordinal);
        Assert.Contains($"evidence bundle {ledgerHead.BundleId:D} revision {ledgerHead.Revision}", report.Body, StringComparison.Ordinal);
        Assert.Contains($"evidence bundle {atlasHead.BundleId:D} revision {atlasHead.Revision}", report.Body, StringComparison.Ordinal);

        // Nothing of Client B's: not its note, not its title's only distinguishing line.
        Assert.DoesNotContain("B's private ledger note", report.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Too early", report.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Too late", report.Body, StringComparison.Ordinal);

        // The read serves the same stored bytes, and the audit row names the report, not prose.
        using var reread = await owner.GetAsync($"/api/clients/{clientA}/weekly-reports/{report.Id}");
        Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
        Assert.Equal(report.Body, (await ReadReportAsync(reread)).Body);
        Assert.Contains("weekly-report.generated", await ReadAuditActionsAsync(database));
    }

    // ===========================================================================================
    // AGENCY-WEEKLY-REPORTS-001 Screenshots: a selected picture is listed per project and travels
    // into the approved HTML export; the Markdown export is the exact approved bytes.
    // ===========================================================================================

    [Fact]
    public async Task Weekly_001_Screenshots_selected_pictures_are_listed_and_exported()
    {
        Assert.True(postgres.IsRealPostgres, "the weekly-report scenarios require a real PostgreSQL container");

        var database = await postgres.CreateDatabaseAsync("weekly_001_shots");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        var mediaRoot = Path.Combine(Path.GetTempPath(), $"chronicleaf-weekly-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mediaRoot);

        var screenshots = new RecordingScreenshotProvider(new FixedTimeProvider(Clock));
        await using var host = CaptureApiHost.Start(database, new FixedTimeProvider(Clock), screenshots, mediaRoot);
        await ProvisionAsync(host);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientA = await CreateClientIdAsync(owner, "Client A", "First agency client.");
        var project = await CreateProjectIdAsync(owner, "Ledger");
        await AssignAsync(owner, project, 1, clientA);

        var milestone = await CreateMilestoneIdAsync(
            owner,
            project,
            "Shipped the ledger export",
            "2026-10-13T09:00:00Z",
            "Ledger's export landed.",
            [],
            []);
        await AppendManualEvidenceAsync(owner, milestone, "Ledger's recorded note.", 0);

        screenshots.AnswerWithPng(width: 1440, height: 900);
        var operationId = await RequestCaptureAsync(owner, milestone, "https://example.com/pricing", ["desktop"]);
        await RunJobAsync(host, operationId);
        Assert.Equal("succeeded", await ReadOperationStatusAsync(owner, operationId));

        var candidate = (await ReadCandidatesAsync(owner, milestone)).Single(picture => picture.Viewport == "desktop");
        await SelectAsync(owner, candidate.Id, candidate.Revision, "The pricing table after the redesign.", bundleRevision: 1);

        using var generated = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports",
            new
            {
                projectIds = new[] { project },
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));

        Assert.Equal(HttpStatusCode.Created, generated.StatusCode);
        var report = await ReadReportAsync(generated);
        Assert.Contains("The pricing table after the redesign.", report.Body, StringComparison.Ordinal);
        Assert.Contains($"media {candidate.MediaId:D}", report.Body, StringComparison.Ordinal);

        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports/{report.Id}/approve",
            new { },
            1));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var markdown = await owner.GetAsync(
            $"/api/clients/{clientA}/weekly-reports/{report.Id}/export?format=markdown");
        Assert.Equal(HttpStatusCode.OK, markdown.StatusCode);
        Assert.Equal(report.Body, await markdown.Content.ReadAsStringAsync());

        using var html = await owner.GetAsync(
            $"/api/clients/{clientA}/weekly-reports/{report.Id}/export?format=html");
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);

        // Pictures ride along, so the shared template packs a ZIP: the document, the
        // approval-covered picture under a generated path, and the checksum manifest.
        Assert.Equal("application/zip", html.Content.Headers.ContentType?.MediaType);
        var document = await ReadZipEntryAsync(html, "index.html");
        Assert.Contains("The pricing table after the redesign.", document, StringComparison.Ordinal);
        Assert.Contains("<img", document, StringComparison.Ordinal);
    }

    // ===========================================================================================
    // AGENCY-WEEKLY-REPORTS-001 Failure: a project outside the client is refused before any
    // render, and an unknown project is the delivered 404. Nothing is stored either way.
    // ===========================================================================================

    [Fact]
    public async Task Weekly_001_Failure_project_outside_client_is_refused_before_render()
    {
        Assert.True(postgres.IsRealPostgres, "the weekly-report scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("weekly_001_failure");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientA = await CreateClientIdAsync(owner, "Client A", "First agency client.");
        var clientB = await CreateClientIdAsync(owner, "Client B", "Second agency client.");

        var grouped = await CreateProjectIdAsync(owner, "Grouped");
        var foreign = await CreateProjectIdAsync(owner, "Foreign");
        var personal = await CreateProjectIdAsync(owner, "Personal");
        await AssignAsync(owner, grouped, 1, clientA);
        await AssignAsync(owner, foreign, 1, clientB);

        // Another client's project: 422 naming the refusal, and nothing stored.
        using var outside = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports",
            new
            {
                projectIds = new[] { grouped, foreign },
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outside.StatusCode);
        Assert.Equal("weekly_report_project_outside_client", await ReadCodeAsync(outside));

        // A personal project of the same scope is outside the client the same way.
        using var personalCall = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports",
            new
            {
                projectIds = new[] { personal },
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, personalCall.StatusCode);
        Assert.Equal("weekly_report_project_outside_client", await ReadCodeAsync(personalCall));

        // An invented id is the delivered 404, so the refusal cannot probe for scope data.
        using var invented = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports",
            new
            {
                projectIds = new[] { Guid.NewGuid().ToString("D") },
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));
        Assert.Equal(HttpStatusCode.NotFound, invented.StatusCode);

        Assert.Equal("0", await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT COUNT(*)::text FROM weekly_reports"));
    }

    // ===========================================================================================
    // AGENCY-WEEKLY-REPORTS-001 Boundary: an empty week truthfully reports no recorded
    // completion instead of inventing progress, and still approves and exports.
    // ===========================================================================================

    [Fact]
    public async Task Weekly_001_Boundary_empty_week_reports_no_recorded_completion()
    {
        Assert.True(postgres.IsRealPostgres, "the weekly-report scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("weekly_001_empty");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientA = await CreateClientIdAsync(owner, "Client A", "First agency client.");
        var project = await CreateProjectIdAsync(owner, "Ledger");
        await AssignAsync(owner, project, 1, clientA);

        // The only milestone lands outside the reported week.
        await CreateMilestoneIdAsync(owner, project, "Earlier work", "2026-10-01T09:00:00Z", "Not this week.", [], []);

        using var generated = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports",
            new
            {
                projectIds = new[] { project },
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));

        Assert.Equal(HttpStatusCode.Created, generated.StatusCode);
        var report = await ReadReportAsync(generated);
        Assert.Contains("No recorded completed milestones in this window.", report.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Earlier work", report.Body, StringComparison.Ordinal);

        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports/{report.Id}/approve",
            new { },
            1));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var exported = await owner.GetAsync(
            $"/api/clients/{clientA}/weekly-reports/{report.Id}/export?format=markdown");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Contains("No recorded completed milestones in this window.", await exported.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ===========================================================================================
    // AGENCY-WEEKLY-REPORTS-002 Success: a week crossing the spring-forward transition still
    // bounds exactly seven local calendar dates in UTC.
    // ===========================================================================================

    [Fact]
    public async Task Weekly_002_Success_dst_week_bounds_exactly_seven_local_dates()
    {
        Assert.True(postgres.IsRealPostgres, "the weekly-report scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("weekly_002_dst");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientA = await CreateClientIdAsync(owner, "Client A", "First agency client.");
        var project = await CreateProjectIdAsync(owner, "Ledger");
        await AssignAsync(owner, project, 1, clientA);

        // The 2026 spring-forward lands on Sunday 2026-03-08 inside this Monday-to-Monday week.
        await CreateMilestoneIdAsync(owner, project, "Sunday after the change", "2026-03-08T08:00:00Z", "04:00 EDT.", [], []);
        await CreateMilestoneIdAsync(owner, project, "Last minute of the week", "2026-03-09T03:59:00Z", "23:59 EDT.", [], []);
        await CreateMilestoneIdAsync(owner, project, "Monday midnight", "2026-03-09T04:00:00Z", "Outside.", [], []);
        await CreateMilestoneIdAsync(owner, project, "Before the week", "2026-03-02T04:59:00Z", "Outside.", [], []);

        using var generated = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports",
            new
            {
                projectIds = new[] { project },
                weekStart = "2026-03-02",
                timeZone = TimeZone,
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));

        Assert.Equal(HttpStatusCode.Created, generated.StatusCode);
        var report = await ReadReportAsync(generated);

        // EST opens the week, EDT closes it: the dates never move, the instants do.
        Assert.Equal("2026-03-02T05:00:00.0000000+00:00", report.WindowStartUtc);
        Assert.Equal("2026-03-09T04:00:00.0000000+00:00", report.WindowEndUtc);

        Assert.Contains("Sunday after the change", report.Body, StringComparison.Ordinal);
        Assert.Contains("Last minute of the week", report.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Monday midnight", report.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Before the week", report.Body, StringComparison.Ordinal);
    }

    // ===========================================================================================
    // AGENCY-WEEKLY-REPORTS-002 Failure: regrouping a covered project supersedes the stored
    // generation and its approval; a regeneration approves cleanly at the next revision.
    // ===========================================================================================

    [Fact]
    public async Task Weekly_002_Failure_regrouping_supersedes_generation_and_approval()
    {
        Assert.True(postgres.IsRealPostgres, "the weekly-report scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("weekly_002_regroup");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientA = await CreateClientIdAsync(owner, "Client A", "First agency client.");
        var clientB = await CreateClientIdAsync(owner, "Client B", "Second agency client.");

        var ledger = await CreateProjectIdAsync(owner, "Ledger");
        var atlas = await CreateProjectIdAsync(owner, "Atlas");
        await AssignAsync(owner, ledger, 1, clientA);
        await AssignAsync(owner, atlas, 1, clientA);

        await CreateMilestoneIdAsync(owner, ledger, "Shipped the export", "2026-10-13T09:00:00Z", "Landed.", [], []);

        var first = await GenerateAsync(owner, clientA, [ledger, atlas]);
        Assert.Equal(1, first.Revision);

        // The regrouping moves the membership generation both reports pin.
        using var moved = await owner.SendAsync(
            await owner.Patch($"/api/projects/{ledger}/client", 2, new { clientId = clientB }));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        // Approving the superseded generation is refused as stale attribution.
        using var staleApprove = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports/{first.Id}/approve",
            new { },
            1));
        Assert.Equal(HttpStatusCode.Conflict, staleApprove.StatusCode);
        Assert.Equal("approval_stale", await ReadCodeAsync(staleApprove));

        // Regenerating against the current attribution supersedes revision 1 and approves.
        var second = await GenerateAsync(owner, clientA, [atlas]);
        Assert.Equal(2, second.Revision);
        Assert.DoesNotContain("Shipped the export", second.Body, StringComparison.Ordinal);

        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports/{second.Id}/approve",
            new { },
            2));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var exported = await owner.GetAsync(
            $"/api/clients/{clientA}/weekly-reports/{second.Id}/export?format=markdown");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
    }

    // ===========================================================================================
    // AGENCY-WEEKLY-REPORTS-002 Boundary: an evidence append after approval supersedes the
    // export; a regeneration re-binds the new head and exports again.
    // ===========================================================================================

    [Fact]
    public async Task Weekly_002_Boundary_evidence_append_supersedes_the_export()
    {
        Assert.True(postgres.IsRealPostgres, "the weekly-report scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("weekly_002_evidence");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientA = await CreateClientIdAsync(owner, "Client A", "First agency client.");
        var project = await CreateProjectIdAsync(owner, "Ledger");
        await AssignAsync(owner, project, 1, clientA);

        var milestone = await CreateMilestoneIdAsync(
            owner,
            project,
            "Shipped the export",
            "2026-10-13T09:00:00Z",
            "Landed.",
            [],
            []);
        await AppendManualEvidenceAsync(owner, milestone, "First note.", 0);

        var first = await GenerateAsync(owner, clientA, [project]);

        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports/{first.Id}/approve",
            new { },
            1));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        // An evidence append moves the head the approval was bound to.
        await AppendManualEvidenceAsync(owner, milestone, "Second note.", 1);

        using var staleExport = await owner.GetAsync(
            $"/api/clients/{clientA}/weekly-reports/{first.Id}/export?format=markdown");
        Assert.Equal(HttpStatusCode.Conflict, staleExport.StatusCode);
        Assert.Equal("approval_stale", await ReadCodeAsync(staleExport));

        var second = await GenerateAsync(owner, clientA, [project]);
        Assert.Equal(2, second.Revision);
        Assert.NotEqual(first.BodyHash, second.BodyHash);

        using var reapproved = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports/{second.Id}/approve",
            new { },
            2));
        Assert.Equal(HttpStatusCode.OK, reapproved.StatusCode);

        using var exported = await owner.GetAsync(
            $"/api/clients/{clientA}/weekly-reports/{second.Id}/export?format=markdown");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Contains("revision 2", await exported.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ===========================================================================================
    // Export gates: no approval means approval_required, a missing revision means 428, a stale
    // name means 409, and only markdown and html leave the building.
    // ===========================================================================================

    [Fact]
    public async Task Weekly_export_gates_require_a_live_approval_and_a_known_format()
    {
        Assert.True(postgres.IsRealPostgres, "the weekly-report scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("weekly_gates");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientA = await CreateClientIdAsync(owner, "Client A", "First agency client.");
        var project = await CreateProjectIdAsync(owner, "Ledger");
        await AssignAsync(owner, project, 1, clientA);
        await CreateMilestoneIdAsync(owner, project, "Shipped the export", "2026-10-13T09:00:00Z", "Landed.", [], []);

        var report = await GenerateAsync(owner, clientA, [project]);

        // Never approved: the export names the missing decision, not the bytes.
        using var unapproved = await owner.GetAsync(
            $"/api/clients/{clientA}/weekly-reports/{report.Id}/export?format=markdown");
        Assert.Equal(HttpStatusCode.Conflict, unapproved.StatusCode);
        Assert.Equal("approval_required", await ReadCodeAsync(unapproved));

        // No revision presented: 428 before anything is read.
        using var missingMatch = await owner.SendAsync(
            await owner.PostWithoutBody($"/api/clients/{clientA}/weekly-reports/{report.Id}/approve"));
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingMatch.StatusCode);

        // A stale revision: 409 naming the stored head.
        using var stale = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports/{report.Id}/approve",
            new { },
            7));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports/{report.Id}/approve",
            new { },
            1));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        // An unknown format is refused even with a live approval, and there is no send route.
        using var badFormat = await owner.GetAsync(
            $"/api/clients/{clientA}/weekly-reports/{report.Id}/export?format=pdf");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badFormat.StatusCode);
        Assert.Equal("format", await ReadFieldErrorAsync(badFormat));

        using var noSendRoute = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports/{report.Id}/send",
            new { },
            1));
        Assert.Equal(HttpStatusCode.NotFound, noSendRoute.StatusCode);
    }

    // ===========================================================================================
    // Idempotency: the same key replays the stored report, and a key naming different inputs
    // is a conflict rather than a second report.
    // ===========================================================================================

    [Fact]
    public async Task Weekly_idempotency_key_replays_same_inputs_and_conflicts_on_drift()
    {
        Assert.True(postgres.IsRealPostgres, "the weekly-report scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("weekly_idempotency");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientA = await CreateClientIdAsync(owner, "Client A", "First agency client.");
        var project = await CreateProjectIdAsync(owner, "Ledger");
        await AssignAsync(owner, project, 1, clientA);
        await CreateMilestoneIdAsync(owner, project, "Shipped the export", "2026-10-13T09:00:00Z", "Landed.", [], []);

        var first = await GenerateWithKeyAsync(owner, clientA, [project], "weekly-acme-1", "Half done.");
        var replayed = await GenerateWithKeyAsync(owner, clientA, [project], "weekly-acme-1", "Half done.");
        Assert.Equal(first.Id, replayed.Id);
        Assert.Equal(1, replayed.Revision);

        // One row, not two: the replay stored nothing.
        Assert.Equal("1", await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT COUNT(*)::text FROM weekly_reports"));

        using var drifted = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports",
            new
            {
                projectIds = new[] { project },
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes = "Different words.",
                nextWeekNotes = (string?)null,
                idempotencyKey = "weekly-acme-1",
            }));
        Assert.Equal(HttpStatusCode.Conflict, drifted.StatusCode);
        Assert.Equal("weekly_report_idempotency_conflict", await ReadCodeAsync(drifted));
    }

    // ===========================================================================================
    // Boundaries: archived and foreign clients, malformed windows, missing sessions and missing
    // reports all answer without exposure.
    // ===========================================================================================

    [Fact]
    public async Task Weekly_boundaries_refuse_without_exposure()
    {
        Assert.True(postgres.IsRealPostgres, "the weekly-report scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("weekly_boundaries");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var owner = await OwnerClient.CreateAsync(host);

        var clientA = await CreateClientIdAsync(owner, "Client A", "First agency client.");
        var project = await CreateProjectIdAsync(owner, "Ledger");
        await AssignAsync(owner, project, 1, clientA);

        // An invented client is the delivered 404.
        using var invented = await owner.SendAsync(await owner.Post(
            $"/api/clients/{Guid.NewGuid():D}/weekly-reports",
            new
            {
                projectIds = new[] { project },
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));
        Assert.Equal(HttpStatusCode.NotFound, invented.StatusCode);

        // An archived client accepts no new reports, while its history stays readable.
        using var archived = await owner.SendAsync(
            await owner.Archive($"/api/clients/{clientA}", 1));
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        using var onArchived = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientA}/weekly-reports",
            new
            {
                projectIds = new[] { project },
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));
        Assert.Equal(HttpStatusCode.Conflict, onArchived.StatusCode);
        Assert.Equal("client_archived", await ReadCodeAsync(onArchived));

        var clientC = await CreateClientIdAsync(owner, "Client C", "Third agency client.");
        await AssignAsync(owner, project, 2, clientC);

        // A malformed week, an unsupported zone and an empty selection are 422s naming fields.
        using var badWeek = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientC}/weekly-reports",
            new
            {
                projectIds = new[] { project },
                weekStart = "12-10-2026",
                timeZone = TimeZone,
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badWeek.StatusCode);
        Assert.Equal("weekStart", await ReadFieldErrorAsync(badWeek));

        using var badZone = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientC}/weekly-reports",
            new
            {
                projectIds = new[] { project },
                weekStart = Week,
                timeZone = "Mars/Olympus",
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badZone.StatusCode);
        Assert.Equal("timeZone", await ReadFieldErrorAsync(badZone));

        using var empty = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientC}/weekly-reports",
            new
            {
                projectIds = Array.Empty<string>(),
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal("projectIds", await ReadFieldErrorAsync(empty));

        // A missing report is the delivered 404, and a missing session is 401.
        using var missing = await owner.GetAsync($"/api/clients/{clientC}/weekly-reports/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var anonymous = host.CreateClient();
        using var noSession = await anonymous.GetAsync($"/api/clients/{clientC}/weekly-reports/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
    }

    // ===========================================================================================
    // Migration: the schema carries this package's table with the enforced bounds, and readiness
    // requires it by position like every earlier package.
    // ===========================================================================================

    [Fact]
    public async Task Weekly_Migration_schema_carries_the_reports_table()
    {
        Assert.Contains(ChronicleafSchema.AgencyWeeklyReportsMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));

        // A database stopped at the Xiaohongshu schema is short of exactly this one.
        var missing = ChronicleafSchema.MissingMigrationIds(
            ChronicleafSchema.RequiredMigrationIds
                .Where(id => id != ChronicleafSchema.AgencyWeeklyReportsMigrationId)
                .ToArray());
        Assert.Equal([ChronicleafSchema.AgencyWeeklyReportsMigrationId], missing);

        var database = await postgres.CreateDatabaseAsync("weekly_migration");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        Assert.Equal(
            "10",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT character_maximum_length::text FROM information_schema.columns " +
                "WHERE table_name = 'weekly_reports' AND column_name = 'week_start'"));

        Assert.Equal(
            "PK_weekly_reports",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT constraint_name FROM information_schema.table_constraints " +
                "WHERE table_name = 'weekly_reports' AND constraint_type = 'PRIMARY KEY'"));

        Assert.Equal(
            "ux_weekly_reports_scope_id_client_id_week_start_revision",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT indexname FROM pg_indexes " +
                "WHERE tablename = 'weekly_reports' AND indexname = 'ux_weekly_reports_scope_id_client_id_week_start_revision'"));
    }

    // ===========================================================================================
    // The fixture.
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

    private static async Task ProvisionAsync(CaptureApiHost host)
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

    private static async Task<string> CreateClientIdAsync(OwnerClient owner, string name, string notes)
    {
        using var response = await owner.SendAsync(await owner.Post("/api/clients", new { name, notes }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadStringAsync(response, "id"))!;
    }

    private static async Task<string> CreateProjectIdAsync(OwnerClient owner, string name)
    {
        using var response = await owner.SendAsync(await owner.Post(
            "/api/projects",
            new { name, description = $"{name} project." }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadStringAsync(response, "id"))!;
    }

    private static async Task AssignAsync(OwnerClient owner, string projectId, long revision, string clientId)
    {
        using var response = await owner.SendAsync(
            await owner.Patch($"/api/projects/{projectId}/client", revision, new { clientId }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Guid> CreateMilestoneIdAsync(
        OwnerClient owner,
        string projectId,
        string title,
        string occurredAt,
        string notes,
        string[] knownLimitations,
        string[] nextSteps)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId}/milestones",
            new
            {
                title,
                version = "0.1.0",
                occurredAt,
                type = "feature",
                notes,
                knownLimitations,
                nextSteps,
            }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Guid.Parse((await ReadStringAsync(response, "id"))!);
    }

    private static async Task<EvidenceHead> AppendManualEvidenceAsync(
        OwnerClient owner,
        Guid milestoneId,
        string notes,
        long expectedRevision)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes, expectedRevision },
            expectedRevision));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        return new EvidenceHead(
            Guid.Parse(root.GetProperty("id").GetString()!),
            root.GetProperty("revision").GetInt64());
    }

    private static async Task<ReportView> GenerateAsync(OwnerClient owner, string clientId, string[] projectIds)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientId}/weekly-reports",
            new
            {
                projectIds,
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes = (string?)null,
                nextWeekNotes = (string?)null,
                idempotencyKey = (string?)null,
            }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadReportAsync(response);
    }

    private static async Task<ReportView> GenerateWithKeyAsync(
        OwnerClient owner,
        string clientId,
        string[] projectIds,
        string key,
        string inProgressNotes)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/clients/{clientId}/weekly-reports",
            new
            {
                projectIds,
                weekStart = Week,
                timeZone = TimeZone,
                inProgressNotes,
                nextWeekNotes = (string?)null,
                idempotencyKey = key,
            }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadReportAsync(response);
    }

    private static async Task<ReportView> ReadReportAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        return new ReportView(
            root.GetProperty("id").GetString()!,
            root.GetProperty("clientId").GetString()!,
            root.GetProperty("revision").GetInt64(),
            root.GetProperty("windowStartUtc").GetString()!,
            root.GetProperty("windowEndUtc").GetString()!,
            root.GetProperty("bodyMarkdown").GetString()!,
            root.GetProperty("bodyHash").GetString()!,
            root.GetProperty("approved").GetBoolean());
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<string> ReadFieldErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("fieldErrors").EnumerateObject().First().Name;
    }

    private static async Task<string?> ReadStringAsync(HttpResponseMessage response, string field)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(field).ValueKind == JsonValueKind.Null
            ? null
            : document.RootElement.GetProperty(field).GetString();
    }

    private static async Task<string> ReadAuditActionsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT string_agg(action, ',' ORDER BY id) FROM audit_events";
        return (await command.ExecuteScalarAsync())?.ToString() ?? string.Empty;
    }

    private static async Task<Guid> RequestCaptureAsync(OwnerClient owner, Guid milestoneId, string url, string[] viewports)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/captures",
            new
            {
                url,
                viewports,
                waitMs = 0,
                fullPage = false,
                expectedBundleRevision = (long?)null,
                idempotencyKey = $"capture-{Guid.NewGuid():N}",
            },
            0));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetString()!);
    }

    private static async Task RunJobAsync(CaptureApiHost host, Guid operationId)
    {
        await host.DispatchAsync();

        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var status = await ChronicleafPostgresFixture.ReadTextAsync(
                host.Database,
                $"SELECT status FROM operations WHERE id = '{operationId}'");

            if (status is "succeeded" or "failed" or "cancelled" or "superseded")
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"The capture operation {operationId} did not reach a terminal status.");
    }

    private static async Task<string> ReadOperationStatusAsync(OwnerClient owner, Guid operationId)
    {
        using var response = await owner.GetAsync($"/api/operations/{operationId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("status").GetString()!;
    }

    private static async Task<IReadOnlyList<CandidateView>> ReadCandidatesAsync(OwnerClient owner, Guid milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId:D}/screenshots");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return
        [
            .. document.RootElement.GetProperty("items").EnumerateArray().Select(item => new CandidateView(
                Guid.Parse(item.GetProperty("id").GetString()!),
                Guid.Parse(item.GetProperty("mediaId").GetString()!),
                item.GetProperty("viewport").GetString()!,
                item.GetProperty("revision").GetInt64()))
        ];
    }

    private static async Task SelectAsync(
        OwnerClient owner,
        Guid screenshotId,
        long screenshotRevision,
        string caption,
        long bundleRevision)
    {
        using var response = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{screenshotId:D}",
            screenshotRevision,
            new
            {
                status = "selected",
                caption,
                isPrimary = false,
                expectedRevision = screenshotRevision,
                expectedBundleRevision = bundleRevision,
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> ReadZipEntryAsync(HttpResponseMessage response, string entryName)
    {
        await using var packed = await response.Content.ReadAsStreamAsync();
        using var archive = new System.IO.Compression.ZipArchive(packed, System.IO.Compression.ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryName);
        Assert.NotNull(entry);
        await using var content = entry.Open();
        using var reader = new StreamReader(content);
        return await reader.ReadToEndAsync();
    }

    private sealed record ReportView(
        string Id,
        string ClientId,
        long Revision,
        string WindowStartUtc,
        string WindowEndUtc,
        string Body,
        string BodyHash,
        bool Approved);

    private sealed record EvidenceHead(Guid BundleId, long Revision);

    private sealed record CandidateView(Guid Id, Guid MediaId, string Viewport, long Revision);
}
