using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Media.S3;
using Chronicleaf.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Platform.Storage.Contracts;
using Platform.Storage.Keys;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// CLOUD-OBJECT-STORAGE-001 and CLOUD-OBJECT-STORAGE-002 against a real disposable PostgreSQL,
/// a real temporary filesystem for the Local side, and either a scripted S3 backend or a real
/// MinIO container for the S3 side - always through the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// <para>
/// Two backend shapes, and the reason for each is stated per scenario. The scripted backend is an
/// in-memory <see cref="IObjectStorage"/> behind the production <see cref="S3MediaStore"/>, the
/// production link service, the production migration service and the production router: every key
/// the stores generate, every scope check they run, every verification they require and every
/// ledger entry they write is production code, and only the bytes' resting place is the test's.
/// The fault switches on the script are how interruption and corruption are scheduled - the two
/// failures no real backend produces on demand.
/// </para>
/// <para>
/// The MinIO scenarios prove the other half: the production backend factory over a real
/// <c>AmazonS3Client</c> against a real S3-compatible server, with the bytes asserted on both
/// sides of the wire - the object the server holds and the row PostgreSQL holds. The credential
/// script stands in for the vault there, while the vault-reference scenario stores a real sealed
/// credential through the real vault routes and proves the sentinel is absent from every row and
/// every message. No scenario touches a real bucket, a real cloud account or a real credential.
/// </para>
/// <para>
/// Every test gets its own database, its own MediaRoot and (for MinIO) its own bucket, and the
/// MinIO container is one per test class. The clock is injected and frozen, so every stored
/// instant is deterministic. The identities are constants: the installation and its scope, the
/// foreign scope, the project and the media ids a scenario declares in advance.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class CloudObjectStorageTests(ChronicleafPostgresFixture postgres, MinioFixture minio)
    : IClassFixture<MinioFixture>, IDisposable
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("8a3f1c4d-5e6b-4a7c-9d0e-1f2a3b4c5d6e");

    /// <summary>A second scope, used only to prove media outside the caller's scope is not found.</summary>
    private static readonly Guid ForeignScopeId = new("9b4e2d5f-6a7c-4b8d-0e1f-2a3b4c5d6e7f");

    private readonly List<string> _temporaryRoots = [];

    public void Dispose()
    {
        foreach (var root in _temporaryRoots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A temporary directory that will not delete is not a failed scenario; the fixture
                // asserts on the files that matter while the test is still running.
            }
        }
    }

    // ===========================================================================================
    // CLOUD-OBJECT-STORAGE-001 Success: a valid bounded PNG uploaded in S3 mode lands in the
    // bucket under its scoped key with its decoded type and hash, and reads back identically.
    // ===========================================================================================

    [Fact]
    public async Task S3_upload_stores_hash_and_scoped_key_and_reads_back_identically()
    {
        Assert.True(postgres.IsRealPostgres, "the cloud object storage scenarios require a real PostgreSQL container");
        var database = await ProvisionedDatabaseAsync("s3_success");
        var mediaRoot = TemporaryRoot();
        var mediaId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee01");
        var png = TestPng.Create(width: 12, height: 7);
        var bucket = await ProvisionedBucketAsync("s3-success");

        await using var host = MediaApiHost.StartS3(
            database,
            Clock,
            mediaRoot,
            MinioOptions(bucket),
            credentialSource: new S3CredentialScript(MinioFixture.User, MinioFixture.Password),
            ids: new SequenceMediaIdSource(mediaId));

        using var owner = await OwnerClient.CreateAsync(host);
        var projectId = await CreateProjectAsync(owner, "Ledger");

        using var uploaded = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            png,
            "image/png"));

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        var uploadBody = await uploaded.Content.ReadAsStringAsync();
        var expectedHash = Sha256Hex(png);

        using var document = JsonDocument.Parse(uploadBody);
        var root = document.RootElement;

        Assert.Equal(mediaId.ToString("D"), root.GetProperty("id").GetString());
        Assert.Equal(MediaTypes.Png, root.GetProperty("mediaType").GetString());
        Assert.Equal(MediaStatus.Ready, root.GetProperty("status").GetString());
        Assert.Equal(png.Length, root.GetProperty("byteLength").GetInt64());
        Assert.Equal(expectedHash, root.GetProperty("sha256").GetString());

        // The key is server-generated, scoped, and names the project it belongs to.
        var storageKey = root.GetProperty("storageKey").GetString()!;
        Assert.Equal(
            $"scopes/{InstallationId:D}/projects/{projectId:D}/media/{MediaStatus.Ready}/{mediaId:D}.png",
            storageKey);

        // The stored row agrees with what the caller was told, and names the S3 backend.
        var row = await ReadMediaRowAsync(database, mediaId);
        Assert.NotNull(row);
        Assert.Equal("s3", row!.ProviderId);
        Assert.Equal(expectedHash, row.Sha256);
        Assert.Equal(storageKey, row.StorageKey);

        // The bucket holds the object under the same key, with the same bytes. Read over the real
        // SDK, so the assertion is about MinIO's own object and not a test double's memory.
        var stored = await MinioGetAsync(bucket, storageKey);
        Assert.Equal(expectedHash, Sha256Hex(stored));

        // The bytes read back through the API are the bytes that were stored.
        using var read = await owner.GetAsync($"/api/media/{mediaId:D}");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(MediaTypes.Png, read.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedHash, Sha256Hex(await read.Content.ReadAsByteArrayAsync()));
    }

    // ===========================================================================================
    // CLOUD-OBJECT-STORAGE-001 Failure: a tenant's request for another tenant's object is denied
    // before any presign is minted and before any byte is transferred.
    // ===========================================================================================

    [Fact]
    public async Task Foreign_scope_media_is_denied_before_presign_or_byte_transfer()
    {
        Assert.True(postgres.IsRealPostgres, "the cloud object storage scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("s3_foreign");
        var mediaRoot = TemporaryRoot();
        var mediaId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee02");
        var png = TestPng.Create(width: 8, height: 5);
        var backend = new S3TestBackend();

        var script = new S3BackendScript(backend);
        await using var host = MediaApiHost.StartS3(
            database,
            Clock,
            mediaRoot,
            S3HostOptions.UnusedBackend(),
            backendFactory: script,
            references: new MediaReferenceScript(),
            ids: new SequenceMediaIdSource(mediaId));

        using var owner = await OwnerClient.CreateAsync(host);
        var projectId = await CreateProjectAsync(owner, "Ledger");

        using (var uploaded = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            png,
            "image/png")))
        {
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        var uploadedRow = await ReadMediaRowAsync(database, mediaId);
        Assert.NotNull(uploadedRow);
        Assert.Equal("s3", uploadedRow.ProviderId);

        // Baselines after the owner's own upload: the upload itself stages, re-reads and promotes
        // through the backend, so only activity past these counts would be a leak.
        var backendsBefore = script.Created;
        var downloadsBefore = backend.Downloads;
        var presignsBefore = backend.Presigns;

        // A row in another scope, read over HTTP, is a 404 - and the backend proves no byte moved:
        // no download was served and no presign was minted for it. The project is real; only the
        // scope is foreign, so the refusal is scope isolation rather than project confusion.
        var foreignMediaId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee99");
        var foreignKey = MediaValues.ReadyStorageKey(ForeignScopeId, Guid.Parse(projectId), foreignMediaId);
        await InsertMediaRowAsync(
            database,
            foreignMediaId,
            ForeignScopeId,
            Guid.Parse(projectId),
            foreignKey,
            "ready",
            Sha256Hex(png),
            "s3");

        using (var read = await owner.GetAsync($"/api/media/{foreignMediaId:D}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }

        using (var presigned = await owner.SendAsync(await owner.PostWithoutBody(
            $"/api/media/{foreignMediaId:D}/presign?lifetimeSeconds=60")))
        {
            Assert.Equal(HttpStatusCode.NotFound, presigned.StatusCode);
        }

        Assert.Equal(backendsBefore, script.Created);
        Assert.Equal(downloadsBefore, backend.Downloads);
        Assert.Equal(presignsBefore, backend.Presigns);
    }

    // ===========================================================================================
    // CLOUD-OBJECT-STORAGE-001 Boundary: a presigned URL carries its expiry, over-long lifetimes
    // are refused before the provider is touched, and an expired URL authorizes nothing.
    // ===========================================================================================

    [Fact]
    public async Task Overlong_presign_lifetime_is_refused_before_the_provider_is_touched()
    {
        Assert.True(postgres.IsRealPostgres, "the cloud object storage scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("s3_presign_bounds");
        var mediaRoot = TemporaryRoot();
        var mediaId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee03");
        var png = TestPng.Create(width: 8, height: 5);
        var backend = new S3TestBackend();

        var script = new S3BackendScript(backend);
        await using var host = MediaApiHost.StartS3(
            database,
            Clock,
            mediaRoot,
            S3HostOptions.UnusedBackend(),
            backendFactory: script,
            ids: new SequenceMediaIdSource(mediaId));

        using var owner = await OwnerClient.CreateAsync(host);
        var projectId = await CreateProjectAsync(owner, "Ledger");

        using (var uploaded = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            png,
            "image/png")))
        {
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        // Six minutes is over the five-minute ceiling: 422, and the backend proves no presign was
        // minted for it.
        using (var refused = await owner.SendAsync(await owner.PostWithoutBody(
            $"/api/media/{mediaId:D}/presign?lifetimeSeconds=360")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

            using var document = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("validation_failed", document.RootElement.GetProperty("code").GetString());
        }

        // Zero and negative lifetimes are not durations: 422 as well.
        using (var zero = await owner.SendAsync(await owner.PostWithoutBody(
            $"/api/media/{mediaId:D}/presign?lifetimeSeconds=0")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, zero.StatusCode);
        }

        // An unparseable lifetime is a semantic failure, not a not-found.
        using (var garbage = await owner.SendAsync(await owner.PostWithoutBody(
            $"/api/media/{mediaId:D}/presign?lifetimeSeconds=soon")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, garbage.StatusCode);
        }

        Assert.Equal(0, backend.Presigns);

        // A lawful request mints a bounded URL whose expiry travels with it.
        using (var minted = await owner.SendAsync(await owner.PostWithoutBody(
            $"/api/media/{mediaId:D}/presign?lifetimeSeconds=60")))
        {
            Assert.Equal(HttpStatusCode.OK, minted.StatusCode);

            using var document = JsonDocument.Parse(await minted.Content.ReadAsStringAsync());
            var url = document.RootElement.GetProperty("url").GetString()!;
            var expiresAt = document.RootElement.GetProperty("expiresAt").GetDateTimeOffset();

            Assert.False(string.IsNullOrWhiteSpace(url));
            Assert.Equal(1, backend.Presigns);

            // The expiry is the bound that was asked for, from the provider's own clock: about
            // sixty seconds out, which is what the provider enforces. A range rather than an
            // instant, because the signature is minted on the provider's time and the test must
            // not pretend the two clocks are one.
            var remaining = (expiresAt - DateTimeOffset.UtcNow).TotalSeconds;
            Assert.InRange(remaining, 50, 65);
        }
    }

    [Fact]
    public async Task Expired_presign_no_longer_authorizes_the_object()
    {
        Assert.True(postgres.IsRealPostgres, "the cloud object storage scenarios require a real PostgreSQL container");
        var database = await ProvisionedDatabaseAsync("s3_presign_expiry");
        var mediaRoot = TemporaryRoot();
        var mediaId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee04");
        var png = TestPng.Create(width: 8, height: 5);
        var bucket = await ProvisionedBucketAsync("s3-presign-expiry");

        await using var host = MediaApiHost.StartS3(
            database,
            Clock,
            mediaRoot,
            MinioOptions(bucket),
            credentialSource: new S3CredentialScript(MinioFixture.User, MinioFixture.Password),
            ids: new SequenceMediaIdSource(mediaId));

        using var owner = await OwnerClient.CreateAsync(host);
        var projectId = await CreateProjectAsync(owner, "Ledger");

        using (var uploaded = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            png,
            "image/png")))
        {
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        // A one-second URL: fetchable now, refused once the second has passed. The sleep is the
        // assertion's own cost - expiry is the provider's clock, not the test's, so only waiting
        // past it proves the refusal.
        string url;
        using (var minted = await owner.SendAsync(await owner.PostWithoutBody(
            $"/api/media/{mediaId:D}/presign?lifetimeSeconds=1")))
        {
            Assert.Equal(HttpStatusCode.OK, minted.StatusCode);

            using var document = JsonDocument.Parse(await minted.Content.ReadAsStringAsync());
            url = document.RootElement.GetProperty("url").GetString()!;

            var parsed = new Uri(url);

            // The URL addresses the configured endpoint in its own scheme: a plain-http MinIO
            // endpoint gets a plain-http URL, which is the only shape the expiry probe below can
            // fetch at all.
            Assert.Equal(Uri.UriSchemeHttp, parsed.Scheme);
        }

        using (var fresh = await new HttpClient().GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        }

        await Task.Delay(TimeSpan.FromSeconds(3));

        using (var expired = await new HttpClient().GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        }
    }

    // ===========================================================================================
    // CLOUD-OBJECT-STORAGE-002 Success: verified copies flip the row to the target provider while
    // the source bytes stay where they are, and each backend serves its own rows afterwards.
    // ===========================================================================================

    [Fact]
    public async Task Migration_copies_verifies_and_flips_each_row_keeping_the_source()
    {
        Assert.True(postgres.IsRealPostgres, "the cloud object storage scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("s3_migrate_success");
        var mediaRoot = TemporaryRoot();
        var firstId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee05");
        var secondId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee06");
        var png = TestPng.Create(width: 10, height: 6);
        var expectedHash = Sha256Hex(png);

        // Two images uploaded the Local way, against the real filesystem.
        await using (var local = MediaApiHost.Start(
            database,
            Clock,
            mediaRoot,
            ids: new SequenceMediaIdSource(firstId, secondId)))
        {
            using var owner = await OwnerClient.CreateAsync(local);
            var projectId = await CreateProjectAsync(owner, "Ledger");

            foreach (var _ in new[] { 1, 2 })
            {
                using var uploaded = await owner.SendAsync(await owner.PostBytes(
                    $"/api/media?projectId={projectId}",
                    png,
                    "image/png"));

                Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            }
        }

        var first = (await ReadMediaRowAsync(database, firstId))!;
        var second = (await ReadMediaRowAsync(database, secondId))!;
        Assert.Equal("local", first.ProviderId);
        Assert.Equal("local", second.ProviderId);

        var backend = new S3TestBackend();
        var script = new S3BackendScript(backend);

        // One record per run: the first moves, the second stays, and the installation serves each
        // from the backend its own row names. That split read is the router's whole claim.
        await using (var host = MediaApiHost.StartS3(
            database,
            Clock,
            mediaRoot,
            S3HostOptions.UnusedBackend(),
            backendFactory: script,
            references: new MediaReferenceScript()))
        {
            using var scope = host.Services.CreateScope();
            var migration = scope.ServiceProvider.GetRequiredService<IMediaMigrationService>();
            var owner = new Application.Milestones.OwnerScope(InstallationId, InstallationId);

            var result = await migration.MigrateAsync(owner, "local", "s3", 1);

            Assert.Equal(1, result.Copied);
            Assert.Equal(0, result.AlreadyVerified);
            Assert.Empty(result.Failures);

            Assert.Equal("s3", (await ReadMediaRowAsync(database, firstId))!.ProviderId);
            Assert.Equal("local", (await ReadMediaRowAsync(database, secondId))!.ProviderId);

            // The ledger recorded the copy's checksum, both keys and the verified state.
            var entry = await ReadLedgerAsync(host, InstallationId, firstId);
            Assert.NotNull(entry);
            Assert.Equal("verified", entry!.State);
            Assert.Equal(expectedHash, entry.SourceSha256);
            Assert.Equal(expectedHash, entry.CopiedSha256);
            Assert.Equal(first.StorageKey, entry.SourceKey);
            Assert.Equal(first.StorageKey, entry.TargetKey);

            // The source bytes are untouched: migration never deletes.
            var localPath = Path.Combine(mediaRoot, first.StorageKey.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(localPath));
            Assert.Equal(expectedHash, Sha256Hex(await File.ReadAllBytesAsync(localPath)));

            // The target holds the same bytes under the same key.
            Assert.True(backend.Objects.TryGetValue(first.StorageKey, out var copied));
            Assert.Equal(expectedHash, Sha256Hex(copied));

            // Each backend serves its own row: the moved image from S3, the waiting one from disk.
            using var apiOwner = await OwnerClient.CreateAsync(host);

            using (var moved = await apiOwner.GetAsync($"/api/media/{firstId:D}"))
            {
                Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
                Assert.Equal(expectedHash, Sha256Hex(await moved.Content.ReadAsByteArrayAsync()));
            }

            using (var waiting = await apiOwner.GetAsync($"/api/media/{secondId:D}"))
            {
                Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);
                Assert.Equal(expectedHash, Sha256Hex(await waiting.Content.ReadAsByteArrayAsync()));
            }

            // The second run finishes the scope, and the third has nothing left to do.
            var finish = await migration.MigrateAsync(owner, "local", "s3", 10);

            Assert.Equal(1, finish.Copied);
            Assert.Empty(finish.Failures);
            Assert.Equal("s3", (await ReadMediaRowAsync(database, secondId))!.ProviderId);

            var idle = await migration.MigrateAsync(owner, "local", "s3", 10);

            Assert.Equal(0, idle.Copied);
            Assert.Equal(0, idle.AlreadyVerified);
            Assert.Empty(idle.Failures);
        }
    }

    // ===========================================================================================
    // CLOUD-OBJECT-STORAGE-002 Failure: an interrupted copy and a checksum mismatch both keep the
    // source reference valid, and a retry after the fault resumes safely.
    // ===========================================================================================

    [Fact]
    public async Task Migration_failure_keeps_the_source_and_retry_resumes()
    {
        Assert.True(postgres.IsRealPostgres, "the cloud object storage scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("s3_migrate_failure");
        var mediaRoot = TemporaryRoot();
        var interruptedId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee07");
        var corruptedId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee08");
        var png = TestPng.Create(width: 10, height: 6);
        var expectedHash = Sha256Hex(png);

        await using (var local = MediaApiHost.Start(
            database,
            Clock,
            mediaRoot,
            ids: new SequenceMediaIdSource(interruptedId, corruptedId)))
        {
            using var owner = await OwnerClient.CreateAsync(local);
            var projectId = await CreateProjectAsync(owner, "Ledger");

            foreach (var _ in new[] { 1, 2 })
            {
                using var uploaded = await owner.SendAsync(await owner.PostBytes(
                    $"/api/media?projectId={projectId}",
                    png,
                    "image/png"));

                Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            }
        }

        var backend = new S3TestBackend { FailUpload = true };
        var script = new S3BackendScript(backend);

        await using var host = MediaApiHost.StartS3(
            database,
            Clock,
            mediaRoot,
            S3HostOptions.UnusedBackend(),
            backendFactory: script,
            references: new MediaReferenceScript());

        using var hostScope = host.Services.CreateScope();
        var migration = hostScope.ServiceProvider.GetRequiredService<IMediaMigrationService>();
        var ownerScope = new Application.Milestones.OwnerScope(InstallationId, InstallationId);

        // The target refuses every write: both records fail, both stay local, and the ledger says
        // the copies started but never verified.
        var interrupted = await migration.MigrateAsync(ownerScope, "local", "s3", 10);

        Assert.Equal(0, interrupted.Copied);
        Assert.Equal(2, interrupted.Failures.Count);
        Assert.Equal("local", (await ReadMediaRowAsync(database, interruptedId))!.ProviderId);
        Assert.Equal("local", (await ReadMediaRowAsync(database, corruptedId))!.ProviderId);

        var ledger = await ReadLedgerAsync(host, InstallationId, interruptedId);
        Assert.NotNull(ledger);
        Assert.NotEqual("verified", ledger!.State);

        // Writes flow again but the target corrupts one copy: the interrupted record now verifies
        // and flips, while the corrupted one is recorded as a mismatch with its source intact.
        backend.FailUpload = false;
        backend.CorruptKeys.Add(MediaValues.ReadyStorageKey(
            InstallationId,
            (await ReadMediaRowAsync(database, corruptedId))!.ProjectId,
            corruptedId));

        var partial = await migration.MigrateAsync(ownerScope, "local", "s3", 10);

        Assert.Equal(1, partial.Copied);
        Assert.Single(partial.Failures);
        Assert.Equal(corruptedId, partial.Failures[0].MediaId);
        Assert.Equal("checksum_mismatch", partial.Failures[0].Reason);
        Assert.Equal("s3", (await ReadMediaRowAsync(database, interruptedId))!.ProviderId);
        Assert.Equal("local", (await ReadMediaRowAsync(database, corruptedId))!.ProviderId);

        var mismatch = await ReadLedgerAsync(host, InstallationId, corruptedId);
        Assert.NotNull(mismatch);
        Assert.Equal("failed", mismatch!.State);

        // The corrupted bytes are served nowhere: the source row still answers from disk.
        using var apiOwner = await OwnerClient.CreateAsync(host);
        using (var kept = await apiOwner.GetAsync($"/api/media/{corruptedId:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
            Assert.Equal(expectedHash, Sha256Hex(await kept.Content.ReadAsByteArrayAsync()));
        }

        // The fault clears and the retry resumes: the failed copy is made again and verifies.
        backend.CorruptKeys.Clear();

        var resumed = await migration.MigrateAsync(ownerScope, "local", "s3", 10);

        Assert.Equal(1, resumed.Copied);
        Assert.Empty(resumed.Failures);
        Assert.Equal("s3", (await ReadMediaRowAsync(database, corruptedId))!.ProviderId);

        var verified = await ReadLedgerAsync(host, InstallationId, corruptedId);
        Assert.NotNull(verified);
        Assert.Equal("verified", verified!.State);
        Assert.Equal(expectedHash, verified.CopiedSha256);
    }

    // ===========================================================================================
    // CLOUD-OBJECT-STORAGE-002 Boundary: a Community host with no S3 configuration stores,
    // serves and retires locally - and mints no presign anywhere.
    // ===========================================================================================

    [Fact]
    public async Task Community_local_mode_needs_no_s3_configuration()
    {
        Assert.True(postgres.IsRealPostgres, "the cloud object storage scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("s3_local_boundary");
        var mediaRoot = TemporaryRoot();
        var mediaId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee09");
        var png = TestPng.Create(width: 9, height: 6);
        var expectedHash = Sha256Hex(png);

        // No S3 setting anywhere in this host: the default configuration is Local mode.
        await using var host = MediaApiHost.StartWithReferences(
            database,
            Clock,
            mediaRoot,
            new MediaReferenceScript(),
            new SequenceMediaIdSource(mediaId));

        // No S3 backend factory was registered, so no S3 client can be built here by any path.
        using (var scope = host.Services.CreateScope())
        {
            Assert.Null(scope.ServiceProvider.GetService<IS3BackendFactory>());
        }

        using var owner = await OwnerClient.CreateAsync(host);
        var projectId = await CreateProjectAsync(owner, "Ledger");

        using (var uploaded = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            png,
            "image/png")))
        {
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        var row = await ReadMediaRowAsync(database, mediaId);
        Assert.NotNull(row);
        Assert.Equal("local", row!.ProviderId);

        using (var read = await owner.GetAsync($"/api/media/{mediaId:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(expectedHash, Sha256Hex(await read.Content.ReadAsByteArrayAsync()));
        }

        // A presign is an S3 capability, and this installation has no S3: 503, not a URL and not
        // a crash. The Local store's own answer is unchanged - retiring still works.
        using (var presigned = await owner.SendAsync(await owner.PostWithoutBody(
            $"/api/media/{mediaId:D}/presign?lifetimeSeconds=60")))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, presigned.StatusCode);

            using var document = JsonDocument.Parse(await presigned.Content.ReadAsStringAsync());
            Assert.Equal("storage_unavailable", document.RootElement.GetProperty("code").GetString());
        }

        using (var retired = await owner.SendAsync(await owner.Archive($"/api/media/{mediaId:D}", null)))
        {
            Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
        }

        Assert.Equal("deleted", (await ReadMediaRowAsync(database, mediaId))!.Status);
    }

    // ===========================================================================================
    // CLOUD-OBJECT-STORAGE-001 vault boundary: the S3 keys travel as a vault reference and never
    // reach a row, a message or a log - and a reference to another provider's credential cannot
    // become S3 keys.
    // ===========================================================================================

    [Fact]
    public async Task S3_credential_stays_a_vault_reference_and_never_reaches_a_row_or_a_message()
    {
        Assert.True(postgres.IsRealPostgres, "the cloud object storage scenarios require a real PostgreSQL container");
        var database = await ProvisionedDatabaseAsync("s3_vault_ref");
        var mediaRoot = TemporaryRoot();
        var bucket = await ProvisionedBucketAsync("s3-vault-ref");
        var credentialId = new Guid("bbbbbbbb-cccc-dddd-eeee-ffffffff0001");

        using var keyFile = OperatorKeyFile.Create();

        // The sealed document the operator stores: one JSON object with the two key halves. The
        // halves are the fixture's own test-only MinIO keys - MinIO authenticates them, so an
        // upload through the API proves the reference resolved to working keys - and the
        // assertions below hunt for those same values everywhere a leak could surface.
        var document = JsonSerializer.Serialize(new
        {
            accessKeyId = MinioFixture.User,
            secretAccessKey = MinioFixture.Password,
        });
        var accessSentinel = MinioFixture.User;
        var secretSentinel = MinioFixture.Password;

        await using var host = MediaApiHost.StartS3(
            database,
            Clock,
            mediaRoot,
            new S3HostOptions(MinioEndpoint(), bucket, "us-east-1", true, string.Empty, credentialId),
            credentialSource: null,
            references: new MediaReferenceScript(),
            ids: new SequenceMediaIdSource(new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee10")),
            credentialIds: new SequenceCredentialIdSource(credentialId),
            keyFile: keyFile.FilePath);

        using var owner = await OwnerClient.CreateAsync(host);

        using (var saved = await owner.SendAsync(await owner.Put(
            "/api/settings/credentials/s3",
            new { value = document },
            0)))
        {
            Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        }

        // The stored row, read straight out of the bytea columns: neither half of the sentinel
        // appears at any offset, and no text column of the table carries it either.
        var row = await CredentialRows.ReadByProviderAsync(database, InstallationId, "s3");
        Assert.NotNull(row);
        Assert.Equal(credentialId, row.Id);

        var accessBytes = Encoding.UTF8.GetBytes(accessSentinel);
        var secretBytes = Encoding.UTF8.GetBytes(secretSentinel);
        Assert.False(CredentialRows.Contains(row.Ciphertext, accessBytes), "the ciphertext holds the access key");
        Assert.False(CredentialRows.Contains(row.Ciphertext, secretBytes), "the ciphertext holds the secret key");
        Assert.False(CredentialRows.Contains(row.Nonce, accessBytes), "the nonce holds the access key");
        Assert.False(CredentialRows.Contains(row.Nonce, secretBytes), "the nonce holds the secret key");
        Assert.DoesNotContain(accessSentinel, await CredentialRows.DumpAllTextAsync(database), StringComparison.Ordinal);
        Assert.DoesNotContain(secretSentinel, await CredentialRows.DumpAllTextAsync(database), StringComparison.Ordinal);

        // The production bridge opens the configured reference and the real factory builds a
        // working backend from it: an upload through the API lands bytes in the bucket.
        var projectId = await CreateProjectAsync(owner, "Ledger");
        var png = TestPng.Create(width: 7, height: 4);
        var expectedHash = Sha256Hex(png);

        string storageKey;
        using (var uploaded = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            png,
            "image/png")))
        {
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

            var body = await uploaded.Content.ReadAsStringAsync();
            Assert.DoesNotContain(accessSentinel, body, StringComparison.Ordinal);
            Assert.DoesNotContain(secretSentinel, body, StringComparison.Ordinal);

            using var uploadDocument = JsonDocument.Parse(body);
            storageKey = uploadDocument.RootElement.GetProperty("storageKey").GetString()!;
        }

        var stored = await MinioGetAsync(bucket, storageKey);
        Assert.Equal(expectedHash, Sha256Hex(stored));
    }

    [Fact]
    public async Task S3_credential_reference_to_another_provider_is_refused_without_a_secret_in_the_message()
    {
        Assert.True(postgres.IsRealPostgres, "the cloud object storage scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("s3_vault_wrong_provider");
        var mediaRoot = TemporaryRoot();
        var githubId = new Guid("bbbbbbbb-cccc-dddd-eeee-ffffffff0002");

        using var keyFile = OperatorKeyFile.Create();

        // The operator points the S3 backend at a github credential: the backend must refuse to
        // turn it into keys, and the refusal must name no secret.
        await using var host = MediaApiHost.StartS3(
            database,
            Clock,
            mediaRoot,
            new S3HostOptions("http://127.0.0.1:9", "test-bucket", "us-east-1", true, string.Empty, githubId),
            references: new MediaReferenceScript(),
            keyFile: keyFile.FilePath);

        using var owner = await OwnerClient.CreateAsync(host);

        const string githubSentinel = "github-sentinel-token-4b8e12";
        using (var saved = await owner.SendAsync(await owner.Put(
            "/api/settings/credentials/github",
            new { value = githubSentinel },
            0)))
        {
            Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        }

        using var scope = host.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IS3BackendFactory>();

        var failure = await Assert.ThrowsAsync<MediaStorageUnavailableException>(() => factory.CreateAsync());
        Assert.DoesNotContain(githubSentinel, failure.Message, StringComparison.Ordinal);

        // A reference to nothing is the same refusal, with the same silence about values.
        await using var missing = MediaApiHost.StartS3(
            database,
            Clock,
            mediaRoot,
            new S3HostOptions(
                "http://127.0.0.1:9",
                "test-bucket",
                "us-east-1",
                true,
                string.Empty,
                new Guid("bbbbbbbb-cccc-dddd-eeee-ffffffff0003")),
            keyFile: keyFile.FilePath);

        using var missingScope = missing.Services.CreateScope();
        var missingFactory = missingScope.ServiceProvider.GetRequiredService<IS3BackendFactory>();

        var absent = await Assert.ThrowsAsync<MediaStorageUnavailableException>(() => missingFactory.CreateAsync());
        Assert.DoesNotContain(githubSentinel, absent.Message, StringComparison.Ordinal);
    }

    // ===========================================================================================
    // CLOUD-OBJECT-STORAGE-001 media_in_use: evidence that references an S3 image holds it, on
    // either backend through the same checker.
    // ===========================================================================================

    [Fact]
    public async Task S3_referenced_media_is_refused_with_media_in_use()
    {
        Assert.True(postgres.IsRealPostgres, "the cloud object storage scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("s3_in_use");
        var mediaRoot = TemporaryRoot();
        var mediaId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee11");
        var png = TestPng.Create(width: 8, height: 5);
        var backend = new S3TestBackend();

        var references = new MediaReferenceScript();
        var script = new S3BackendScript(backend);
        await using var host = MediaApiHost.StartS3(
            database,
            Clock,
            mediaRoot,
            S3HostOptions.UnusedBackend(),
            backendFactory: script,
            references: references,
            ids: new SequenceMediaIdSource(mediaId));

        using var owner = await OwnerClient.CreateAsync(host);
        var projectId = await CreateProjectAsync(owner, "Ledger");

        using (var uploaded = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            png,
            "image/png")))
        {
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        // While evidence references the image the retire is refused and nothing moves: the row is
        // still ready and the object is still there.
        references.Reference(mediaId);
        var readyKey = MediaValues.ReadyStorageKey(InstallationId, Guid.Parse(projectId), mediaId);

        using (var refused = await owner.SendAsync(await owner.Archive($"/api/media/{mediaId:D}", null)))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            using var document = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("media_in_use", document.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal("ready", (await ReadMediaRowAsync(database, mediaId))!.Status);
        Assert.True(backend.Objects.ContainsKey(readyKey));
        Assert.Contains(mediaId, references.Asked);

        // Released, the same retire succeeds and the object is gone.
        references.Release(mediaId);

        using (var retired = await owner.SendAsync(await owner.Archive($"/api/media/{mediaId:D}", null)))
        {
            Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
        }

        Assert.Equal("deleted", (await ReadMediaRowAsync(database, mediaId))!.Status);
        Assert.False(backend.Objects.ContainsKey(readyKey));
    }

    // ===========================================================================================
    // CLOUD-OBJECT-STORAGE-002 purge parity: an interrupted S3 upload leaves a recorded pending
    // row and an unreferenced object, and the sweep reconciles both idempotently.
    // ===========================================================================================

    [Fact]
    public async Task S3_interrupted_upload_leaves_a_reconcilable_pending_row()
    {
        Assert.True(postgres.IsRealPostgres, "the cloud object storage scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("s3_purge");
        var mediaRoot = TemporaryRoot();
        var mediaId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee12");
        var png = TestPng.Create(width: 8, height: 5);
        var expectedHash = Sha256Hex(png);
        var backend = new S3TestBackend { FailPromotion = true };

        var script = new S3BackendScript(backend);
        await using var host = MediaApiHost.StartS3(
            database,
            Clock,
            mediaRoot,
            S3HostOptions.UnusedBackend(),
            backendFactory: script,
            ids: new SequenceMediaIdSource(mediaId));

        using var owner = await OwnerClient.CreateAsync(host);
        var projectId = await CreateProjectAsync(owner, "Ledger");
        var parsedProjectId = Guid.Parse(projectId);

        // The promotion write fails: 503, no row, and the staged object is released by the call's
        // own compensation.
        using (var failed = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            png,
            "image/png")))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        }

        Assert.Equal(0, await CountMediaRowsAsync(database));

        // A pending row is now what an interrupted upload would have left behind had it got as far
        // as recording itself, staged two days before the frozen clock. It names bytes that exist
        // and is still not servable.
        var pendingKey = MediaValues.PendingStorageKey(InstallationId, parsedProjectId, mediaId);
        backend.FailPromotion = false;
        backend.Objects[pendingKey] = png;

        // Staged two days before the frozen clock, so the sweep below is the design's real
        // condition - staged longer ago than the retention window - and not an arbitrary cutoff.
        await InsertMediaRowAsync(
            database,
            mediaId,
            InstallationId,
            parsedProjectId,
            pendingKey,
            "pending",
            expectedHash,
            "s3",
            Clock.AddHours(-48));

        using (var read = await owner.GetAsync($"/api/media/{mediaId:D}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }

        // The reconciliation removes the orphan and retires the row, and is idempotent.
        using (var scope = host.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IMediaStore>();

            Assert.Equal(1, await store.PurgeUnreferencedAsync(Clock.AddHours(-24), 100));
            Assert.False(backend.Objects.ContainsKey(pendingKey));
            Assert.Equal(0, await store.PurgeUnreferencedAsync(Clock.AddHours(-24), 100));

            var swept = await ReadMediaRowAsync(database, mediaId);
            Assert.Equal("deleted", swept!.Status);
        }
    }

    // ===========================================================================================
    // Helpers.
    // ===========================================================================================

    private string MinioEndpoint() => minio.Endpoint;

    private S3HostOptions MinioOptions(string bucket, Guid? credentialId = null) =>
        new(minio.Endpoint, bucket, "us-east-1", true, string.Empty, credentialId ?? Guid.NewGuid());

    private AmazonS3Client MinioClient() => minio.CreateClient();

    private Task<string> ProvisionedBucketAsync(string purpose) => minio.ProvisionBucketAsync(purpose);

    private Task<byte[]> MinioGetAsync(string bucket, string key) => minio.GetObjectAsync(bucket, key);

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

    private static async Task<string> CreateProjectAsync(OwnerClient owner, string name)
    {
        using var response = await owner.SendAsync(
            await owner.Post("/api/projects", new { name, description = $"{name} description." }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("id")
            .GetString()!;
    }

    private string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"chronicleaf-s3-{Guid.NewGuid():N}", "media");
        Directory.CreateDirectory(root);
        _temporaryRoots.Add(root);

        return root;
    }

    private static async Task InsertMediaRowAsync(
        string database,
        Guid mediaId,
        Guid scopeId,
        Guid projectId,
        string storageKey,
        string status,
        string sha256,
        string provider = "local",
        DateTimeOffset? createdAt = null) =>
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            $"""
             INSERT INTO media (id, scope_id, project_id, storage_key, media_type, byte_length, sha256, status, created_at, owner_operation_id, revision, provider_id)
             VALUES ('{mediaId:D}', '{scopeId:D}', '{projectId:D}', '{storageKey}', '{MediaTypes.Png}', 16, '{sha256}', '{status}', '{(createdAt ?? Clock).UtcDateTime:O}', NULL, 1, '{provider}')
             """);

    private static async Task<S3StoredMediaRow?> ReadMediaRowAsync(string database, Guid mediaId)
    {
        var text = await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT media_type || '|' || byte_length || '|' || sha256 || '|' || status || '|' || storage_key || '|' || revision || '|' || provider_id || '|' || project_id "
            + $"FROM media WHERE id = '{mediaId:D}'");

        if (text is null)
        {
            return null;
        }

        var parts = text.Split('|');

        return new S3StoredMediaRow(parts[0], long.Parse(parts[1]), parts[2], parts[3], parts[4], long.Parse(parts[5]), parts[6], Guid.Parse(parts[7]));
    }

    private static async Task<MediaMigrationCopy?> ReadLedgerAsync(
        MediaApiHost host,
        Guid scopeId,
        Guid mediaId)
    {
        using var scope = host.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<IMediaMigrationRepository>()
            .FindAsync(scopeId, mediaId);
    }

    private static Task<long> CountMediaRowsAsync(string database) =>
        ChronicleafPostgresFixture.CountRowsAsync(database, "media");

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));
}

