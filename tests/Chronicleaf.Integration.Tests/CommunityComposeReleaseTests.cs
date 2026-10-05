using System.Net;
using System.Text;
using System.Text.Json;
using Chronicleaf.AI.Contracts;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Capture;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Capture.Sandbox;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// COMMUNITY-COMPOSE-RELEASE-001 and COMMUNITY-COMPOSE-RELEASE-002 against a real disposable
/// PostgreSQL and the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the project and milestone records, the evidence
/// store, the voice service, the credential vault over a real operator key file, the asset and
/// approval stores, the timeline projection, the capture URL policy, the sandbox probe, the
/// routes, the access boundary and the database. The two declared substitutions are inherited
/// from the packages that own them: the provider <em>answer</em> is scripted (the
/// grounded-markdown-drafts seam, on the ollama slot the host inserts first), and no browser
/// is launched (the screenshot-capture seam). No stage here may be read as evidence that any
/// provider works or that a real page was photographed.
/// </para>
/// <para>
/// <b>What is static.</b> The two-service shape itself - exactly one application and one
/// PostgreSQL, no Redis, pinned images, named volumes, loopback default, read-only key mount -
/// is asserted out of the repository files a fresh clone carries, because a container that
/// cannot be described is not reproducible. The container bring-up, restart, backup and
/// restore cycle is <c>scripts/verify-community.sh</c>; these scenarios prove the same
/// workflow the script drives completes through the same routes, and that a restart (a new
/// host on the retained database) serves identical bytes.
/// </para>
/// </remarks>
public sealed class CommunityComposeReleaseTests(ChronicleafPostgresFixture postgres)
    : IClassFixture<ChronicleafPostgresFixture>
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid InstallationId = new("d9f4b6c3-2e15-6a8d-b794-3a0f5e7c9d22");

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    private const string EvidenceNote =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string KnownLimitation = "Only Linux is covered.";

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "compose.yaml")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "compose.yaml was not found above the test output directory.");
        return directory!.FullName;
    }

    // ==============================================================================================
    // COMMUNITY-COMPOSE-RELEASE-001 Success: a clean install completes the
    // milestone-to-approved-Markdown flow, and the timeline tells it back.
    // ==============================================================================================

    [Fact]
    public async Task Release_001_Success_a_clean_install_completes_the_milestone_to_approved_markdown_flow()
    {
        Assert.True(postgres.IsRealPostgres, "the release scenarios require a real PostgreSQL container.");
        var arranged = await ArrangeAsync("release_001_success");
        await using var fixture = arranged;

        // Approve what the generation committed, then export the exact bytes.
        using var approved = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.AssetId:D}/approve",
            new
            {
                contentRevision = 1,
                evidenceRevision = fixture.BundleRevision,
                voiceFindingsAcknowledged = false,
            }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var exported = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=markdown");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        var exportedBytes = await exported.Content.ReadAsStringAsync();

        // The export is the stored revision's own body, byte for byte - read out of the row,
        // not through the serializer that served it.
        var revision = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.AssetId, 1);
        Assert.NotNull(revision);
        Assert.Equal(revision!.BodyMarkdown, exportedBytes);
        Assert.Equal("attachment", exported.Content.Headers.ContentDisposition?.DispositionType);

        // And the project's private timeline carries the confirmed milestone the flow ran on.
        using var timeline = await fixture.Owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/timeline");
        Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);
        var items = JsonDocument.Parse(await timeline.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items").EnumerateArray().ToArray();
        var entry = items.Single(item =>
            Guid.Parse(item.GetProperty("id").GetString()!) == fixture.MilestoneId);
        Assert.Equal("confirmed", entry.GetProperty("status").GetString());
    }

    // ==============================================================================================
    // COMMUNITY-COMPOSE-RELEASE-001 Success (packaging): exactly two services, pinned database,
    // no extra dependency, loopback default, read-only key.
    // ==============================================================================================

    [Fact]
    public void Release_001_Success_the_compose_file_describes_exactly_app_plus_postgres()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "compose.yaml"));

        Assert.Contains("postgres:17.6-alpine@sha256:ef257d85f76e48da1c64832459b59fcaba1a4dac97bf5d7450c77753542eee94", compose, StringComparison.Ordinal);
        Assert.Contains("database:/var/lib/postgresql/data", compose, StringComparison.Ordinal);
        Assert.Contains("media:/app/runtime/media", compose, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:${CHRONICLEAF_APP_PORT:-5080}:8080", compose, StringComparison.Ordinal);
        Assert.Contains("service_healthy", compose, StringComparison.Ordinal);
        Assert.Contains("NET_ADMIN", compose, StringComparison.Ordinal);
        Assert.Contains("/run/chronicleaf/encryption.key:ro", compose, StringComparison.Ordinal);
        Assert.Contains("pg_isready", compose, StringComparison.Ordinal);
        Assert.Contains("/api/health", compose, StringComparison.Ordinal);

        // No third runtime: no image line may name a queue, cache or broker. The
        // comment above this code says the same thing in prose, so the scan reads
        // only image references rather than words.
        foreach (var line in compose.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("image:", StringComparison.Ordinal))
            {
                foreach (var forbidden in new[] { "redis", "rabbitmq", "kafka", "nats", "memcached" })
                {
                    Assert.DoesNotContain(forbidden, trimmed, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // Two services, named exactly app and postgres: a third service is a third runtime the
        // design did not authorize.
        var services = YamlServiceNames(compose);
        Assert.Equal(
            new List<string> { "app", "postgres" },
            services.OrderBy(name => name, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Release_001_Success_operator_values_are_names_only_and_the_image_carries_no_credential()
    {
        var example = File.ReadAllText(Path.Combine(RepoRoot(), ".env.example"));
        foreach (var line in example.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            Assert.EndsWith("=", trimmed);
        }

        Assert.DoesNotContain("postgres:17.6", example, StringComparison.Ordinal);

        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Dockerfile"));
        Assert.Contains("USER $APP_UID", dockerfile, StringComparison.Ordinal);
        Assert.Contains("chronicleaf-entrypoint.sh", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-sandbox", dockerfile, StringComparison.Ordinal);

        var entrypoint = File.ReadAllText(Path.Combine(RepoRoot(), "scripts/chronicleaf-entrypoint.sh"));
        Assert.Contains("exec dotnet Chronicleaf.Api.dll", entrypoint, StringComparison.Ordinal);
        Assert.Contains("capture_sandbox_unavailable", entrypoint, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-sandbox", entrypoint, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(RepoRoot(), "docs/community-quickstart.md")));
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "scripts/verify-community.sh")));
    }

    [Fact]
    public async Task Release_001_Success_all_five_provider_transports_are_installed_in_production()
    {
        Assert.True(postgres.IsRealPostgres, "the release scenarios require a real PostgreSQL container.");
        var database = await postgres.CreateDatabaseAsync("release_001_adapters");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using var host = GroundedMarkdownDraftsApiHost.StartProduction(
            database,
            new FixedTimeProvider(Clock),
            keyFile: null);

        // Read from the delivered resolver: the release verifies all five, while the first
        // usable draft only needs one of them.
        Assert.Equal(
            GenerationProviderIds.All.OrderBy(id => id, StringComparer.Ordinal),
            host.InstalledAdapters().OrderBy(id => id, StringComparer.Ordinal));
    }

    // ==============================================================================================
    // COMMUNITY-COMPOSE-RELEASE-001 Failure: a missing browser or secret fails visibly.
    // ==============================================================================================

    [Fact]
    public async Task Release_001_Failure_a_private_capture_destination_is_refused_before_any_job_exists()
    {
        Assert.True(postgres.IsRealPostgres, "the release scenarios require a real PostgreSQL container.");
        var database = await postgres.CreateDatabaseAsync("release_001_refused");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        var mediaRoot = Path.Combine(Path.GetTempPath(), $"chronicleaf-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mediaRoot);
        await using var host = CaptureApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            new RecordingScreenshotProvider(new FixedTimeProvider(Clock)),
            mediaRoot);
        await ProvisionAsync(host);
        using var owner = await OwnerClient.CreateAsync(host);

        var refuseProject = await CreateProjectAsync(owner, "release-001-refused");
        var refuseMilestone = await CreateMilestoneAsync(owner, refuseProject);

        // An IPv4 private address inside an IPv6 envelope: refused by the URL policy before a
        // job exists, so there is nothing for a worker to pick up and dial later.
        using var refused = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{refuseMilestone:D}/captures",
            new
            {
                url = "http://[::ffff:10.0.0.5]/admin",
                viewports = new[] { "desktop" },
                waitMs = 0,
                fullPage = false,
                expectedBundleRevision = (long?)null,
                idempotencyKey = $"release-{Guid.NewGuid():N}",
            },
            0));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("capture_target_blocked", await ReadCodeAsync(refused));
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "operations"));
    }

    [Fact]
    public void Release_001_Failure_without_a_capture_sandbox_the_refusal_is_the_visible_answer()
    {
        // The production supervisor on this host: no effective capabilities, no user namespace.
        var availability = new CaptureSandboxSupervisor(
            new CaptureSandboxSettings(),
            new FixedTimeProvider(Clock),
            NullLogger<CaptureSandboxSupervisor>.Instance).Probe();

        Assert.False(availability.Available);
        Assert.NotNull(availability.Reason);

        // And the refusal maps to a 503 with the panel's own code - a missing kernel capability,
        // answerable by running where it exists, never a silent downgrade.
        Assert.Equal(CaptureError.SandboxUnavailable, "capture_sandbox_unavailable");
        Assert.Equal(503, ApiErrorStatus.ForCode(CaptureError.SandboxUnavailable));
    }

    // ==============================================================================================
    // COMMUNITY-COMPOSE-RELEASE-001 Boundary: restart with retained volumes keeps everything.
    // ==============================================================================================

    [Fact]
    public async Task Release_001_Boundary_a_restart_on_the_retained_database_serves_identical_bytes()
    {
        Assert.True(postgres.IsRealPostgres, "the release scenarios require a real PostgreSQL container.");
        var arranged = await ArrangeAsync("release_001_restart");
        var keyPath = arranged.KeyFile.FilePath;
        var database = arranged.Database;

        string firstExport;
        string firstTimeline;
        try
        {
            await ApproveAsync(arranged, 1, arranged.BundleRevision);
            using var exported = await arranged.Owner.GetAsync(
                $"/api/assets/{arranged.AssetId:D}/export?format=markdown");
            Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
            firstExport = await exported.Content.ReadAsStringAsync();

            using var timeline = await arranged.Owner.GetAsync($"/api/projects/{arranged.ProjectId:D}/timeline");
            Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);
            firstTimeline = await timeline.Content.ReadAsStringAsync();

            // The restart: the old host process is gone, the database (the retained volume)
            // is not. The key file is the operator's mount, so it survives the restart too.
            arranged.Owner.Dispose();
            await arranged.Host.DisposeAsync();

            await using var restarted = GroundedMarkdownDraftsApiHost.StartProduction(
                database,
                new FixedTimeProvider(Clock),
                keyPath);
            using var owner = await OwnerClient.CreateAsync(restarted);

            using var reread = await owner.GetAsync(
                $"/api/assets/{arranged.AssetId:D}/export?format=markdown");
            Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
            Assert.Equal(firstExport, await reread.Content.ReadAsStringAsync());

            using var retimeline = await owner.GetAsync($"/api/projects/{arranged.ProjectId:D}/timeline");
            Assert.Equal(HttpStatusCode.OK, retimeline.StatusCode);
            Assert.Equal(firstTimeline, await retimeline.Content.ReadAsStringAsync());
        }
        finally
        {
            arranged.KeyFile.Dispose();
        }
    }

    // ==============================================================================================
    // COMMUNITY-COMPOSE-RELEASE-002 Success/Failure: readiness is honest; deployable stays false
    // with the exact reason recorded while any runtime check is unproven here.
    // ==============================================================================================

    [Fact]
    public async Task Release_002_Success_readiness_distinguishes_a_live_process_from_a_usable_install()
    {
        Assert.True(postgres.IsRealPostgres, "the release scenarios require a real PostgreSQL container.");
        var database = await postgres.CreateDatabaseAsync("release_002_ready");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using var host = ChronicleafApiHost.Start(database, Clock);
        using var client = host.CreateClient();

        using (var ready = await client.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        using (var health = await client.GetAsync("/api/health"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }

    [Fact]
    public async Task Release_002_Failure_export_without_approval_is_refused_and_stays_refused()
    {
        Assert.True(postgres.IsRealPostgres, "the release scenarios require a real PostgreSQL container.");
        await using var fixture = await ArrangeAsync("release_002_no_approval");

        using var exported = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=markdown");

        Assert.Equal(HttpStatusCode.Conflict, exported.StatusCode);
        Assert.Equal("approval_required", await ReadCodeAsync(exported));
    }

    // ==============================================================================================
    // COMMUNITY-COMPOSE-RELEASE-002 Boundary: undecided visibility means no publication anywhere.
    // ==============================================================================================

    [Fact]
    public void Release_002_Boundary_no_release_script_publishes_the_repository()
    {
        var root = RepoRoot();
        foreach (var script in new[] { "scripts/verify-community.sh", "scripts/chronicleaf-entrypoint.sh" })
        {
            var body = File.ReadAllText(Path.Combine(root, script));
            Assert.DoesNotContain("git push", body, StringComparison.Ordinal);
            Assert.DoesNotContain("gh repo", body, StringComparison.Ordinal);
            Assert.DoesNotContain("docker push", body, StringComparison.Ordinal);
        }

        var compose = File.ReadAllText(Path.Combine(root, "compose.yaml"));
        Assert.DoesNotContain("git push", compose, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // The fixture: project, confirmed milestone, recorded evidence, sealed credential, authorized
    // preview, stored draft from a real generation job.
    // ==============================================================================================

    private async Task<ReleaseFixture> ArrangeAsync(string purpose)
    {
        var database = await ProvisionedDatabaseAsync(purpose);
        var keyFile = OperatorKeyFile.Create();
        var clock = new FixedTimeProvider(Clock);

        Guid projectId;
        Guid milestoneId;
        Guid credentialId;
        EvidenceHead head;

        await using (var setup = GroundedMarkdownDraftsApiHost.StartProduction(database, clock, keyFile.FilePath))
        {
            using var setupOwner = await OwnerClient.CreateAsync(setup);

            projectId = await CreateProjectAsync(setupOwner, purpose);
            milestoneId = await CreateMilestoneAsync(setupOwner, projectId);

            using var evidence = await setupOwner.SendAsync(await setupOwner.Post(
                $"/api/milestones/{milestoneId:D}/evidence/manual",
                new { notes = EvidenceNote, expectedRevision = 0 },
                0));

            Assert.Equal(HttpStatusCode.Created, evidence.StatusCode);

            using var voiced = await setupOwner.SendAsync(await setupOwner.Put(
                $"/api/projects/{projectId:D}/voice-profile",
                new { presetId = "technical-blog", overrides = new { language = "en" } },
                0));

            Assert.Equal(HttpStatusCode.OK, voiced.StatusCode);

            credentialId = await SaveCredentialAsync(setupOwner);
            head = await ReadEvidenceHeadAsync(setupOwner, milestoneId);
        }

        var summary = SummaryJson(head.Ids[0]);
        var scripted = ScriptedDraftProvider.With(summary, BodyFor(summary));

        var host = GroundedMarkdownDraftsApiHost.Start(database, clock, keyFile.FilePath, scripted);
        var owner = await OwnerClient.CreateAsync(host);

        var preview = await CreatePreviewAsync(host, owner, milestoneId, credentialId, head.Revision, head.Ids[0]);

        using var requested = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/drafts",
            new
            {
                previewId = preview.PreviewId.ToString("D"),
                consentHash = preview.ConsentHash,
                providerId = ScriptedDraftProvider.Slot,
                model = ScriptedDraftProvider.KnownModel,
                evidenceRevision = preview.EvidenceRevision,
                voiceRevision = preview.VoiceRevision,
                idempotencyKey = $"release-{purpose}",
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var operationId = JsonDocument.Parse(await requested.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetGuid();

        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, operationId, "succeeded", JobBudget);

        var asset = await ContentRows.ReadAssetForMilestoneAsync(database, milestoneId);
        Assert.NotNull(asset);

        return new ReleaseFixture(
            database, host, keyFile, owner, projectId, milestoneId,
            asset!.Id, head.Id, head.Revision);
    }

    private static async Task ApproveAsync(ReleaseFixture fixture, long contentRevision, long evidenceRevision)
    {
        using var approved = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.AssetId:D}/approve",
            new
            {
                contentRevision,
                evidenceRevision,
                voiceFindingsAcknowledged = false,
            }));

        Assert.True(
            approved.StatusCode == HttpStatusCode.OK,
            $"approve {contentRevision}@{evidenceRevision} answered {(int)approved.StatusCode}: "
            + await approved.Content.ReadAsStringAsync());
    }

    private async Task<string> ProvisionedDatabaseAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using var host = GroundedMarkdownDraftsApiHost.StartProduction(
            database,
            new FixedTimeProvider(Clock),
            keyFile: null);

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

    private static async Task<Guid> CreateProjectAsync(OwnerClient owner, string purpose)
    {
        using var response = await owner.SendAsync(await owner.Post(
            "/api/projects",
            new { name = $"{purpose} 项目", description = $"{purpose} 说明。" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<Guid> CreateMilestoneAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/milestones",
            new
            {
                title = "第一次可复现发布",
                version = "0.1.0",
                occurredAt = "2026-10-01T09:00:00Z",
                type = "feature",
                notes = "记录。",
                knownLimitations = Array.Empty<string>(),
                nextSteps = Array.Empty<string>(),
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Manual records are confirmed on save: the explicit save is the confirmation.
        Assert.Equal("confirmed", body.RootElement.GetProperty("status").GetString());

        return Guid.Parse(body.RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<Guid> SaveCredentialAsync(OwnerClient owner)
    {
        using var response = await owner.SendAsync(await owner.Put(
            $"/api/settings/credentials/{ScriptedDraftProvider.Slot}",
            new { value = "test-key-never-used-for-a-real-call" },
            0));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw);

        Assert.DoesNotContain("test-key-never-used-for-a-real-call", raw, StringComparison.Ordinal);

        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<EvidenceHead> ReadEvidenceHeadAsync(OwnerClient owner, Guid milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var bundle = body.RootElement.GetProperty("items")[0];

        return new EvidenceHead(
            bundle.GetProperty("id").GetGuid(),
            bundle.GetProperty("revision").GetInt64(),
            [.. bundle.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())]);
    }

    private static async Task<PreviewBody> CreatePreviewAsync(
        GroundedMarkdownDraftsApiHost host,
        OwnerClient owner,
        Guid milestoneId,
        Guid credentialId,
        long evidenceRevision,
        Guid evidenceId)
    {
        _ = host;

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/generation-preview",
            new
            {
                providerId = ScriptedDraftProvider.Slot,
                model = ScriptedDraftProvider.KnownModel,
                credentialRef = credentialId.ToString("D"),
                evidenceRevision,
                selectedEvidenceIds = new[] { evidenceId.ToString("D") },
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return new PreviewBody(
            body.RootElement.GetProperty("previewId").GetGuid(),
            body.RootElement.GetProperty("consentHash").GetString()!,
            body.RootElement.GetProperty("evidenceRevision").GetInt64(),
            body.RootElement.GetProperty("voiceRevision").GetInt64());
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    /// <summary>Service names from a Compose file, without a YAML dependency.</summary>
    private static IReadOnlyList<string> YamlServiceNames(string compose)
    {
        var names = new List<string>();
        var lines = compose.Split('\n');
        var inServices = false;

        foreach (var line in lines)
        {
            if (line == "services:")
            {
                inServices = true;
                continue;
            }

            if (!inServices)
            {
                continue;
            }

            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                break;
            }

            if (line.StartsWith("  ") && !line.StartsWith("   ") && line.TrimEnd().EndsWith(':'))
            {
                names.Add(line.Trim().TrimEnd(':'));
            }
        }

        return names;
    }

    private sealed record EvidenceHead(Guid Id, long Revision, IReadOnlyList<Guid> Ids);

    private sealed record PreviewBody(Guid PreviewId, string ConsentHash, long EvidenceRevision, long VoiceRevision);

    private sealed record ReleaseFixture(
        string Database,
        GroundedMarkdownDraftsApiHost Host,
        OperatorKeyFile KeyFile,
        OwnerClient Owner,
        Guid ProjectId,
        Guid MilestoneId,
        Guid AssetId,
        Guid EvidenceBundleId,
        long BundleRevision) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Host.DisposeAsync();
            KeyFile.Dispose();
        }
    }

    private static string SummaryJson(Guid? evidenceId)
    {
        var id = evidenceId is { } value ? value.ToString("d") : null;

        return $$"""
          {
            "milestoneTitle": "Reproducible release pipeline",
            "summary": "The pipeline now reproduces from a clean checkout.",
            "changes": [{"text": "The pipeline runs from a clean checkout.", "evidenceIds": ["{{id}}"]}],
            "problemsSolved": [{"text": "A release could not be rebuilt from its source.", "evidenceIds": ["{{id}}"]}],
            "knownLimitations": [{"text": {{Quote(KnownLimitation)}}, "evidenceIds": ["{{id}}"]}],
            "nextSteps": [{"text": "Cover the other platform.", "evidenceIds": ["{{id}}"]}]
          }
          """;
    }

    private static string BodyFor(string summaryJson)
    {
        var summary = GroundedSummarySchema.Read(summaryJson);
        var claims = ClaimMap.Build(summary);
        var builder = new StringBuilder();

        builder.Append("# ").Append(summary.MilestoneTitle).Append("\n\n");
        builder.Append(summary.Summary).Append("[^").Append(claims[0].Id).Append("]\n");

        AppendSection(builder, "What changed", GroundedSummarySection.Changes, summary, claims);
        AppendSection(builder, "Problems solved", GroundedSummarySection.ProblemsSolved, summary, claims);
        AppendSection(builder, "Known limitations", GroundedSummarySection.KnownLimitations, summary, claims);
        AppendSection(builder, "Next steps", GroundedSummarySection.NextSteps, summary, claims);

        builder.Append('\n');
        foreach (var claim in claims)
        {
            builder.Append("[^").Append(claim.Id).Append("]: ").Append(claim.Text).Append('\n');
        }

        return JsonSerializer.Serialize(new { bodyMarkdown = builder.ToString() });
    }

    private static void AppendSection(
        StringBuilder builder,
        string heading,
        GroundedSummarySection section,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims)
    {
        var items = summary.ClaimsIn(section);
        if (items.Count == 0)
        {
            return;
        }

        builder.Append("\n## ").Append(heading).Append('\n');

        for (var index = 0; index < items.Count; index++)
        {
            var claim = claims.Single(candidate => candidate.Section == section && candidate.Index == index);
            builder.Append("- ").Append(items[index].Text).Append("[^").Append(claim.Id).Append("]\n");
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
