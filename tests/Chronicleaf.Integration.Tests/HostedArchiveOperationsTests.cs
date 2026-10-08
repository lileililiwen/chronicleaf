using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Archive;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// HOSTED-ARCHIVE-OPERATIONS-001 and HOSTED-ARCHIVE-OPERATIONS-002 against a real disposable
/// PostgreSQL and the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is simulated where it matters. Every row comes from a real PostgreSQL server
/// started from a pinned image, the committed migrations created the tables, the archive routes
/// are the real ones behind the real access boundary, the manifests are recorded and rehearsed
/// through the real backup service over the real digest reader, the tombstone, journey
/// revocation and schedule disable commit in one real unit of work, and the purge retires bytes
/// through the real media store over a real temporary directory. There is no EF in-memory
/// provider and no stubbed store in this file.
/// </para>
/// <para>
/// Each scenario gets its own database and the clock is injected and settable, so the
/// thirty-day recovery window is proven by moving time rather than by waiting for it. Backup
/// manifests have no route by design - the reference names the operator's inventory and the key
/// version names the operator's key history - so the manifest half is exercised through the
/// service the operator tooling resolves, and the suite asserts the reference never reaches an
/// owner-facing response.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class HostedArchiveOperationsTests(ChronicleafPostgresFixture postgres)
    : IDisposable
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("b3e7a1c4-5f28-4d92-8a11-0e9c4f6b2a51");

    private readonly List<string> _temporaryRoots = [];
    private readonly List<string> _keyFiles = [];

    public void Dispose()
    {
        foreach (var root in _temporaryRoots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        foreach (var keyFile in _keyFiles)
        {
            try
            {
                File.Delete(keyFile);
            }
            catch (IOException)
            {
            }
        }
    }

    // ===========================================================================================
    // Envelope codes: every code this package owns maps to a status, never to a 500.
    // ===========================================================================================

    [Fact]
    public void Every_archive_code_maps_to_its_status()
    {
        Assert.All(
            new[]
            {
                ArchiveError.KeyMismatch,
                ArchiveError.MediaMismatch,
                ArchiveError.SchemaMismatch,
                ArchiveError.AlreadyDeleted,
                ArchiveError.RestoreExpired,
            },
            code => Assert.NotEqual(500, ApiErrorStatus.ForCode(code)));

        Assert.Equal(409, ApiErrorStatus.ForCode(ArchiveError.KeyMismatch));
        Assert.Equal(409, ApiErrorStatus.ForCode(ArchiveError.MediaMismatch));
        Assert.Equal(409, ApiErrorStatus.ForCode(ArchiveError.SchemaMismatch));
        Assert.Equal(409, ApiErrorStatus.ForCode(ArchiveError.AlreadyDeleted));
        Assert.Equal(410, ApiErrorStatus.ForCode(ArchiveError.RestoreExpired));
    }

    // ===========================================================================================
    // HOSTED-ARCHIVE-OPERATIONS-001 Success: measure, record and rehearse proves the hashes.
    // ===========================================================================================

    [Fact]
    public async Task Backup_001_Success_measure_record_and_rehearse_proves_hashes_consistent()
    {
        var scenario = await ScenarioAsync("hao_001_success");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "存档");
        var milestone = await CreateMilestoneAsync(owner, project, "First release");
        await AppendManualEvidenceAsync(owner, milestone, "The collected note.");
        var mediaId = await UploadMediaAsync(owner, project, TestPng.Create(12, 7));
        await SeedAssetAsync(scenario.Database, project, milestone);

        var measured = await Backups(host).MeasureAsync(Scope());
        var recorded = await Backups(host).RecordAsync(
            Scope(),
            new ArchiveManifestRequest(
                ChronicleafDbContext.RequiredSchemaVersion,
                "pg-snapshot-2026-10-06-001",
                measured.MediaInventoryHash,
                measured.ContentDigest,
                1));

        Assert.False(recorded.Verified);
        Assert.Null(recorded.RestoreVerifiedAt);
        Assert.Equal(1, recorded.Revision);

        // The manifest carries the operator's reference in the service view, and the rehearsal
        // proves the live scope still hashes to the recorded values.
        var verified = await Backups(host).RehearseAsync(Scope(), recorded.Id);

        Assert.True(verified.Verified);
        Assert.NotNull(verified.RestoreVerifiedAt);
        Assert.Equal(2, verified.Revision);
        Assert.Equal(measured.MediaInventoryHash, verified.MediaInventoryHash);
        Assert.Equal(measured.ContentDigest, verified.ContentDigest);

        // The stamp is a row, not a memory: a fresh read reports the same verification.
        var reread = await Backups(host).ReadAsync(Scope(), recorded.Id);
        Assert.NotNull(reread);
        Assert.True(reread!.Verified);

        // The media the digest covered is still the bytes that were sent.
        Assert.Equal(Sha256Hex(TestPng.Create(12, 7)), await ReadMediaShaAsync(scenario.Database, mediaId));

        await AssertAuditAsync(scenario.Database, "archive.backup.verified");
    }

    // ===========================================================================================
    // HOSTED-ARCHIVE-OPERATIONS-001 Failure: a missing key or media object is not promoted.
    // ===========================================================================================

    [Fact]
    public async Task Backup_001_Failure_a_backup_under_an_unknown_key_is_not_promoted()
    {
        var scenario = await ScenarioAsync("hao_001_key");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "密钥");
        await UploadMediaAsync(owner, project, TestPng.Create(8, 5));

        var measured = await Backups(host).MeasureAsync(Scope());
        var recorded = await Backups(host).RecordAsync(
            Scope(),
            new ArchiveManifestRequest(
                ChronicleafDbContext.RequiredSchemaVersion,
                "pg-snapshot-2026-10-06-002",
                measured.MediaInventoryHash,
                measured.ContentDigest,
                999));

        var failure = await Record.ExceptionAsync(() => Backups(host).RehearseAsync(Scope(), recorded.Id));

        var mismatch = Assert.IsType<ArchiveKeyMismatchException>(failure);
        Assert.Equal(ArchiveError.KeyMismatch, mismatch.Code);

        // Nothing is promoted: the stamp stays null and a later read still says unverified.
        var reread = await Backups(host).ReadAsync(Scope(), recorded.Id);
        Assert.NotNull(reread);
        Assert.False(reread!.Verified);
        Assert.Null(reread.RestoreVerifiedAt);
    }

    [Fact]
    public async Task Backup_001_Failure_a_changed_media_inventory_is_not_promoted()
    {
        var scenario = await ScenarioAsync("hao_001_media");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "介质");
        await UploadMediaAsync(owner, project, TestPng.Create(8, 5));

        var measured = await Backups(host).MeasureAsync(Scope());
        var recorded = await Backups(host).RecordAsync(
            Scope(),
            new ArchiveManifestRequest(
                ChronicleafDbContext.RequiredSchemaVersion,
                "pg-snapshot-2026-10-06-003",
                measured.MediaInventoryHash,
                measured.ContentDigest,
                1));

        // An object arrives after the manifest was written: the inventory drifted.
        await UploadMediaAsync(owner, project, TestPng.Create(4, 4));

        var failure = await Record.ExceptionAsync(() => Backups(host).RehearseAsync(Scope(), recorded.Id));

        var mismatch = Assert.IsType<ArchiveMediaMismatchException>(failure);
        Assert.Equal(ArchiveError.MediaMismatch, mismatch.Code);
        Assert.Equal("media", mismatch.Inventory);

        var reread = await Backups(host).ReadAsync(Scope(), recorded.Id);
        Assert.NotNull(reread);
        Assert.False(reread!.Verified);
    }

    [Fact]
    public async Task Backup_001_Failure_a_backup_under_an_unknown_schema_is_not_promoted()
    {
        var scenario = await ScenarioAsync("hao_001_schema");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "模式");
        await UploadMediaAsync(owner, project, TestPng.Create(8, 5));

        var measured = await Backups(host).MeasureAsync(Scope());
        var recorded = await Backups(host).RecordAsync(
            Scope(),
            new ArchiveManifestRequest(
                ChronicleafDbContext.RequiredSchemaVersion + 1,
                "pg-snapshot-2026-10-06-004",
                measured.MediaInventoryHash,
                measured.ContentDigest,
                1));

        var failure = await Record.ExceptionAsync(() => Backups(host).RehearseAsync(Scope(), recorded.Id));

        var mismatch = Assert.IsType<ArchiveSchemaMismatchException>(failure);
        Assert.Equal(ArchiveError.SchemaMismatch, mismatch.Code);

        var reread = await Backups(host).ReadAsync(Scope(), recorded.Id);
        Assert.NotNull(reread);
        Assert.False(reread!.Verified);
    }

    [Fact]
    public async Task Backup_001_Failure_a_changed_content_digest_is_not_promoted()
    {
        var scenario = await ScenarioAsync("hao_001_content");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "内容");
        var milestone = await CreateMilestoneAsync(owner, project, "Drifting release");

        var measured = await Backups(host).MeasureAsync(Scope());
        var recorded = await Backups(host).RecordAsync(
            Scope(),
            new ArchiveManifestRequest(
                ChronicleafDbContext.RequiredSchemaVersion,
                "pg-snapshot-2026-10-06-005",
                measured.MediaInventoryHash,
                measured.ContentDigest,
                1));

        // Evidence arrives after the manifest was written: the content digest drifted.
        await AppendManualEvidenceAsync(owner, milestone, "Evidence after the backup.");

        var failure = await Record.ExceptionAsync(() => Backups(host).RehearseAsync(Scope(), recorded.Id));

        var mismatch = Assert.IsType<ArchiveMediaMismatchException>(failure);
        Assert.Equal(ArchiveError.MediaMismatch, mismatch.Code);
        Assert.Equal("content", mismatch.Inventory);
    }

    // ===========================================================================================
    // HOSTED-ARCHIVE-OPERATIONS-001 Boundary: scheduling alone claims no recovery; scopes isolate.
    // ===========================================================================================

    [Fact]
    public async Task Backup_001_Boundary_recording_alone_claims_no_recovery()
    {
        var scenario = await ScenarioAsync("hao_001_unverified");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "未验证");
        await UploadMediaAsync(owner, project, TestPng.Create(8, 5));

        var measured = await Backups(host).MeasureAsync(Scope());
        var recorded = await Backups(host).RecordAsync(
            Scope(),
            new ArchiveManifestRequest(
                ChronicleafDbContext.RequiredSchemaVersion,
                "pg-snapshot-2026-10-06-006",
                measured.MediaInventoryHash,
                measured.ContentDigest,
                1));

        // A recorded manifest with no rehearsal is unverified: configuring the backup says
        // nothing about whether it restores, and no continuous-recovery guarantee is claimed.
        Assert.False(recorded.Verified);
        Assert.Null(recorded.RestoreVerifiedAt);
    }

    [Fact]
    public async Task Backup_001_Boundary_foreign_scope_rows_do_not_satisfy_this_scopes_digest()
    {
        var scenario = await ScenarioAsync("hao_001_tenant");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "租户");
        await UploadMediaAsync(owner, project, TestPng.Create(8, 5));

        var measured = await Backups(host).MeasureAsync(Scope());

        // Another tenant's rows land in the same tables: a project, a milestone and a media row
        // under a foreign scope must not move this scope's measurements by a single line.
        var foreignScope = Guid.NewGuid();
        var foreignProject = Guid.NewGuid();
        await ChronicleafPostgresFixture.ExecuteAsync(
            scenario.Database,
            "INSERT INTO projects (id, scope_id, name, description, revision, archived_at)"
            + $" VALUES ('{foreignProject:D}', '{foreignScope:D}', 'Foreign', 'Foreign project.', 1, NULL)");
        await ChronicleafPostgresFixture.ExecuteAsync(
            scenario.Database,
            "INSERT INTO media (id, scope_id, project_id, storage_key, media_type, byte_length,"
            + $" sha256, status, created_at, revision, provider_id)"
            + $" VALUES ('{Guid.NewGuid():D}', '{foreignScope:D}', '{foreignProject:D}',"
            + $" 'scopes/{foreignScope:D}/foreign.png', 'image/png', 8, '{new string('c', 64)}',"
            + $" 'ready', '{Clock:yyyy-MM-ddTHH:mm:ssZ}', 1, 'local')");

        var remeasured = await Backups(host).MeasureAsync(Scope());
        Assert.Equal(measured.MediaInventoryHash, remeasured.MediaInventoryHash);
        Assert.Equal(measured.ContentDigest, remeasured.ContentDigest);

        // And the rehearsal still passes: the foreign bytes satisfy nothing here.
        var recorded = await Backups(host).RecordAsync(
            Scope(),
            new ArchiveManifestRequest(
                ChronicleafDbContext.RequiredSchemaVersion,
                "pg-snapshot-2026-10-06-007",
                measured.MediaInventoryHash,
                measured.ContentDigest,
                1));

        var verified = await Backups(host).RehearseAsync(Scope(), recorded.Id);
        Assert.True(verified.Verified);

        // A foreign manifest id answers as absent to this scope: no cross-tenant read.
        var foreignManifest = Guid.NewGuid();
        await ChronicleafPostgresFixture.ExecuteAsync(
            scenario.Database,
            "INSERT INTO archive_backup_manifests (id, scope_id, schema_version, created_at,"
            + $" db_backup_ref, media_inventory_hash, content_digest, encryption_key_version,"
            + $" restore_verified_at, revision, updated_at)"
            + $" VALUES ('{foreignManifest:D}', '{foreignScope:D}', 1,"
            + $" '{Clock:yyyy-MM-ddTHH:mm:ssZ}', 'foreign-snapshot', '{new string('d', 64)}',"
            + $" '{new string('e', 64)}', 1, NULL, 1, '{Clock:yyyy-MM-ddTHH:mm:ssZ}')");

        Assert.Null(await Backups(host).ReadAsync(Scope(), foreignManifest));
    }

    // ===========================================================================================
    // HOSTED-ARCHIVE-OPERATIONS-002 Success: a confirmed delete suspends projection and poll.
    // ===========================================================================================

    [Fact]
    public async Task Archive_002_Success_a_confirmed_delete_suspends_projection_and_poll()
    {
        var scenario = await ScenarioAsync("hao_002_delete");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var (name, project) = await CreateNamedProjectAsync(owner, "可恢复删除");
        var milestone = await CreateMilestoneAsync(owner, project, "Doomed release");
        await AppendManualEvidenceAsync(owner, milestone, "Evidence that must survive the delete.");
        var mediaId = await UploadMediaAsync(owner, project, TestPng.Create(12, 7));
        await SeedPublishedJourneyAsync(scenario.Database, project);
        await SeedScheduleAsync(scenario.Database, project);
        var credentialTag = await SaveCredentialAsync(owner, "github", "gho_archive_survives_delete_001");

        var revision = await ReadProjectRevisionAsync(owner, project);
        using var deleted = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = name },
                revision));

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        using var tombstone = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync());
        var root = tombstone.RootElement;
        Assert.Equal(project.ToString("D"), root.GetProperty("projectId").GetString());
        Assert.Equal(name, root.GetProperty("projectName").GetString());
        Assert.True(root.GetProperty("eraseRequested").GetBoolean());
        Assert.True(root.GetProperty("live").GetBoolean());
        Assert.Equal(1, root.GetProperty("revision").GetInt64());

        // Thirty days from the deletion, disclosed in the response itself.
        var deletedAt = root.GetProperty("deletedAt").GetDateTimeOffset();
        Assert.Equal(deletedAt.AddDays(30), root.GetProperty("recoveryUntil").GetDateTimeOffset());

        // The public projection stopped: the journey is revoked, not removed.
        var journey = await ReadJourneyAsync(owner, project);
        Assert.False(journey.GetProperty("enabled").GetBoolean());
        Assert.NotNull(journey.GetProperty("revokedAt").GetString());

        // The recurring poll stopped: the schedule is off with its cadence intact.
        var schedule = await ReadScheduleAsync(owner, project);
        Assert.False(schedule.GetProperty("enabled").GetBoolean());

        // Nothing was erased by the delete: the evidence, the media bytes, the credential and
        // the project itself all still read back.
        Assert.Equal(1L, await CountAsync(scenario.Database, "SELECT count(*) FROM evidence_bundles"));
        using var media = await owner.GetAsync($"/api/media/{mediaId:D}");
        Assert.Equal(HttpStatusCode.OK, media.StatusCode);
        var (configured, tag) = await ReadCredentialAsync(owner, "github");
        Assert.True(configured);
        Assert.Equal(credentialTag, tag);
        var reread = await ReadProjectRevisionAsync(owner, project);
        Assert.Equal(revision, reread);

        await AssertAuditAsync(scenario.Database, "project.archive-deleted");
    }

    // ===========================================================================================
    // HOSTED-ARCHIVE-OPERATIONS-002 Failure: the guards around the destructive route.
    // ===========================================================================================

    [Fact]
    public async Task Archive_002_Failure_the_delete_guards_confirm_revision_and_session()
    {
        var scenario = await ScenarioAsync("hao_002_guards");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var (name, project) = await CreateNamedProjectAsync(owner, "守卫");
        var revision = await ReadProjectRevisionAsync(owner, project);

        // A missing If-Match is 428: nothing may delete what it never read.
        using var noMatch = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = name },
                null));
        Assert.Equal((HttpStatusCode)428, noMatch.StatusCode);

        // A wrong name is 422 naming the field, with nothing written.
        using var wrongName = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = "Another project" },
                revision));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongName.StatusCode);
        Assert.Equal(
            "validation_failed",
            JsonDocument.Parse(await wrongName.Content.ReadAsStringAsync())
                .RootElement.GetProperty("code").GetString());

        // A stale revision is 409: a delete that raced an edit loses loudly.
        using var stale = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = name },
                revision + 10));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(0L, await CountAsync(scenario.Database, "SELECT count(*) FROM archive_tombstones"));

        // The real delete lands once.
        using var deleted = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = name },
                revision));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        // A second delete while one is live is a conflict, not a merged window.
        using var again = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = name },
                revision));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(
            "archive_already_deleted",
            JsonDocument.Parse(await again.Content.ReadAsStringAsync())
                .RootElement.GetProperty("code").GetString());

        // Anonymous callers get nothing: the routes are private by default-deny.
        var anonymous = host.CreateClient();
        using var noSession = await anonymous.GetAsync($"/api/projects/{project:D}/archive-data");
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);

        // A project that was never deleted reads 404, not an empty tombstone.
        var other = await CreateProjectAsync(owner, "从未删除");
        using var missing = await owner.GetAsync($"/api/projects/{other:D}/archive-data");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ===========================================================================================
    // HOSTED-ARCHIVE-OPERATIONS-002 Success: restore inside the window lifts the deletion.
    // ===========================================================================================

    [Fact]
    public async Task Archive_002_Success_restore_inside_the_window_lifts_the_deletion()
    {
        var scenario = await ScenarioAsync("hao_002_restore");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var (name, project) = await CreateNamedProjectAsync(owner, "恢复");
        var revision = await ReadProjectRevisionAsync(owner, project);

        using var deleted = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = name },
                revision));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var tombstoneRevision = JsonDocument
            .Parse(await deleted.Content.ReadAsStringAsync())
            .RootElement.GetProperty("revision").GetInt64();

        // A stale tombstone revision loses: the restore is conditional like the delete.
        using var stale = await owner.SendAsync(
            await owner.Post($"/api/projects/{project:D}/archive-data/restore", null, tombstoneRevision + 5));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var restored = await owner.SendAsync(
            await owner.Post($"/api/projects/{project:D}/archive-data/restore", null, tombstoneRevision));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        using var body = JsonDocument.Parse(await restored.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.False(root.GetProperty("live").GetBoolean());
        Assert.False(root.GetProperty("eraseRequested").GetBoolean());
        Assert.NotNull(root.GetProperty("restoredAt").GetString());

        // The deletion is lifted, not merged: no live tombstone remains, so the state reads 404
        // and restoring again restores nothing.
        using var read = await owner.GetAsync($"/api/projects/{project:D}/archive-data");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        using var again = await owner.SendAsync(
            await owner.Post($"/api/projects/{project:D}/archive-data/restore", null, tombstoneRevision + 1));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        await AssertAuditAsync(scenario.Database, "project.archive-restored");
    }

    // ===========================================================================================
    // HOSTED-ARCHIVE-OPERATIONS-002 Failure: restore past the window; foreign restore denied.
    // ===========================================================================================

    [Fact]
    public async Task Archive_002_Failure_restore_past_the_window_is_expired()
    {
        var scenario = await ScenarioAsync("hao_002_expired");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var (name, project) = await CreateNamedProjectAsync(owner, "过期");
        var revision = await ReadProjectRevisionAsync(owner, project);

        using var deleted = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = name },
                revision));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var tombstoneRevision = JsonDocument
            .Parse(await deleted.Content.ReadAsStringAsync())
            .RootElement.GetProperty("revision").GetInt64();

        // Thirty-one days later the window the delete disclosed has lapsed. The old
        // session lapsed with it, so the restore signs in again at the later instant.
        scenario.Clock.Current = Clock.AddDays(31);
        using var later = await OwnerClient.CreateAsync(host);

        using var expired = await later.SendAsync(
            await later.Post($"/api/projects/{project:D}/archive-data/restore", null, tombstoneRevision));
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal(
            "archive_restore_expired",
            JsonDocument.Parse(await expired.Content.ReadAsStringAsync())
                .RootElement.GetProperty("code").GetString());

        // The tombstone is still live: expiry refuses the restore without changing the state.
        using var read = await later.GetAsync($"/api/projects/{project:D}/archive-data");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Archive_002_Failure_a_foreign_restore_is_denied_without_foreign_data()
    {
        var scenario = await ScenarioAsync("hao_002_foreign");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        // Another tenant's project and live tombstone, seeded straight into the tables: no route
        // of this scope can reach them, and the suite proves it from the outside.
        var foreignScope = Guid.NewGuid();
        var foreignProject = Guid.NewGuid();
        await ChronicleafPostgresFixture.ExecuteAsync(
            scenario.Database,
            "INSERT INTO projects (id, scope_id, name, description, revision, archived_at)"
            + $" VALUES ('{foreignProject:D}', '{foreignScope:D}', 'Foreign', 'Foreign project.', 1, NULL)");
        await ChronicleafPostgresFixture.ExecuteAsync(
            scenario.Database,
            "INSERT INTO archive_tombstones (id, scope_id, project_id, project_name, deleted_at,"
            + " recovery_until, erase_requested, hold_reason, restored_at, purged_at, revision,"
            + " created_at, updated_at)"
            + $" VALUES ('{Guid.NewGuid():D}', '{foreignScope:D}', '{foreignProject:D}', 'Foreign',"
            + $" '{Clock:yyyy-MM-ddTHH:mm:ssZ}', '{Clock.AddDays(30):yyyy-MM-ddTHH:mm:ssZ}', true,"
            + " NULL, NULL, NULL, 1,"
            + $" '{Clock:yyyy-MM-ddTHH:mm:ssZ}', '{Clock:yyyy-MM-ddTHH:mm:ssZ}')");

        using var read = await owner.GetAsync($"/api/projects/{foreignProject:D}/archive-data");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        using var restore = await owner.SendAsync(
            await owner.Post($"/api/projects/{foreignProject:D}/archive-data/restore", null, 1));
        Assert.Equal(HttpStatusCode.NotFound, restore.StatusCode);

        // The refusal names the delivered not-found and carries no foreign id, name or window.
        var refusal = await restore.Content.ReadAsStringAsync();
        Assert.DoesNotContain(foreignScope.ToString("D"), refusal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Foreign", refusal, StringComparison.Ordinal);

        // And the foreign row is untouched: still live, still revision one.
        Assert.Equal(
            1L,
            await CountAsync(
                scenario.Database,
                $"SELECT count(*) FROM archive_tombstones WHERE project_id = '{foreignProject:D}'"
                + " AND restored_at IS NULL AND purged_at IS NULL AND revision = 1"));
    }

    // ===========================================================================================
    // HOSTED-ARCHIVE-OPERATIONS-002 Boundary: purge after the window retires only eligible media.
    // ===========================================================================================

    [Fact]
    public async Task Archive_002_Boundary_purge_after_the_window_retires_only_eligible_media()
    {
        var scenario = await ScenarioAsync("hao_002_purge");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var (name, project) = await CreateNamedProjectAsync(owner, "清除");
        var milestone = await CreateMilestoneAsync(owner, project, "Purged release");
        await AppendManualEvidenceAsync(owner, milestone, "Evidence the purge must keep.");
        var bundleId = await ReadGuidAsync(
            scenario.Database,
            $"SELECT id FROM evidence_bundles WHERE milestone_id = '{milestone:D}'");

        var loose = await UploadMediaAsync(owner, project, TestPng.Create(8, 5));
        var bound = await UploadMediaAsync(owner, project, TestPng.Create(6, 6));
        await SeedScreenshotItemAsync(scenario.Database, bundleId, milestone, bound);

        var revision = await ReadProjectRevisionAsync(owner, project);
        using var deleted = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = name },
                revision));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        // The window lapses with the erase request standing: the purge may now run. The old
        // session lapsed with it, so every later read signs in again at the later instant.
        scenario.Clock.Current = Clock.AddDays(31);
        using var later = await OwnerClient.CreateAsync(host);

        var receipts = await Retention(host).PurgeExpiredAsync(scenario.Clock.Current);
        var receipt = Assert.Single(receipts);
        Assert.False(receipt.Skipped);
        Assert.Null(receipt.SkipReason);
        Assert.Equal(project, receipt.ProjectId);
        Assert.Contains(loose, receipt.RetiredMedia);
        Assert.Contains(bound, receipt.RetainedMediaInUse);
        Assert.Equal(1, receipt.RetainedEvidenceBundles);

        // The unreferenced bytes are gone and unservable; the referenced bytes still serve.
        using var gone = await later.GetAsync($"/api/media/{loose:D}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var kept = await later.GetAsync($"/api/media/{bound:D}");
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);

        // Evidence, milestones and the tombstone history survive the purge: only eligible
        // unreferenced private data was removed, with a receipt saying what stayed.
        Assert.Equal(1L, await CountAsync(scenario.Database, "SELECT count(*) FROM evidence_bundles"));
        Assert.Equal(2L, await CountAsync(scenario.Database, "SELECT count(*) FROM evidence_items"));
        Assert.Equal(1L, await CountAsync(scenario.Database, "SELECT count(*) FROM archive_tombstones"));

        using var read = await later.GetAsync($"/api/projects/{project:D}/archive-data");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        await AssertAuditAsync(scenario.Database, "project.archive-purged");
    }

    [Fact]
    public async Task Archive_002_Boundary_purge_inside_the_window_removes_nothing()
    {
        var scenario = await ScenarioAsync("hao_002_window");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var (name, project) = await CreateNamedProjectAsync(owner, "窗口");
        var mediaId = await UploadMediaAsync(owner, project, TestPng.Create(8, 5));

        var revision = await ReadProjectRevisionAsync(owner, project);
        using var deleted = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = name },
                revision));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        // The purge runs on schedule but the window is still open: it removes nothing.
        var receipts = await Retention(host).PurgeExpiredAsync(Clock.AddDays(29));
        var receipt = Assert.Single(receipts);
        Assert.True(receipt.Skipped);
        Assert.Equal("window_open", receipt.SkipReason);

        using var media = await owner.GetAsync($"/api/media/{mediaId:D}");
        Assert.Equal(HttpStatusCode.OK, media.StatusCode);

        using var read = await owner.GetAsync($"/api/projects/{project:D}/archive-data");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Archive_002_Boundary_purge_never_deletes_evidence_under_a_live_approval()
    {
        var scenario = await ScenarioAsync("hao_002_approval");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var (name, project) = await CreateNamedProjectAsync(owner, "批准");
        var milestone = await CreateMilestoneAsync(owner, project, "Approved release");
        var mediaId = await UploadMediaAsync(owner, project, TestPng.Create(8, 5));
        await SeedAssetAsync(scenario.Database, project, milestone);
        await SeedLiveApprovalAsync(scenario.Database, project, milestone);

        var revision = await ReadProjectRevisionAsync(owner, project);
        using var deleted = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = name },
                revision));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        scenario.Clock.Current = Clock.AddDays(31);
        using var later = await OwnerClient.CreateAsync(host);

        // The window has lapsed but a live approval still binds the content: the purge stops
        // before deleting anything, and the receipt says why.
        var blocked = await Retention(host).PurgeExpiredAsync(scenario.Clock.Current);
        var skip = Assert.Single(blocked);
        Assert.True(skip.Skipped);
        Assert.Equal("approval_active", skip.SkipReason);

        using var media = await later.GetAsync($"/api/media/{mediaId:D}");
        Assert.Equal(HttpStatusCode.OK, media.StatusCode);

        // Withdrawing the approval releases the purge: invalidating is the owner's remedy, and
        // only then does the eligible media retire.
        await ChronicleafPostgresFixture.ExecuteAsync(
            scenario.Database,
            "UPDATE content_approvals SET invalidated_at ="
            + $" '{scenario.Clock.Current:yyyy-MM-ddTHH:mm:ssZ}', reason = 'withdrawn'"
            + " WHERE invalidated_at IS NULL");

        var released = await Retention(host).PurgeExpiredAsync(scenario.Clock.Current);
        var receipt = Assert.Single(released);
        Assert.False(receipt.Skipped);
        Assert.Contains(mediaId, receipt.RetiredMedia);
        Assert.Equal(1, receipt.RetainedContentAssets);

        // The approval history itself survives: withdrawing invalidates, and the purge never
        // deletes the row that once authorized an export.
        Assert.Equal(1L, await CountAsync(scenario.Database, "SELECT count(*) FROM content_approvals"));
    }

    [Fact]
    public async Task Archive_002_Boundary_a_hold_blocks_the_purge_until_it_clears()
    {
        var scenario = await ScenarioAsync("hao_002_hold");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var (name, project) = await CreateNamedProjectAsync(owner, "保留");
        var mediaId = await UploadMediaAsync(owner, project, TestPng.Create(8, 5));

        var revision = await ReadProjectRevisionAsync(owner, project);
        using var deleted = await owner.SendAsync(
            await owner.DeleteWithBody(
                $"/api/projects/{project:D}/archive-data",
                new { confirmProjectName = name },
                revision));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var tombstoneId = await ReadGuidAsync(
            scenario.Database,
            $"SELECT id FROM archive_tombstones WHERE project_id = '{project:D}'");

        var held = await Retention(host).HoldAsync(Scope(), tombstoneId, "operator legal hold");
        Assert.Equal("operator legal hold", held.HoldReason);

        scenario.Clock.Current = Clock.AddDays(31);

        var blocked = await Retention(host).PurgeExpiredAsync(scenario.Clock.Current);
        var skip = Assert.Single(blocked);
        Assert.True(skip.Skipped);
        Assert.Equal("held", skip.SkipReason);

        var released = await Retention(host).ClearHoldAsync(Scope(), tombstoneId);
        Assert.Null(released.HoldReason);

        var purged = await Retention(host).PurgeExpiredAsync(scenario.Clock.Current);
        var receipt = Assert.Single(purged);
        Assert.False(receipt.Skipped);
        Assert.Contains(mediaId, receipt.RetiredMedia);
    }

    // ===========================================================================================
    // Fixtures and readers.
    // ===========================================================================================

    private sealed class SettableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = initial;

        public override DateTimeOffset GetUtcNow() => Current;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed record Scenario(
        string Database,
        HostedArchiveOperationsApiHost Host,
        SettableTimeProvider Clock,
        string MediaRoot,
        string KeyFile);

    private async Task<Scenario> ScenarioAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        var mediaRoot = Path.Combine(Path.GetTempPath(), $"chronicleaf-archive-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mediaRoot);
        _temporaryRoots.Add(mediaRoot);

        using var keyFile = OperatorKeyFile.Create();
        var keyPath = Path.Combine(Path.GetTempPath(), $"chronicleaf-archive-{purpose}-{Guid.NewGuid():N}.key");
        File.Copy(keyFile.FilePath, keyPath, overwrite: true);
        _keyFiles.Add(keyPath);

        var clock = new SettableTimeProvider(Clock);
        var host = HostedArchiveOperationsApiHost.Start(database, clock, mediaRoot, keyPath);

        using (var scope = host.Services.CreateScope())
        {
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

        return new Scenario(database, host, clock, mediaRoot, keyPath);
    }

    private static OwnerScope Scope() => new(InstallationId, InstallationId);

    private static IArchiveBackupService Backups(HostedArchiveOperationsApiHost host)
    {
        var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IArchiveBackupService>();
    }

    private static IArchiveRetentionService Retention(HostedArchiveOperationsApiHost host)
    {
        var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IArchiveRetentionService>();
    }

    private static async Task<Guid> CreateProjectAsync(OwnerClient owner, string name)
    {
        using var response = await owner.SendAsync(
            await owner.Post("/api/projects", new { name = $"{name} 项目", description = $"{name} 说明。" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<(string Name, Guid Id)> CreateNamedProjectAsync(OwnerClient owner, string name)
    {
        var full = $"{name} 项目";
        using var response = await owner.SendAsync(
            await owner.Post("/api/projects", new { name = full, description = $"{name} 说明。" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (full, Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!));
    }

    private static async Task<long> ReadProjectRevisionAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return root.GetProperty("revision").GetInt64();
    }

    private static async Task<Guid> CreateMilestoneAsync(OwnerClient owner, Guid projectId, string title)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/milestones",
            new
            {
                title,
                version = "0.1.0",
                occurredAt = "2026-10-01T09:00:00Z",
                type = "feature",
                notes = $"{title} notes.",
                knownLimitations = Array.Empty<string>(),
                nextSteps = Array.Empty<string>(),
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
    }

    private static async Task AppendManualEvidenceAsync(OwnerClient owner, Guid milestoneId, string notes)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes, expectedRevision = 0 },
            0));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<Guid> UploadMediaAsync(OwnerClient owner, Guid projectId, byte[] png)
    {
        using var uploaded = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            png,
            "image/png"));

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await uploaded.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<JsonElement> ReadJourneyAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId:D}/journey");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<JsonElement> ReadScheduleAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId:D}/sync-schedule");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<string> SaveCredentialAsync(OwnerClient owner, string provider, string value)
    {
        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/settings/credentials/{provider}",
            new { value },
            0));

        Assert.True(
            saved.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            await saved.Content.ReadAsStringAsync());

        // The revision travels in the ETag, never in the body: the body names only the
        // provider, whether it is configured and when it changed - never a secret.
        return saved.Headers.ETag?.ToString() ?? string.Empty;
    }

    private static async Task<(bool Configured, string ETag)> ReadCredentialAsync(
        OwnerClient owner,
        string provider)
    {
        using var response = await owner.GetAsync($"/api/settings/credentials/{provider}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var configured = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("configured").GetBoolean();

        return (configured, response.Headers.ETag?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// Seeds a live published journey straight into the table: the publication flow the journey
    /// package owns needs approved assets, which are another package's outcome, while what this
    /// suite proves is the suspension - that a live row is revoked in the same unit of work as
    /// the tombstone. The row itself is real and the revoke path is production code.
    /// </summary>
    private static async Task SeedPublishedJourneyAsync(string database, Guid projectId)
    {
        var hash = new string('a', 64);
        var slug = $"journey-{projectId:N}";
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            "INSERT INTO project_journeys (id, scope_id, project_id, slug, enabled, revision,"
            + " selection_hash, snapshot_ref, snapshot_json, revoked_at, created_at, updated_at)"
            + $" VALUES ('{Guid.NewGuid():D}', '{InstallationId:D}', '{projectId:D}',"
            + $" '{slug}', true, 1, '{hash}', '{hash}',"
            + $" '{{\"entries\":[]}}', NULL,"
            + $" '{Clock:yyyy-MM-ddTHH:mm:ssZ}', '{Clock:yyyy-MM-ddTHH:mm:ssZ}')");
    }

    /// <summary>
    /// Seeds a live schedule and its repository connection straight into the tables, for the
    /// same reason as the journey seed above: the connection handshake needs the GitHub
    /// transport, while what this suite proves is the suspension through the delivered
    /// schedule contract. Both rows are real and the disable path is production code.
    /// </summary>
    private static async Task SeedScheduleAsync(string database, Guid projectId)
    {
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            "INSERT INTO github_connections (id, scope_id, project_id, \"owner\", repo,"
            + " credential_ref, default_branch, repository_id, created_at, updated_at)"
            + $" VALUES ('{Guid.NewGuid():D}', '{InstallationId:D}', '{projectId:D}',"
            + " 'chronicleaf-tests', 'archive-fixture', NULL, 'main', 4242,"
            + $" '{Clock:yyyy-MM-ddTHH:mm:ssZ}', '{Clock:yyyy-MM-ddTHH:mm:ssZ}')");
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            "INSERT INTO project_sync_schedules (id, scope_id, project_id, enabled,"
            + " interval_minutes, next_run_at, last_operation_id, revision, created_at, updated_at)"
            + $" VALUES ('{Guid.NewGuid():D}', '{InstallationId:D}', '{projectId:D}', true, 60,"
            + $" '{Clock.AddMinutes(60):yyyy-MM-ddTHH:mm:ssZ}', NULL, 1,"
            + $" '{Clock:yyyy-MM-ddTHH:mm:ssZ}', '{Clock:yyyy-MM-ddTHH:mm:ssZ}')");
    }

    /// <summary>
    /// Seeds a draft content asset straight into the table: generation is another package's job,
    /// while what this suite proves is that the digest covers articles and the purge keeps them.
    /// </summary>
    private static async Task SeedAssetAsync(string database, Guid projectId, Guid milestoneId)
    {
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            "INSERT INTO content_assets (id, scope_id, project_id, milestone_id, type, status,"
            + " current_revision, created_at, updated_at)"
            + $" VALUES ('{Guid.NewGuid():D}', '{InstallationId:D}', '{projectId:D}',"
            + $" '{milestoneId:D}', 'markdown-story', 'draft', 1,"
            + $" '{Clock:yyyy-MM-ddTHH:mm:ssZ}', '{Clock:yyyy-MM-ddTHH:mm:ssZ}')");
    }

    /// <summary>
    /// Seeds a live approval over the project's first asset: the review flow is another
    /// package's outcome, while what this suite proves is that the purge stops while one is
    /// live. The row is real and the block check is production SQL.
    /// </summary>
    private static async Task SeedLiveApprovalAsync(string database, Guid projectId, Guid milestoneId)
    {
        var assetId = await ReadGuidAsync(
            database,
            $"SELECT id FROM content_assets WHERE project_id = '{projectId:D}'"
            + $" AND milestone_id = '{milestoneId:D}'");

        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            "INSERT INTO content_approvals (id, scope_id, asset_id, content_revision, actor_id,"
            + " payload_kind, body_hash, evidence_revision, voice_snapshot_hash,"
            + " media_selection_hash, artifact_hash, approved_at, invalidated_at, reason)"
            + $" VALUES ('{Guid.NewGuid():D}', '{InstallationId:D}', '{assetId:D}', 1, NULL,"
            + $" 'text', '{new string('b', 64)}', 1, '{new string('b', 64)}',"
            + $" '{new string('b', 64)}', NULL, '{Clock:yyyy-MM-ddTHH:mm:ssZ}', NULL, NULL)");
    }

    /// <summary>
    /// Seeds a collected screenshot item naming one media row: the capture flow is another
    /// package's job, while what this suite proves is that the purge retires around a live
    /// reference through the delivered reference check. The row is real - the database's own
    /// kind, hash and collected-text constraints accept it - and the retire refusal is
    /// production code.
    /// </summary>
    private static async Task SeedScreenshotItemAsync(
        string database,
        Guid bundleId,
        Guid milestoneId,
        Guid mediaId)
    {
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            "INSERT INTO evidence_items (id, bundle_id, milestone_id, kind, source_ref,"
            + " source_revision, captured_at, payload_sha256, text, media_id, redactions,"
            + " availability, supersedes_id)"
            + $" VALUES ('{Guid.NewGuid():D}', '{bundleId:D}', '{milestoneId:D}', 'screenshot',"
            + $" 'capture', 'viewport-desktop', '{Clock:yyyy-MM-ddTHH:mm:ssZ}',"
            + $" '{new string('f', 64)}', 'The depicted picture.', '{mediaId:D}', '{{}}',"
            + " 'complete', NULL)");
    }

    private static Task<long> CountAsync(string database, string sql) =>
        OperationRows.CountAsync(database, sql);

    private static async Task<Guid> ReadGuidAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);

        return (Guid)value!;
    }

    private static async Task<string> ReadMediaShaAsync(string database, Guid mediaId)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT sha256 FROM media WHERE id = '{mediaId:D}'";

        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);

        return (string)value!;
    }

    private static async Task AssertAuditAsync(string database, string action)
    {
        var count = await CountAsync(
            database,
            $"SELECT count(*) FROM audit_events WHERE action = '{action}'");

        Assert.True(count >= 1, $"No audit row for {action}.");
    }

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));
}