/// <summary>The media row as PostgreSQL stores it, read over a raw connection.</summary>
internal sealed record S3StoredMediaRow(
    string MediaType,
    long ByteLength,
    string Sha256,
    string Status,
    string StorageKey,
    long Revision,
    string ProviderId,
    Guid ProjectId);

/// <summary>
/// The S3 side of the cloud object storage scenarios: an in-memory object store behind the
/// production S3 store, link service, migration service and router.
/// </summary>
/// <remarks>
/// It speaks the adopted platform's own <see cref="IObjectStorage"/> faithfully - uploads land
/// under the requested key, metadata reports what was written, a missing object is a not-found and
/// presigns carry the requested bound - so the key shape, the scope checks, the two-phase sequence
/// and the ledger the production code performs are exercised rather than assumed. The two faults
/// are the two failures no real backend produces on demand: a refused write and a write that does
/// not match what was sent. Wire conformance itself is the MinIO scenarios' claim, not this
/// script's.
/// </remarks>
public sealed class S3TestBackend : IObjectStorage
{
    private readonly Lock _gate = new();

    /// <summary>The objects this backend holds, by portable key.</summary>
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Keys whose next upload lands corrupted, so verification must refuse them.</summary>
    public HashSet<string> CorruptKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>When true, every upload is reported unavailable.</summary>
    public bool FailUpload { get; set; }

