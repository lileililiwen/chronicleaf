using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// LOCAL-MEDIA-STORAGE-001 and LOCAL-MEDIA-STORAGE-002 against a real disposable PostgreSQL and a
/// real temporary filesystem, through the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is simulated where it matters. The bytes are written by the adopted Local storage
/// adapter to a real directory under <c>/tmp</c>, the rows are produced by a real PostgreSQL server,
/// and every claim is read back over the wire - a status code, a header, a body - or over a real file
/// on disk. There is no in-memory storage provider, no EF in-memory provider and no stubbed store;
/// those would be refused as evidence for exactly the same reason the delivered packages refuse them:
/// the claim under test is that a file on a disk stays consistent with a row in a database, and
/// neither half of that can be faked.
/// </para>
/// <para>
/// Every test gets its own database and its own MediaRoot, both temporary, and the MediaRoot is
/// outside the sibling <c>outside</c> directory the boundary scenario plants a file in - so "nothing
/// outside MediaRoot was touched" is a fact about a real filesystem layout rather than a claim about
/// a mock's call log. The clock is injected and frozen, so every stored instant is deterministic.
/// The identities are constants: the installation and its scope, the foreign scope, the project and
/// the media ids a scenario declares in advance so the key it expects is a fact rather than a guess.
/// </para>
/// <para>
/// The three seams the fixtures expose are stated in <c>MediaApiHost</c>: a reference script standing
/// in for the evidence references a later package owns, a fault injector for the storage provider, and
/// a fixed id sequence. Each replaces exactly one registration. The store, the provider, the
/// repository, the routes and the access boundary are the production ones in every scenario.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class LocalMediaStorageTests(ChronicleafPostgresFixture postgres)
    : IDisposable
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("6e1c8b3a-2f47-4d5e-9a10-2b3c4d5e6f71");

    /// <summary>A second scope, used only to prove media outside the caller's scope is not found.</summary>
    private static readonly Guid ForeignScopeId = new("7f2d9c4b-3058-4e6f-8b21-3c4d5e6f7082");

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
    // LOCAL-MEDIA-STORAGE-001 Success: a valid bounded PNG is stored with the type and hash its
    // bytes actually have, and the same bytes come back through the API.
    // ===========================================================================================

    [Fact]
    public async Task Valid_png_is_stored_with_its_decoded_type_and_hash_and_reads_back_identically()
    {
        Assert.True(postgres.IsRealPostgres, "the media scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("media_success");
        var mediaRoot = TemporaryRoot();
        var mediaId = new Guid("11111111-2222-3333-4444-555555555501");
        var png = TestPng.Create(width: 12, height: 7);

        await using var host = MediaApiHost.StartWithReferences(
            database,
            Clock,
            mediaRoot,
            new MediaReferenceScript(),
            new SequenceMediaIdSource(mediaId));

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
        Assert.Equal($"/api/media/{mediaId:D}", uploaded.Headers.Location?.ToString());
        Assert.Equal("\"1\"", uploaded.Headers.ETag?.ToString());

        // The decoded type, not the declared one and not the provider's guess from the extension.
        Assert.Equal(MediaTypes.Png, root.GetProperty("mediaType").GetString());
        Assert.Equal(MediaStatus.Ready, root.GetProperty("status").GetString());
        Assert.Equal(png.Length, root.GetProperty("byteLength").GetInt64());

        // The hash of the bytes that were sent, which is the only thing a hash here can mean.
        Assert.Equal(expectedHash, root.GetProperty("sha256").GetString());
        Assert.Equal(expectedHash, Sha256Hex(png));

        // The key is server-generated, scoped, and names the project it belongs to.
        var storageKey = root.GetProperty("storageKey").GetString()!;
        Assert.Equal(
            $"scopes/{InstallationId:D}/projects/{projectId:D}/media/{MediaStatus.Ready}/{mediaId:D}.png",
            storageKey);

        // The stored row agrees with what the caller was told, and with the bytes on disk. Read over a
        // raw connection so the assertion is about PostgreSQL's own row and not a tracked entity.
        var row = await ReadMediaRowAsync(database, mediaId);
        Assert.NotNull(row);
        Assert.Equal(MediaTypes.Png, row!.MediaType);
        Assert.Equal(expectedHash, row.Sha256);
        Assert.Equal(png.Length, row.ByteLength);
        Assert.Equal(MediaStatus.Ready, row.Status);
        Assert.Equal(storageKey, row.StorageKey);
        Assert.Equal(Clock, await ReadCreatedAtAsync(database, mediaId));

        var onDisk = Path.Combine(mediaRoot, storageKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(onDisk), $"the ready object must exist at {storageKey}");
        Assert.Equal(expectedHash, Sha256Hex(await File.ReadAllBytesAsync(onDisk)));

        // The bytes read back through the API are the bytes that were stored.
        using var read = await owner.GetAsync($"/api/media/{mediaId:D}");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(MediaTypes.Png, read.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", read.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(expectedHash, Sha256Hex(await read.Content.ReadAsByteArrayAsync()));
        Assert.Equal(png, await read.Content.ReadAsByteArrayAsync());
    }

    // ===========================================================================================
    // LOCAL-MEDIA-STORAGE-001 Failure: an id in another scope is a 404 with no bytes in the body.
    // ===========================================================================================

    [Fact]
    public async Task Media_id_in_another_scope_is_not_found_and_the_body_carries_no_bytes()
    {
        Assert.True(postgres.IsRealPostgres, "the media scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("media_foreign_scope");
        var mediaRoot = TemporaryRoot();
        var foreignId = new Guid("11111111-2222-3333-4444-555555555502");

        // Real bytes really exist on disk for the foreign record, so a leak would be visible.
        var foreignKey = $"scopes/{ForeignScopeId:D}/projects/{Guid.NewGuid():D}/media/{MediaStatus.Ready}/{foreignId:D}.png";
        var foreignFile = Path.Combine(mediaRoot, foreignKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(foreignFile)!);
        var foreignBytes = TestPng.Create(4, 4);
        await File.WriteAllBytesAsync(foreignFile, foreignBytes);

        await using var host = MediaApiHost.Start(database, Clock, mediaRoot);
        using var owner = await OwnerClient.CreateAsync(host);

        // A real project, because the media table's foreign key to projects is a real constraint and
        // a scenario that violated it would be testing the violation rather than the boundary. What
        // makes the row foreign is its scope, which is the only thing a read is filtered by.
        var foreignProjectId = await CreateProjectAsync(owner, "Foreign scope");

        await InsertMediaRowAsync(
            database,
            foreignId,
            ForeignScopeId,
            Guid.Parse(foreignProjectId),
            foreignKey,
            MediaStatus.Ready,
            Sha256Hex(foreignBytes));

        using var response = await owner.GetAsync($"/api/media/{foreignId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        // One code for absent, out-of-scope and unservable, so a caller cannot probe for another
        // installation's media by comparing codes.
        Assert.Equal(MediaError.NotFound, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));

        // No bytes, and specifically not the bytes that are sitting on disk for that id.
        Assert.DoesNotContain("PNG", body, StringComparison.Ordinal);
        Assert.NotEqual(foreignBytes, EncodingOf(body));
        Assert.True(File.Exists(foreignFile), "a refused read must not remove the object");
        Assert.Equal(Sha256Hex(foreignBytes), Sha256Hex(await File.ReadAllBytesAsync(foreignFile)));
    }

    // ===========================================================================================
    // LOCAL-MEDIA-STORAGE-001 Boundary: a traversal or symlinked path reads and writes nothing
    // outside MediaRoot.
    // ===========================================================================================

    [Fact]
    public async Task Traversal_and_symlinked_paths_never_reach_a_file_outside_the_media_root()
    {
        Assert.True(postgres.IsRealPostgres, "the media scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("media_traversal");
        var (mediaRoot, outside) = TemporaryRootPair();
        var traversalId = new Guid("11111111-2222-3333-4444-555555555503");
        var symlinkId = new Guid("11111111-2222-3333-4444-555555555504");

        // A real file outside MediaRoot, with contents and an instant that must survive untouched.
        var outsideFile = Path.Combine(outside, "secret.png");
        var outsideBytes = TestPng.Create(3, 3);
        await File.WriteAllBytesAsync(outsideFile, outsideBytes);
        var outsideStamp = File.GetLastWriteTimeUtc(outsideFile);

        // A link inside the MediaRoot whose target is outside it. This is the case a purely textual
        // containment check passes and a resolved-path check refuses: the composed path really does
        // begin with the root, and reading or writing it still lands in /tmp's other directory.
        var escapeLink = Path.Combine(mediaRoot, "escape");
        Directory.CreateSymbolicLink(escapeLink, outside);

        await using var host = MediaApiHost.Start(database, Clock, mediaRoot);
        using var owner = await OwnerClient.CreateAsync(host);

        // A real project for each injected row: the media table's foreign key is enforced, and these
        // scenarios are about where a key resolves, not about whether a constraint can be ignored.
        var linkedProjectId = await CreateProjectAsync(owner, "Linked");
        var ordinaryProjectId = await CreateProjectAsync(owner, "Ordinary");

        // 1. A traversal key cannot be built. The adopted StorageObjectKey is a value type, so a
        //    hostile string fails to become a key rather than becoming a bad one.
        Assert.Throws<ArgumentException>(() => new Platform.Storage.Keys.StorageObjectKey(
            $"../{outside}/secret.png"));
        Assert.Throws<ArgumentException>(() => new Platform.Storage.Keys.StorageObjectKey(
            $"escape/../../{outside}/secret.png"));
        Assert.Throws<ArgumentException>(() => new Platform.Storage.Keys.StorageObjectKey("escape//secret.png"));

        // 2. A key that is valid but whose first segment is a link out of the root is refused by the
        //    resolved-path guard, which is the same guard the read path runs on a stored key.
        var escaping = new Platform.Storage.Keys.StorageObjectKey("escape/secret.png");
        Assert.Throws<MediaOutsideRootException>(() => host.ResolvedRoot().ResolveInside(escaping));
        Assert.False(host.ResolvedRoot().TryResolveInside("escape/secret.png", out _));

        // 3. Over the wire: a row whose stored key goes through the link is a 404 with no bytes, and
        //    the file outside is neither read nor overwritten.
        var linkedKey = $"escape/secret.png";
        await InsertMediaRowAsync(
            database,
            symlinkId,
            InstallationId,
            Guid.Parse(linkedProjectId),
            linkedKey,
            MediaStatus.Ready,
            Sha256Hex(outsideBytes));

        using (var read = await owner.GetAsync($"/api/media/{symlinkId:D}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

            var body = await read.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal(MediaError.NotFound, document.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("PNG", body, StringComparison.Ordinal);
        }

        Assert.Equal(outsideBytes, await File.ReadAllBytesAsync(outsideFile));
        Assert.Equal(outsideStamp, File.GetLastWriteTimeUtc(outsideFile));

        // 4. Deleting such a record refuses before it writes anything and does not remove the file.
        using (var retired = await owner.SendAsync(await owner.Archive($"/api/media/{symlinkId:D}", null)))
        {
            Assert.Equal(HttpStatusCode.NotFound, retired.StatusCode);
        }

        Assert.True(File.Exists(outsideFile), "a refused retire must not delete a file outside the root");
        Assert.Equal(outsideBytes, await File.ReadAllBytesAsync(outsideFile));
        Assert.Equal(outsideStamp, File.GetLastWriteTimeUtc(outsideFile));

        // The row was not touched: the guard runs before the transition, so a 404 here did not retire it.
        var untouched = await ReadMediaRowAsync(database, symlinkId);
        Assert.Equal(MediaStatus.Ready, untouched!.Status);
        Assert.Equal(1, untouched.Revision);

        // 5. The same for a key that tries to climb out with a legal-looking prefix. The key itself is
        //    rejected by the platform, and a hand-written key that somehow reached the guard is refused
        //    by the containment check.
        await InsertMediaRowAsync(
            database,
            traversalId,
            InstallationId,
            Guid.Parse(ordinaryProjectId),
            $"scopes/{InstallationId:D}/projects/{Guid.Parse(ordinaryProjectId):D}/media/{MediaStatus.Ready}/{traversalId:D}.png",
            MediaStatus.Ready,
            Sha256Hex(outsideBytes));

        using (var read = await owner.GetAsync($"/api/media/{traversalId:D}"))
        {
            // A legal key that names nothing is also a 404: an absent object and a refused key are the
            // same answer, so neither discloses that the other exists.
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }

        Assert.Equal(outsideBytes, await File.ReadAllBytesAsync(outsideFile));
        Assert.Equal(outsideStamp, File.GetLastWriteTimeUtc(outsideFile));

        // 6. Nothing an ordinary upload writes leaves the root. One real upload, then a full sweep of
        //    both trees: every new file is under MediaRoot and the outside tree is unchanged.
        var hostWithIds = MediaApiHost.StartWithReferences(
            database,
            Clock,
            mediaRoot,
            new MediaReferenceScript(),
            new SequenceMediaIdSource(new Guid("11111111-2222-3333-4444-555555555505")));

        await using (hostWithIds)
        {
            using var writer = await OwnerClient.CreateAsync(hostWithIds);
            var projectId = await CreateProjectAsync(writer, "Scoping");
            var png = TestPng.Create(5, 5);

            using var uploaded = await writer.SendAsync(await writer.PostBytes(
                $"/api/media?projectId={projectId}",
                png,
                "image/png"));

            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        Assert.Equal([outsideFile], Directory.GetFiles(outside, "*", SearchOption.AllDirectories).Order());
        Assert.NotEmpty(Directory.GetFiles(mediaRoot, "*", SearchOption.AllDirectories));
    }

    // ===========================================================================================
    // LOCAL-MEDIA-STORAGE-002 Success: a ready image linked into a reference is still readable
    // after the process that stored it is gone.
    // ===========================================================================================

    [Fact]
    public async Task Ready_media_linked_into_a_reference_is_still_readable_after_a_restart()
    {
        Assert.True(postgres.IsRealPostgres, "the media scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("media_restart");
        var mediaRoot = TemporaryRoot();
        var mediaId = new Guid("11111111-2222-3333-4444-555555555506");
        var png = TestPng.Create(9, 9);
        var expectedHash = Sha256Hex(png);
        var storageKey = string.Empty;

        // One script for both processes. The reference is state that outlives the process - it lives
        // in whatever the reference owner is - so the same instance is handed to each host, which is
        // what makes the second process's 409 evidence about the reference surviving rather than
        // about a second script that happens to know the answer.
        var references = new MediaReferenceScript();

        // First process: store the image and declare a reference to it, exactly as a later package's
        // evidence link would.
        await using (var first = MediaApiHost.StartWithReferences(
            database,
            Clock,
            mediaRoot,
            references,
            new SequenceMediaIdSource(mediaId)))
        {
            using var owner = await OwnerClient.CreateAsync(first);
            var projectId = await CreateProjectAsync(owner, "Long running");

            using var uploaded = await owner.SendAsync(await owner.PostBytes(
                $"/api/media?projectId={projectId}",
                png,
                "image/png"));

            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

            using var document = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
            storageKey = document.RootElement.GetProperty("storageKey").GetString()!;

            references.Reference(mediaId);
        }

        // The host is disposed. Nothing in memory survives, and the MediaRoot is still on disk.
        Assert.True(File.Exists(Path.Combine(mediaRoot, storageKey.Replace('/', Path.DirectorySeparatorChar))));

        // Second process: a brand new host, a brand new service provider, the same database and the
        // same MediaRoot. This is the restart, and it is a real one.
        await using var second = MediaApiHost.StartWithReferences(database, Clock, mediaRoot, references);
        using var restarted = await OwnerClient.CreateAsync(second);

        using var read = await restarted.GetAsync($"/api/media/{mediaId:D}");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(MediaTypes.Png, read.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedHash, Sha256Hex(await read.Content.ReadAsByteArrayAsync()));
        Assert.Equal(png, await read.Content.ReadAsByteArrayAsync());

        // Still linked, so still not deletable: the reference survived the restart too.
        using var retire = await restarted.SendAsync(await restarted.Archive($"/api/media/{mediaId:D}", null));

        Assert.Equal(HttpStatusCode.Conflict, retire.StatusCode);

        using var conflict = JsonDocument.Parse(await retire.Content.ReadAsStringAsync());
        Assert.Equal(MediaError.MediaInUse, conflict.RootElement.GetProperty("code").GetString());
    }

    // ===========================================================================================
    // LOCAL-MEDIA-STORAGE-002 Failure: a failure during promotion or during registration leaves no
    // ready row pointing at missing bytes.
    // ===========================================================================================

    [Fact]
    public async Task Promotion_failure_leaves_no_ready_row_and_no_published_orphan()
    {
        Assert.True(postgres.IsRealPostgres, "the media scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("media_promotion_failure");
        var mediaRoot = TemporaryRoot();
        // The provider accepts the staged write and then fails the promotion, which is the exact
        // moment a process would be killed or a disk would fill. Everything else is the real adapter
        // over a real directory.
        var faults = new MediaFaults { FailPromotion = true };

        await using (var host = MediaApiHost.StartWithFaults(database, Clock, mediaRoot, faults))
        {

            using var owner = await OwnerClient.CreateAsync(host);
            var projectId = await CreateProjectAsync(owner, "Interrupted");
            var png = TestPng.Create(6, 6);

            using var response = await owner.SendAsync(await owner.PostBytes(
                $"/api/media?projectId={projectId}",
                png,
                "image/png"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(MediaError.StorageUnavailable, document.RootElement.GetProperty("code").GetString());

            // No row at all, so in particular no ready row naming a key whose bytes are missing.
            Assert.Equal(0, await CountMediaRowsAsync(database));
            Assert.Null(await ReadMediaRowAsync(database, Guid.NewGuid()));

            // And no published orphan: both the staged and the promoted object were released, so the
            // MediaRoot has no file at all.
            Assert.Empty(Directory.GetFiles(mediaRoot, "*", SearchOption.AllDirectories));

            // The id that was generated is unknown to the caller and resolves to nothing, which is the
            // honest outcome for a failed upload: no id was ever returned.
            using var read = await owner.GetAsync($"/api/media/{Guid.Empty:D}");
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }
    }

    [Fact]
    public async Task Registration_failure_releases_the_bytes_and_leaves_no_ready_row()
    {
        Assert.True(postgres.IsRealPostgres, "the media scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("media_registration_failure");
        var mediaRoot = TemporaryRoot();

        // The failure is injected at the database, after the bytes have been staged, promoted and
        // verified. That is the only ordering in which a ready row could ever have pointed at missing
        // bytes, so it is the ordering this scenario has to break.
        await using var host = MediaApiHost.StartWithRepositoryFaults(database, Clock, mediaRoot);
        {
            using var owner = await OwnerClient.CreateAsync(host);
            var projectId = await CreateProjectAsync(owner, "Registration");
            var png = TestPng.Create(6, 6);

            using var response = await owner.SendAsync(await owner.PostBytes(
                $"/api/media?projectId={projectId}",
                png,
                "image/png"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("database_unavailable", document.RootElement.GetProperty("code").GetString());

            // The row never committed, and the bytes it would have named are gone.
            Assert.Equal(0, await CountMediaRowsAsync(database));
            Assert.Empty(Directory.GetFiles(mediaRoot, "*", SearchOption.AllDirectories));
        }
    }

    // ===========================================================================================
    // LOCAL-MEDIA-STORAGE-002 Boundary: deleting referenced media is a 409 and the bytes survive.
    // ===========================================================================================

    [Fact]
    public async Task Deleting_referenced_media_returns_media_in_use_and_the_bytes_survive()
    {
        Assert.True(postgres.IsRealPostgres, "the media scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("media_in_use");
        var mediaRoot = TemporaryRoot();
        var mediaId = new Guid("11111111-2222-3333-4444-555555555507");
        var references = new MediaReferenceScript();

        await using var host = MediaApiHost.StartWithReferences(
            database,
            Clock,
            mediaRoot,
            references,
            new SequenceMediaIdSource(mediaId));

        using var owner = await OwnerClient.CreateAsync(host);
        var projectId = await CreateProjectAsync(owner, "Referenced");
        var png = TestPng.Create(8, 8);
        var expectedHash = Sha256Hex(png);

        using var uploaded = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            png,
            "image/png"));

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        var storageKey = JsonDocument
            .Parse(await uploaded.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("storageKey")
            .GetString()!;

        var onDisk = Path.Combine(mediaRoot, storageKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(onDisk));

        // The reference exists now, exactly as an evidence link would.
        references.Reference(mediaId);

        using (var refused = await owner.SendAsync(await owner.Archive($"/api/media/{mediaId:D}", null)))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            using var document = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("media_in_use", document.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                MediaError.MediaInUse,
                document.RootElement.GetProperty("code").GetString());
        }

        // The reference check ran, and it ran for this media.
        Assert.Contains(mediaId, references.Asked);

        // Nothing changed: the row is still ready at its original revision and the bytes are intact.
        var row = await ReadMediaRowAsync(database, mediaId);
        Assert.Equal(MediaStatus.Ready, row!.Status);
        Assert.Equal(1, row.Revision);
        Assert.True(File.Exists(onDisk));
        Assert.Equal(expectedHash, Sha256Hex(await File.ReadAllBytesAsync(onDisk)));

        // And it is still readable, because a refused retire changed nothing.
        using var read = await owner.GetAsync($"/api/media/{mediaId:D}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(png, await read.Content.ReadAsByteArrayAsync());

        // Once the reference is gone the same request is allowed, and then the bytes really are gone.
        references.Release(mediaId);

        using var retired = await owner.SendAsync(await owner.Archive($"/api/media/{mediaId:D}", null));

        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        using var retiredDocument = JsonDocument.Parse(await retired.Content.ReadAsStringAsync());
        Assert.Equal(MediaStatus.Deleted, retiredDocument.RootElement.GetProperty("status").GetString());
        Assert.True(retiredDocument.RootElement.GetProperty("bytesReleased").GetBoolean());
        Assert.Equal(2, retiredDocument.RootElement.GetProperty("revision").GetInt64());

        Assert.False(File.Exists(onDisk));

        using var gone = await owner.GetAsync($"/api/media/{mediaId:D}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    // ===========================================================================================
    // LOCAL-MEDIA-STORAGE-001/002 input boundary: a body that is not an accepted image, and a body
    // over the limit, are both refused with the delivered envelope and nothing is written.
    // ===========================================================================================

    [Fact]
    public async Task Non_png_and_oversized_bodies_are_refused_and_nothing_is_written()
    {
        Assert.True(postgres.IsRealPostgres, "the media scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("media_input_limits");
        var mediaRoot = TemporaryRoot();

        // The byte limit is configured down to 1 KiB so the oversized case is provable without moving
        // 20 MiB through a real socket. The key is the production one and the value goes through the
        // production clamping rule, so this is a limit a deployment could set too.
        const int ConfiguredLimit = 1024;

        await using var host = MediaApiHost.Start(
            database,
            Clock,
            mediaRoot,
            maximumByteLength: ConfiguredLimit);

        using var owner = await OwnerClient.CreateAsync(host);
        var projectId = await CreateProjectAsync(owner, "Limits");

        // 1. Not an image at all, and it says so honestly in its own header.
        using (var response = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            "this is a note to self, not a picture"u8.ToArray(),
            "text/plain")))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(MediaError.UnsupportedMediaType, document.RootElement.GetProperty("code").GetString());
        }

        // 2. A PNG signature followed by nothing. It starts like an image and is not one.
        using (var response = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            MediaImage.PngSignature.ToArray(),
            "image/png")))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }

        // 3. A complete PNG with no terminator, which is what an interrupted upload looks like.
        using (var response = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            TestPng.CreateWithoutTerminator(4, 4),
            "image/png")))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }

        // 4. Valid PNG bytes, but one byte over the limit, so only the size can be the reason. The
        //    limit is configured to something small so the scenario does not have to move 20 MiB.
        using (var response = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId}",
            TestPng.CreateOversizedBy(ConfiguredLimit),
            "image/png")))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(ApiError.PayloadTooLarge, document.RootElement.GetProperty("code").GetString());
        }

        // Nothing was written for any of the four: no row, and not one file under the root.
        Assert.Equal(0, await CountMediaRowsAsync(database));
        Assert.Empty(Directory.GetFiles(mediaRoot, "*", SearchOption.AllDirectories));
    }

    // ===========================================================================================
    // LOCAL-MEDIA-STORAGE-002: a pending upload that never promoted is never served, even though
    // its bytes are on disk.
    // ===========================================================================================

    [Fact]
    public async Task Pending_upload_that_never_promoted_is_never_served()
    {
        Assert.True(postgres.IsRealPostgres, "the media scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("media_pending");
        var mediaRoot = TemporaryRoot();
        var mediaId = new Guid("11111111-2222-3333-4444-55555555550b");
        var png = TestPng.Create(7, 7);
        var expectedHash = Sha256Hex(png);
        var projectId = string.Empty;

        // The promotion fails and so does the release, which is what a process that died between the
        // two looks like from inside the store: the staged object survives on disk and no row is
        // registered.
        var faults = new MediaFaults { FailPromotion = true, FailRelease = true };

        await using (var host = MediaApiHost.StartWithFaults(
            database,
            Clock,
            mediaRoot,
            faults,
            new SequenceMediaIdSource(mediaId)))
        {

            using var owner = await OwnerClient.CreateAsync(host);

            // Captured because the scenario asserts on the staged object's key afterwards, and that
            // key names this project.
            projectId = await CreateProjectAsync(owner, "Interrupted");

            using var response = await owner.SendAsync(await owner.PostBytes(
                $"/api/media?projectId={projectId}",
                png,
                "image/png"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(0, await CountMediaRowsAsync(database));
        }

        var parsedProjectId = Guid.Parse(projectId);

        // The staged bytes really are still there, under a pending key, with the right contents. That
        // is what makes the next assertion meaningful: the bytes exist and are still not served.
        var pendingKey = MediaValues.PendingStorageKey(InstallationId, parsedProjectId, mediaId);
        var staged = Path.Combine(mediaRoot, pendingKey.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(staged), $"the staged object must still be on disk at {pendingKey}");
        Assert.Equal(expectedHash, Sha256Hex(await File.ReadAllBytesAsync(staged)));

        // A pending row is now what an interrupted upload would have left behind had it got as far as
        // recording itself. It names bytes that exist and is still not servable.
        await InsertMediaRowAsync(
            database,
            mediaId,
            InstallationId,
            parsedProjectId,
            pendingKey,
            MediaStatus.Pending,
            expectedHash,

            // Staged two days before the frozen clock, so the sweep below is the design's real
            // condition - staged longer ago than the retention window - and not an arbitrary cutoff.
            createdAt: Clock.AddHours(-48));

        await using (var host = MediaApiHost.Start(database, Clock, mediaRoot))
        {
            using var owner = await OwnerClient.CreateAsync(host);

            using var read = await owner.GetAsync($"/api/media/{mediaId:D}");

            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

            var body = await read.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal(MediaError.NotFound, document.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("PNG", body, StringComparison.Ordinal);

            // The record was not promoted by the read, and the bytes are still untouched.
            var row = await ReadMediaRowAsync(database, mediaId);
            Assert.Equal(MediaStatus.Pending, row!.Status);
            Assert.Equal(1, row.Revision);
            Assert.Equal(expectedHash, Sha256Hex(await File.ReadAllBytesAsync(staged)));
        }

        // The reconciliation the design calls for removes the orphan, and is idempotent.
        await using (var host = MediaApiHost.Start(database, Clock, mediaRoot))
        {
            using var scope = host.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IMediaStore>();

            var released = await store.PurgeUnreferencedAsync(Clock.AddHours(-24), 100);

            Assert.Equal(1, released);
            Assert.False(File.Exists(staged));

            // A second pass has nothing left to do, so running it again is not an error.
            Assert.Equal(0, await store.PurgeUnreferencedAsync(Clock.AddHours(-24), 100));

            // The swept record is terminal, so nothing can read it or be swept again.
            var swept = await ReadMediaRowAsync(database, mediaId);
            Assert.Equal(MediaStatus.Deleted, swept!.Status);
            Assert.Equal(2, swept.Revision);
        }
    }

    // ===========================================================================================
    // Helpers.
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
        var root = Path.Combine(Path.GetTempPath(), $"chronicleaf-media-{Guid.NewGuid():N}", "media");
        Directory.CreateDirectory(root);
        _temporaryRoots.Add(root);

        return root;
    }

    /// <summary>A temporary MediaRoot with a sibling directory outside it, for the boundary scenario.</summary>
    private (string MediaRoot, string Outside) TemporaryRootPair()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"chronicleaf-media-{Guid.NewGuid():N}");
        var media = Path.Combine(parent, "media");
        var outside = Path.Combine(parent, "outside");

        Directory.CreateDirectory(media);
        Directory.CreateDirectory(outside);
        _temporaryRoots.Add(parent);

        return (media, outside);
    }

    private static async Task InsertMediaRowAsync(
        string database,
        Guid mediaId,
        Guid scopeId,
        Guid projectId,
        string storageKey,
        string status,
        string sha256,
        DateTimeOffset? createdAt = null) =>
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            $"""
             INSERT INTO media (id, scope_id, project_id, storage_key, media_type, byte_length, sha256, status, created_at, owner_operation_id, revision)
             VALUES ('{mediaId:D}', '{scopeId:D}', '{projectId:D}', '{storageKey}', '{MediaTypes.Png}', 16, '{sha256}', '{status}', '{(createdAt ?? Clock).UtcDateTime:O}', NULL, 1)
             """);

    private static async Task<StoredMediaRow?> ReadMediaRowAsync(string database, Guid mediaId)
    {
        var text = await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT media_type || '|' || byte_length || '|' || sha256 || '|' || status || '|' || storage_key || '|' || revision "
            + $"FROM media WHERE id = '{mediaId:D}'");

        if (text is null)
        {
            return null;
        }

        var parts = text.Split('|');

        return new StoredMediaRow(parts[0], long.Parse(parts[1]), parts[2], parts[3], parts[4], long.Parse(parts[5]));
    }

    private static Task<DateTimeOffset?> ReadCreatedAtAsync(string database, Guid mediaId) =>
        ReadInstantAsync(
            database,
            $"SELECT created_at::text FROM media WHERE id = '{mediaId:D}'");

    private static async Task<DateTimeOffset?> ReadInstantAsync(string database, string sql)
    {
        var text = await ChronicleafPostgresFixture.ReadTextAsync(database, sql);

        return text is null
            ? null
            : DateTimeOffset.Parse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal);
    }

    private static Task<long> CountMediaRowsAsync(string database) =>
        ChronicleafPostgresFixture.CountRowsAsync(database, "media");

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static byte[] EncodingOf(string body) => System.Text.Encoding.UTF8.GetBytes(body);
}

/// <summary>The media row as PostgreSQL stores it, read over a raw connection.</summary>
internal sealed record StoredMediaRow(
    string MediaType,
    long ByteLength,
    string Sha256,
    string Status,
    string StorageKey,
    long Revision);