    /// <summary>When true, the promotion write of each call is reported unavailable.</summary>
    public bool FailPromotion { get; set; }

    /// <summary>How many uploads this backend has seen. Promotion is the second upload of one call.</summary>
    public int Uploads { get; private set; }

    /// <summary>How many downloads this backend has served.</summary>
    public int Downloads { get; private set; }

    /// <summary>How many presigns this backend has minted.</summary>
    public int Presigns { get; private set; }

    public Task<StorageOutcome> UploadAsync(StorageUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        int call;
        lock (_gate)
        {
            call = ++_uploadCount;
            Uploads = _uploadCount;
        }

        if (FailUpload || (FailPromotion && call % 2 == 0))
        {
            return Task.FromResult(StorageOutcome.Unavailable(new StorageFailure(
                "storage.provider_unavailable",
                "The injected S3 upload failure.",
                true)));
        }

        using var buffer = new MemoryStream();
        request.Content.CopyTo(buffer);
        var bytes = buffer.ToArray();

        lock (_gate)
        {
            if (CorruptKeys.Contains(request.Key.Value) && bytes.Length > 0)
            {
                bytes[0] ^= 0xFF;
            }

            Objects[request.Key.Value] = bytes;
        }

        return Task.FromResult(StorageOutcome.Succeeded());
    }

    public Task<StorageDownloadResult> DownloadAsync(StorageObjectKey key, CancellationToken cancellationToken = default)
    {
        byte[]? bytes;
        lock (_gate)
        {
            Downloads++;
            Objects.TryGetValue(key.Value, out bytes);
        }

        if (bytes is null)
        {
            return Task.FromResult(StorageDownloadResult.NotFound());
        }

        var content = new MemoryStream(bytes, writable: false);
        var metadata = new StorageObjectMetadata(bytes.LongLength, MediaTypes.Png, DateTimeOffset.UtcNow, null);

        return Task.FromResult(StorageDownloadResult.Succeeded(new StorageReadResult(content, metadata)));
    }

    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<StorageObjectMetadata?>(Objects.TryGetValue(key.Value, out var bytes)
                ? new StorageObjectMetadata(bytes.LongLength, MediaTypes.Png, DateTimeOffset.UtcNow, null)
                : null);
        }
    }

    public Task<StorageOutcome> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Objects.Remove(key.Value);
        }

        // A missing object is success, which is the design's rule and the reason a second release
        // after a successful one is not an error.
        return Task.FromResult(StorageOutcome.Succeeded());
    }

    public Task<(StorageOutcome Outcome, PresignedOperation? Operation)> PresignAsync(
        PresignRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            Presigns++;
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(request.Lifetime);

        return Task.FromResult((
            StorageOutcome.Succeeded(),
            (PresignedOperation?)new PresignedOperation(
                new Uri($"https://s3.example/{request.Key.Value}?expires={expiresAt.ToUnixTimeSeconds()}"),
                request.Key,
                request.Operation,
                expiresAt,
                request.ContentType,
                request.MaximumBytes)));
    }

    private int _uploadCount;
}

/// <summary>
/// The backend factory the S3 scenarios drive: one scripted backend per host, counted and
/// released.
/// </summary>
/// <remarks>
/// It replaces the production factory wholesale, which is the point: the stores, the link service,
/// the migration service and the router are the production ones in every scenario, and only the
/// bytes' resting place is the test's. Creation is counted so a scenario can prove a refused
/// request never opened a backend at all.
/// </remarks>
public sealed class S3BackendScript : IS3BackendFactory
{
    private readonly IObjectStorage _inner;
    private int _created;
    private int _released;

    public S3BackendScript(IObjectStorage inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>How many backends this script has built.</summary>
    public int Created => Volatile.Read(ref _created);

    /// <summary>How many backends have been disposed.</summary>
    public int Released => Volatile.Read(ref _released);

    public Task<OwnedS3Backend> CreateAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _created);

        return Task.FromResult(new OwnedS3Backend(
            _inner,
            () =>
            {
                Interlocked.Increment(ref _released);

                return ValueTask.CompletedTask;
            }));
    }
}

/// <summary>
/// The credential source the MinIO scenarios drive: fixed MinIO keys, leased and returned.
/// </summary>
/// <remarks>
/// It replaces only the vault bridge, so the production backend factory - the client it builds,
/// the options it carries and the platform adapter it wraps - is exercised rather than assumed.
/// The vault's own half of the reference is the dedicated vault scenario's claim, not this
/// script's: what crosses here are test-only MinIO keys, never a real secret.
/// </remarks>
public sealed class S3CredentialScript(string accessKey, string secretKey) : IS3CredentialSource
{
    private readonly string _accessKey = accessKey ?? throw new ArgumentNullException(nameof(accessKey));
    private readonly string _secretKey = secretKey ?? throw new ArgumentNullException(nameof(secretKey));

    public Task<S3CredentialLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var document = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { accessKeyId = _accessKey, secretAccessKey = _secretKey }));

        return Task.FromResult(new S3CredentialLease(
            Guid.NewGuid(),
            Guid.NewGuid(),
            S3MediaOptions.CredentialProvider,
            document));
    }
}
