using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.AI;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// STATIC-BLOG-EXPORT-001 and STATIC-BLOG-EXPORT-002 against a real disposable PostgreSQL, the
/// real <c>Chronicleaf.Api</c> host, the real operation queue and the real project, milestone,
/// evidence, asset, approval, media and HTML export contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the projects, the confirmed milestones, the evidence
/// store, the asset store, the approvals table, the media store, the routes, the access boundary
/// and the database. The drafts a scenario exports are produced by real generation jobs through
/// the same scripted provider the drafts suite drives, and the picture one of them carries is
/// produced by a real capture job through the same recording provider the capture suite drives -
/// the two declared substitutions, and no stage here may be read as evidence that any provider
/// works or that a real page was photographed.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> An archive is unzipped and every entry is compared against
/// the manifest the artifact carries: the claims here are about what is <em>served</em> and what
/// the served bytes say about themselves, and a checksum that agrees with itself whether or not
/// the bytes on disk were right would prove nothing.
/// </para>
/// <para>
/// The two requirements' success, failure and boundary scenarios are covered one for one, the
/// approval bindings this package reuses rather than redefines are covered as carry-over, and
/// the builder's own refusal branches are covered without a database. Each test is named after
/// the scenario it covers.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class StaticBlogExportTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("d4e6b8c1-2a03-5e7d-b594-1f8e3c7a9d22");

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    private const string PublicUrl = "https://example.com/pricing";

    /// <summary>
    /// The evidence every export scenario drafts from. Free of digits on purpose, for the same
    /// reason the review suite's own note is: the numeral rule refuses a figure that appears in no
    /// evidence item, and a note full of numbers would let claims pass for the wrong reason.
    /// </summary>
    private const string EvidenceNote =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string SecondEvidenceNote =
        "The slice now ships from a tagged commit. The tag was cut after the checks passed.";

    private const string KnownLimitation = "Only Linux is covered.";

    private const string PictureCaption = "The pricing table after the redesign.";

    /// <summary>
    /// A milestone note that must never reach an archive. Milestone notes are the owner's private
    /// record, not evidence, and the exporter never reads them.
    /// </summary>
    private const string PrivateMilestoneNotes = "PRIVATE-NOTE-NEVER-EXPORTED-do-not-publish";

    // ==============================================================================================
    // STATIC-BLOG-EXPORT-001 Success: two approved posts render text and local images, and the
    // manifest matches the packed bytes. Private candidates and notes never enter the archive.
    // ==============================================================================================

    [Fact]
    public async Task Blog_001_Success_two_approved_posts_render_text_images_and_matching_manifest()
    {
        await using var fixture = await ArrangeAsync("blog_001_success");

        using var requested = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog",
            new
            {
                approvedAssetIds = new[] { fixture.Asset1Id.ToString("D"), fixture.Asset2Id.ToString("D") },
                basePath = "/journey",
                idempotencyKey = "blog-001-success",
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var operationId = JsonDocument
            .Parse(await requested.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetGuid();

        await fixture.Host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", TimeSpan.FromSeconds(5));
        Assert.Null(operation.ErrorCode);
        Assert.NotNull(operation.ResultRef);

        using var downloaded = await fixture.Owner.GetAsync(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog/{operationId:D}/download");

        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal("application/zip", downloaded.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", downloaded.Content.Headers.ContentDisposition?.DispositionType);

        var bytes = await downloaded.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        // Exactly the generated entries: nothing from owner text can become a path, so nothing
        // else can be here. Both sides sort ordinally, because the asset ids are server-generated
        // GUIDs with no alphabetical relation to the creation order: asserting the sorted actual
        // against an unsorted expectation passes only when the first id happens to sort first.
        var names = archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[]
            {
                ".nojekyll",
                $"assets/{fixture.SelectedMediaIds[0]:N}.png",
                "index.html",
                "manifest.json",
                $"posts/{fixture.Asset1Id:N}.html",
                $"posts/{fixture.Asset2Id:N}.html",
            }.Order(StringComparer.Ordinal).ToList(),
            names);

        var index = Encoding.UTF8.GetString(await ReadEntryAsync(archive, "index.html"));
        var postOne = Encoding.UTF8.GetString(await ReadEntryAsync(archive, $"posts/{fixture.Asset1Id:N}.html"));
        var postTwo = Encoding.UTF8.GetString(await ReadEntryAsync(archive, $"posts/{fixture.Asset2Id:N}.html"));
        var picture = await ReadEntryAsync(archive, $"assets/{fixture.SelectedMediaIds[0]:N}.png");
        var manifest = Encoding.UTF8.GetString(await ReadEntryAsync(archive, "manifest.json"));

        // The timeline reads oldest first, cites the evidence head each entry was approved
        // against, and links each post relatively.
        var firstLink = index.IndexOf($"posts/{fixture.Asset1Id:N}.html", StringComparison.Ordinal);
        var secondLink = index.IndexOf($"posts/{fixture.Asset2Id:N}.html", StringComparison.Ordinal);
        Assert.True(firstLink >= 0 && secondLink > firstLink);
        Assert.Contains("evidence revision 2", index, StringComparison.Ordinal);
        Assert.Contains("evidence revision 1", index, StringComparison.Ordinal);
        Assert.Contains("First reproducible release", index, StringComparison.Ordinal);
        Assert.Contains("Second shippable slice", index, StringComparison.Ordinal);

        // The approved text survived the shape change, and the allowlisted picture is the packaged
        // relative path with the real PNG bytes behind it.
        Assert.Contains("The pipeline now reproduces from a clean checkout.", postOne, StringComparison.Ordinal);
        Assert.Contains(
            $"<img src=\"../assets/{fixture.SelectedMediaIds[0]:N}.png\"",
            postOne,
            StringComparison.Ordinal);
        Assert.Contains(PictureCaption, postOne, StringComparison.Ordinal);
        Assert.Equal(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            picture[..8]);

        // The second post carries no picture and no gallery: a text-only approval stays text-only.
        Assert.Contains("The slice now ships from a tagged commit.", postTwo, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", postTwo, StringComparison.OrdinalIgnoreCase);

        // Nothing executable reached any page: the fixed template carries no scripts and the
        // sanitizer kept hostile markup as characters.
        foreach (var page in new[] { index, postOne, postTwo })
        {
            Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        }

        // The manifest describes exactly what was packed, with checksums over the packed bytes.
        using var manifestJson = JsonDocument.Parse(manifest);
        var root = manifestJson.RootElement;
        Assert.Equal(StaticBlogSiteBuilder.TemplateVersion, root.GetProperty("templateVersion").GetString());
        Assert.Equal("/journey", root.GetProperty("basePath").GetString());

        var posts = root.GetProperty("posts").EnumerateArray().ToList();
        Assert.Equal(2, posts.Count);
        Assert.Equal(fixture.Asset1Id.ToString("D"), posts[0].GetProperty("assetId").GetString());
        Assert.Equal(fixture.Asset2Id.ToString("D"), posts[1].GetProperty("assetId").GetString());
        Assert.Equal(2, posts[0].GetProperty("evidenceRevision").GetInt64());
        Assert.Equal(1, posts[1].GetProperty("evidenceRevision").GetInt64());

        var files = root.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(4, files.Count);

        foreach (var file in files)
        {
            var path = file.GetProperty("path").GetString()!;
            var packed = await ReadEntryAsync(archive, path);
            Assert.Equal(Hash(packed), file.GetProperty("sha256").GetString());
            Assert.Equal(packed.LongLength, file.GetProperty("bytes").GetInt64());
        }

        var contentSha = root.GetProperty("contentSha256").GetString()!;
        Assert.Equal(operation.ResultRef, $"static-blog-export:{contentSha}:2:1");

        // The private half of the project is absent everywhere: the unselected candidate's media
        // id names no entry and no manifest file, and the milestone's private notes name no text.
        var unselected = fixture.UnselectedMediaIds[0].ToString("N");
        Assert.DoesNotContain(unselected, manifest, StringComparison.Ordinal);
        Assert.DoesNotContain(unselected, index, StringComparison.Ordinal);
        Assert.DoesNotContain(unselected, postOne, StringComparison.Ordinal);
        foreach (var page in new[] { index, postOne, postTwo })
        {
            Assert.DoesNotContain(PrivateMilestoneNotes, page, StringComparison.Ordinal);
        }

        // The audit row records that an archive was committed, not a copy of it.
        var metadata = await ChronicleafPostgresFixture.ReadTextAsync(
            fixture.Database,
            "SELECT safe_metadata::text FROM audit_events WHERE action = 'static-blog-export.committed' "
            + "ORDER BY occurred_at DESC LIMIT 1");
        Assert.False(string.IsNullOrEmpty(metadata));
        Assert.DoesNotContain("The pipeline now reproduces", metadata!, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // STATIC-BLOG-EXPORT-001 Failure: a selection that includes an unapproved asset is refused
    // before any job exists.
    // ==============================================================================================

    [Fact]
    public async Task Blog_001_Failure_selection_with_unapproved_asset_is_refused_before_any_job()
    {
        await using var fixture = await ArrangeAsync("blog_001_unapproved", approveSecond: false);

        using var refused = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog",
            new
            {
                approvedAssetIds = new[] { fixture.Asset1Id.ToString("D"), fixture.Asset2Id.ToString("D") },
                basePath = "/journey",
                idempotencyKey = "blog-001-unapproved",
            }));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var envelope = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("approval_required", envelope.RootElement.GetProperty("code").GetString());

        // Refused before a job existed: no export operation and no request row.
        Assert.Equal(
            "0",
            await ChronicleafPostgresFixture.ReadTextAsync(
                fixture.Database,
                "SELECT count(*)::text FROM operations WHERE kind = 'project.static-blog-export'"));
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(fixture.Database, "static_blog_export_requests"));
    }

    // ==============================================================================================
    // STATIC-BLOG-EXPORT-001 Boundary: an asset of another project answers 404, never an export.
    // ==============================================================================================

    [Fact]
    public async Task Blog_001_Boundary_asset_from_another_project_is_not_exportable()
    {
        await using var fixture = await ArrangeAsync("blog_001_foreign");
        var foreignAsset = await ArrangeSecondProjectAsync(fixture, "blog_001_foreign_b");

        using var missing = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog",
            new
            {
                approvedAssetIds = new[] { fixture.Asset1Id.ToString("D"), foreignAsset.ToString("D") },
                basePath = "/journey",
                idempotencyKey = "blog-001-foreign",
            }));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(fixture.Database, "static_blog_export_requests"));
    }

    // ==============================================================================================
    // STATIC-BLOG-EXPORT-002 Success: a nonroot base path travels as deployment metadata while
    // every link stays relative and offline-resolvable.
    // ==============================================================================================

    [Fact]
    public async Task Blog_002_Success_nonroot_base_path_travels_as_metadata_with_offline_relative_links()
    {
        await using var fixture = await ArrangeAsync("blog_002_basepath");

        using var requested = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog",
            new
            {
                approvedAssetIds = new[] { fixture.Asset1Id.ToString("D"), fixture.Asset2Id.ToString("D") },
                basePath = "/journey/",
                idempotencyKey = "blog-002-basepath",
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var operationId = JsonDocument
            .Parse(await requested.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetGuid();

        await fixture.Host.DispatchAsync();
        await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", TimeSpan.FromSeconds(5));

        using var downloaded = await fixture.Owner.GetAsync(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog/{operationId:D}/download");

        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        var bytes = await downloaded.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var index = Encoding.UTF8.GetString(await ReadEntryAsync(archive, "index.html"));
        var postOne = Encoding.UTF8.GetString(await ReadEntryAsync(archive, $"posts/{fixture.Asset1Id:N}.html"));

        // The trailing slash is normalized away, and the base path is recorded as metadata -
        // never as a filesystem path and never inside a packed entry name.
        Assert.Contains(
            "<meta name=\"chronicleaf-base-path\" content=\"/journey\">",
            index,
            StringComparison.Ordinal);
        var manifest = Encoding.UTF8.GetString(await ReadEntryAsync(archive, "manifest.json"));
        Assert.Contains("\"basePath\":\"/journey\"", manifest, StringComparison.Ordinal);

        // Every navigation is relative: the index reaches its posts, a post reaches back to the
        // index and sideways to its pictures, and no link names a host, a scheme or an absolute
        // path that could only resolve on one deployment.
        Assert.Contains($"href=\"posts/{fixture.Asset1Id:N}.html\"", index, StringComparison.Ordinal);
        Assert.Contains(
            $"src=\"../assets/{fixture.SelectedMediaIds[0]:N}.png\"",
            postOne,
            StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/", index, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"/", index, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/", postOne, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"/", postOne, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // STATIC-BLOG-EXPORT-002 Failure: an approval revoked before the build supersedes the run with
    // no completed archive.
    // ==============================================================================================

    [Fact]
    public async Task Blog_002_Failure_revocation_before_the_build_supersedes_with_no_archive()
    {
        await using var fixture = await ArrangeAsync("blog_002_revoked");

        using var requested = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog",
            new
            {
                approvedAssetIds = new[] { fixture.Asset1Id.ToString("D"), fixture.Asset2Id.ToString("D") },
                basePath = "/journey",
                idempotencyKey = "blog-002-revoked",
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var operationId = JsonDocument
            .Parse(await requested.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetGuid();

        // The approval lapses while the job is queued: withdrawing it invalidates the frozen
        // binding the run must still see.
        using var withdrawn = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.Asset1Id:D}/unapprove",
            new { reason = "The numbers need a second look." }));
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);

        await fixture.Host.DispatchAsync();
        var operation = await AwaitErrorAsync(fixture, operationId, StaticBlogError.Superseded);

        Assert.Equal("failed", operation.Status);
        Assert.Null(operation.ResultRef);

        // No completed archive exists, so the download is refused as a lapsed authorization and
        // the status route reports the redacted code rather than a reason.
        using var download = await fixture.Owner.GetAsync(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog/{operationId:D}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
        var envelope = JsonDocument.Parse(await download.Content.ReadAsStringAsync());
        Assert.Equal("approval_stale", envelope.RootElement.GetProperty("code").GetString());

        using var status = await fixture.Owner.GetAsync(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog/{operationId:D}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var view = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("failed", view.GetProperty("status").GetString());
        Assert.Equal(StaticBlogError.Superseded, view.GetProperty("errorCode").GetString());
    }

    // ==============================================================================================
    // STATIC-BLOG-EXPORT-002 Boundary: the same immutable selection builds reproducible checksums.
    // ==============================================================================================

    [Fact]
    public async Task Blog_002_Boundary_same_selection_exported_twice_builds_reproducible_checksums()
    {
        await using var fixture = await ArrangeAsync("blog_002_repeat");

        var first = await RequestExportAsync(
            fixture,
            new[] { fixture.Asset1Id, fixture.Asset2Id },
            "/journey",
            "blog-002-repeat-a");
        var second = await RequestExportAsync(
            fixture,
            new[] { fixture.Asset2Id, fixture.Asset1Id },
            "/journey",
            "blog-002-repeat-b");

        Assert.NotEqual(first, second);

        await fixture.Host.DispatchAsync();
        await OperationRows.AwaitAsync(fixture.Database, first, "succeeded", TimeSpan.FromSeconds(5));
        await OperationRows.AwaitAsync(fixture.Database, second, "succeeded", TimeSpan.FromSeconds(5));

        var firstBytes = await DownloadAsync(fixture, first);
        var secondBytes = await DownloadAsync(fixture, second);

        // Selection order on the wire changes nothing: the canonical order builds the same bytes
        // and the same manifest digest twice.
        Assert.Equal(firstBytes, secondBytes);

        var firstSha = await StatusShaAsync(fixture, first);
        var secondSha = await StatusShaAsync(fixture, second);
        Assert.Equal(firstSha, secondSha);
        Assert.Equal(64, firstSha.Length);
    }

    // ==============================================================================================
    // Binding carry-over: the export reuses the review package's approval bindings rather than
    // redefining them.
    // ==============================================================================================

    [Fact]
    public async Task Blog_Binding_moved_evidence_refuses_the_request_as_stale()
    {
        await using var fixture = await ArrangeAsync("blog_bind_stale");

        // Evidence appended after the approval moves the head the approval was bound to.
        using var appended = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.Milestone1Id:D}/evidence/manual",
            new
            {
                notes = "A collection landed after the approval.",
                expectedRevision = fixture.BundleRevision1,
            },
            fixture.BundleRevision1));

        Assert.Equal(HttpStatusCode.Created, appended.StatusCode);

        using var stale = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog",
            new
            {
                approvedAssetIds = new[] { fixture.Asset1Id.ToString("D"), fixture.Asset2Id.ToString("D") },
                basePath = "/journey",
                idempotencyKey = "blog-bind-stale",
            }));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var envelope = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("approval_stale", envelope.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Blog_Binding_missing_picture_fails_the_build_as_media_unavailable()
    {
        await using var fixture = await ArrangeAsync("blog_bind_media");

        var operationId = await RequestExportAsync(
            fixture,
            new[] { fixture.Asset1Id, fixture.Asset2Id },
            "/journey",
            "blog-bind-media");

        // The bytes are lost behind the approval's back: the row stays as the record that they
        // were, but its status is no longer servable - which is exactly what the media store's own
        // contract says makes a record unreadable. The approval still binds the picture, so the
        // build fails rather than serving the site picture-less.
        await using (var connection = new NpgsqlConnection(fixture.Database))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE media SET status = 'deleted' WHERE id = '{fixture.SelectedMediaIds[0]:D}'";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await fixture.Host.DispatchAsync();
        var operation = await AwaitErrorAsync(fixture, operationId, HtmlExportError.MediaUnavailable);

        Assert.Equal("failed", operation.Status);
        Assert.Null(operation.ResultRef);
    }

    // ==============================================================================================
    // Request validation: traversal base paths, duplicate selections and idempotent replays.
    // ==============================================================================================

    [Fact]
    public async Task Blog_Request_traversal_base_path_is_refused()
    {
        await using var fixture = await ArrangeAsync("blog_req_basepath");

        foreach (var basePath in new[] { "../../etc", "/journey/../etc", "https://evil.invalid/x", "/journey//x" })
        {
            using var refused = await fixture.Owner.SendAsync(await fixture.Owner.Post(
                $"/api/projects/{fixture.ProjectId:D}/exports/static-blog",
                new
                {
                    approvedAssetIds = new[] { fixture.Asset1Id.ToString("D") },
                    basePath,
                    idempotencyKey = $"blog-req-basepath-{basePath.Length}",
                }));

            Assert.True(
                refused.StatusCode == HttpStatusCode.UnprocessableContent,
                $"{basePath} answered {(int)refused.StatusCode}");
            var envelope = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("validation_failed", envelope.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Blog_Request_same_key_replays_the_original_job()
    {
        await using var fixture = await ArrangeAsync("blog_req_idem");

        var first = await RequestExportRawAsync(
            fixture,
            new[] { fixture.Asset1Id, fixture.Asset2Id },
            "/journey",
            "blog-req-idem");
        var second = await RequestExportRawAsync(
            fixture,
            new[] { fixture.Asset1Id, fixture.Asset2Id },
            "/journey",
            "blog-req-idem");

        Assert.Equal(first.OperationId, second.OperationId);
        Assert.True(first.Created);
        Assert.False(second.Created);
    }

    // ==============================================================================================
    // Migration and schema: the export request table, its bounds and its vocabulary.
    // ==============================================================================================

    [Fact]
    public async Task The_export_request_table_index_and_vocabulary_come_from_the_forward_migration_and_readiness_requires_it()
    {
        var database = await postgres.CreateDatabaseAsync("blog_migration");

        Assert.Contains(ChronicleafSchema.StaticBlogExportMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));

        var missing = ChronicleafSchema.MissingMigrationIds(
            ChronicleafSchema.RequiredMigrationIds
                .Where(id => id != ChronicleafSchema.StaticBlogExportMigrationId)
                .ToArray());
        Assert.Equal([ChronicleafSchema.StaticBlogExportMigrationId], missing);

        Assert.Equal(
            "static_blog_export_requests",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT tablename FROM pg_tables WHERE tablename = 'static_blog_export_requests'"));

        Assert.Equal(
            "ux_static_blog_export_requests_scope_id_operation_id",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT indexname FROM pg_indexes WHERE tablename = 'static_blog_export_requests' " +
                "AND indexname = 'ux_static_blog_export_requests_scope_id_operation_id'"));

        Assert.Equal(
            "IX_static_blog_export_requests_project_id",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT indexname FROM pg_indexes WHERE tablename = 'static_blog_export_requests' " +
                "AND indexname = 'IX_static_blog_export_requests_project_id'"));

        Assert.Equal(
            "ck_static_blog_export_requests_selection_hash_sha256_hex",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT conname FROM pg_constraint WHERE conname = " +
                "'ck_static_blog_export_requests_selection_hash_sha256_hex'"));

        // The bound is the database's, not a comment's: a second row for one job is refused.
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM static_blog_export_requests";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    // ==============================================================================================
    // Builder refusals without a database: the site bound, cancellation and generated paths.
    // ==============================================================================================

    [Fact]
    public async Task Blog_Builder_cancelled_build_returns_no_partial_archive()
    {
        var posts = new[]
        {
            new SitePostInput(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Reproducible release pipeline",
                Clock,
                1,
                ContentRevision.Hash("Words.\n"),
                1,
                "Words.\n",
                []),
        };

        // The token is already cancelled before the first byte is read: the builder throws
        // instead of returning anything, so there is no completed partial archive to mistake for
        // a result.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => StaticBlogSiteBuilder.BuildAsync(
                posts,
                "/journey",
                "A site.",
                new HtmlExportAdapter(),
                new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Blog_Builder_oversized_site_is_refused_before_packing()
    {
        var posts = new[]
        {
            new SitePostInput(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Reproducible release pipeline",
                Clock,
                1,
                ContentRevision.Hash("Words.\n"),
                1,
                "Words.\n",
                [new SitePostImage(
                    Guid.NewGuid(),
                    "A caption.",
                    "image/png",
                    new byte[StaticBlogSiteBuilder.MaxSiteBytes + 1])]),
        };

        var thrown = await Assert.ThrowsAsync<PayloadTooLargeException>(
            () => StaticBlogSiteBuilder.BuildAsync(
                posts, "/journey", "A site.", new HtmlExportAdapter(), CancellationToken.None));

        Assert.Equal(ApiError.PayloadTooLarge, thrown.Code);
    }

    [Fact]
    public async Task Blog_Builder_hostile_owner_text_never_reaches_a_path()
    {
        var assetId = Guid.NewGuid();
        var posts = new[]
        {
            new SitePostInput(
                assetId,
                Guid.NewGuid(),
                "../../evil <script>alert('title')</script>",
                Clock,
                1,
                ContentRevision.Hash("Words.\n"),
                1,
                "Words.\n",
                []),
        };

        var site = await StaticBlogSiteBuilder.BuildAsync(
            posts, "/journey", "../../evil site", new HtmlExportAdapter(), CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(site.ZipBytes), ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToList();
        Assert.Equal(
            [".nojekyll", "index.html", "manifest.json", $"posts/{assetId:N}.html"],
            [.. names.Order(StringComparer.Ordinal)]);
        Assert.All(names, name => Assert.DoesNotContain("..", name, StringComparison.Ordinal));

        var index = Encoding.UTF8.GetString(await ReadEntryAsync(archive, "index.html"));
        Assert.DoesNotContain("<script", index, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("../../evil site", index, StringComparison.Ordinal);

        var manifest = JsonDocument.Parse(site.ManifestJson);
        Assert.Equal("/journey", manifest.RootElement.GetProperty("basePath").GetString());
    }

    [Fact]
    public async Task Blog_Builder_base_path_normalizes_and_pins_a_selection_hash()
    {
        Assert.Equal("/", StaticBlogSiteBuilder.NormalizeBasePath(null));
        Assert.Equal("/", StaticBlogSiteBuilder.NormalizeBasePath("  "));
        Assert.Equal("/journey", StaticBlogSiteBuilder.NormalizeBasePath("/journey/"));

        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");

        // Order on the wire changes nothing: the hash is over the canonical order.
        Assert.Equal(
            StaticBlogSiteBuilder.SelectionHashFor([first, second]),
            StaticBlogSiteBuilder.SelectionHashFor([second, first]));
        Assert.Equal(64, StaticBlogSiteBuilder.SelectionHashFor([first]).Length);
    }

    // ==============================================================================================
    // The fixture.
    // ==============================================================================================

    /// <summary>
    /// Builds everything a scenario needs: a real database, a real project, two confirmed
    /// milestones with real recorded evidence, a real sealed credential, two stored drafts
    /// produced by real generation jobs, and the assets those drafts belong to - the first with
    /// a selected picture beside an unselected candidate, the second text-only. Both assets are
    /// approved unless a scenario says otherwise.
    /// </summary>
    private async Task<Fixture> ArrangeAsync(
        string purpose,
        bool approveFirst = true,
        bool approveSecond = true)
    {
        var database = await ProvisionedDatabaseAsync(purpose);
        var keyFile = OperatorKeyFile.Create();
        var clock = new FixedTimeProvider(Clock);

        Guid projectId;
        Guid milestone1Id;
        Guid milestone2Id;
        Guid credentialId;
        HeadView head1;
        HeadView head2;

        await using (var setup = GroundedMarkdownDraftsApiHost.StartProduction(database, clock, keyFile.FilePath))
        {
            using var setupOwner = await OwnerClient.CreateAsync(setup);

            projectId = await CreateProjectAsync(setupOwner, purpose);
            milestone1Id = await CreateMilestoneAsync(
                setupOwner, projectId, "First reproducible release", "2026-10-01T09:00:00Z", PrivateMilestoneNotes);
            milestone2Id = await CreateMilestoneAsync(
                setupOwner, projectId, "Second shippable slice", "2026-10-03T09:00:00Z", "A private working note.");

            foreach (var (milestoneId, notes) in new[]
                         {
                             (milestone1Id, EvidenceNote),
                             (milestone2Id, SecondEvidenceNote),
                         })
            {
                using var evidence = await setupOwner.SendAsync(await setupOwner.Post(
                    $"/api/milestones/{milestoneId:D}/evidence/manual",
                    new { notes, expectedRevision = 0 },
                    0));

                Assert.Equal(HttpStatusCode.Created, evidence.StatusCode);
            }

            // The generated bodies are English, so the voice they are judged under must be too:
            // the default profile declares Chinese and would report language findings on every
            // field, which is the blocked scenario's own condition rather than this fixture's.
            using var voiced = await setupOwner.SendAsync(await setupOwner.Put(
                $"/api/projects/{projectId:D}/voice-profile",
                new { presetId = "technical-blog", overrides = new { language = "en" } },
                0));

            Assert.Equal(HttpStatusCode.OK, voiced.StatusCode);

            credentialId = await SaveCredentialAsync(setupOwner);
        }

        // One worker at a time shares this database. Each host runs its own job server over
        // the same job storage, so a capture host that is still alive when a draft is
        // dispatched could dequeue the draft job - and its container holds no generation
        // double, so the draft would fail against the real adapter. The capture host is
        // therefore disposed before the drafts host starts; the candidates, the selection and
        // the media bytes it wrote persist in the database and on disk.
        List<Guid> selectedMedia = [];
        List<Guid> unselectedMedia = [];

        await using (var capture = CaptureApiHost.Start(
                         database, clock, new RecordingScreenshotProvider(clock)))
        {
            using var pictureOwner = await OwnerClient.CreateAsync(capture);

            var candidates = await CaptureTwoAsync(capture, pictureOwner, milestone1Id);
            var first = candidates[0];

            using var selected = await pictureOwner.SendAsync(await pictureOwner.Patch(
                $"/api/screenshots/{first.Id:D}",
                first.Revision,
                new
                {
                    status = "selected",
                    caption = PictureCaption,
                    expectedRevision = first.Revision,
                    expectedBundleRevision = 1,
                }));

            Assert.Equal(HttpStatusCode.OK, selected.StatusCode);

            // The select appended the second bundle revision: the carried manual fact has a new
            // id there, so the summary cites the head's ids rather than the first revision's.
            selectedMedia.Add(first.MediaId);
            unselectedMedia.Add(candidates[1].MediaId);
        }

        // The drafts host shares the database - and therefore the milestones, the evidence and
        // the stored media - with the capture host above. Only the seams differ: this host
        // answers generation, that one answers captures.
        var host = GroundedMarkdownDraftsApiHost.Start(
            database,
            clock,
            keyFile.FilePath,
            ScriptedDraftProvider.With("{}", "{}"));
        var owner = await OwnerClient.CreateAsync(host);

        head1 = await ReadHeadAsync(owner, milestone1Id);
        Assert.Equal(2, head1.Revision);
        head2 = await ReadHeadAsync(owner, milestone2Id);
        Assert.Equal(1, head2.Revision);

        // The script is replaced with the summaries citing the heads just read: a summary must
        // cite the frozen bundle's own item ids, so no honest script can exist before this read.
        host.Provider!.Rescript(
            SummaryJson(head1.ManualItemId, "Reproducible release pipeline", FirstSummaryText),
            BodyFor(head1.ManualItemId, "Reproducible release pipeline", FirstSummaryText));

        var asset1Id = await DraftAsync(
            host, owner, database, milestone1Id, credentialId, head1, $"blog-{purpose}-m1");

        host.Provider!.Rescript(
            SummaryJson(head2.ManualItemId, "Second shippable slice", SecondSummaryText),
            BodyFor(head2.ManualItemId, "Second shippable slice", SecondSummaryText));

        var asset2Id = await DraftAsync(
            host, owner, database, milestone2Id, credentialId, head2, $"blog-{purpose}-m2");

        if (approveFirst)
        {
            await ApproveAsync(owner, asset1Id, 1, head1.Revision);
        }

        if (approveSecond)
        {
            await ApproveAsync(owner, asset2Id, 1, head2.Revision);
        }

        return new Fixture(
            database,
            host,
            keyFile,
            owner,
            projectId,
            milestone1Id,
            milestone2Id,
            asset1Id,
            asset2Id,
            credentialId,
            head1.BundleId,
            head1.Revision,
            head2.BundleId,
            head2.Revision,
            selectedMedia,
            unselectedMedia);
    }

    /// <summary>
    /// A second project with one approved story, for the foreign-asset boundary: it shares the
    /// fixture's database, credential and provider double, and nothing else.
    /// </summary>
    private static async Task<Guid> ArrangeSecondProjectAsync(Fixture fixture, string purpose)
    {
        var owner = fixture.Owner;
        var projectId = await CreateProjectAsync(owner, purpose);
        var milestoneId = await CreateMilestoneAsync(
            owner, projectId, "Foreign milestone", "2026-10-04T09:00:00Z", "A foreign working note.");

        using var voiced = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = "technical-blog", overrides = new { language = "en" } },
            0));

        Assert.Equal(HttpStatusCode.OK, voiced.StatusCode);

        using var evidence = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = EvidenceNote, expectedRevision = 0 },
            0));

        Assert.Equal(HttpStatusCode.Created, evidence.StatusCode);

        var head = await ReadHeadAsync(owner, milestoneId);

        fixture.Host.Provider!.Rescript(
            SummaryJson(head.ManualItemId, "Reproducible release pipeline", FirstSummaryText),
            BodyFor(head.ManualItemId, "Reproducible release pipeline", FirstSummaryText));

        var assetId = await DraftAsync(
            fixture.Host, owner, fixture.Database, milestoneId, fixture.CredentialId, head,
            $"blog-{purpose}");

        await ApproveAsync(owner, assetId, 1, head.Revision);

        return assetId;
    }

    private static async Task<Guid> DraftAsync(
        GroundedMarkdownDraftsApiHost host,
        OwnerClient owner,
        string database,
        Guid milestoneId,
        Guid credentialId,
        HeadView head,
        string idempotencyKey)
    {
        var preview = await CreatePreviewAsync(
            host, owner, milestoneId, credentialId, head.Revision, head.ManualItemId);

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
                idempotencyKey,
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var accepted = JsonDocument.Parse(await requested.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();

        await host.DispatchAsync();

        try
        {
            await OperationRows.AwaitAsync(database, operationId, "succeeded", TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException timeout)
        {
            var last = await OperationRows.ReadAsync(database, operationId);
            throw new TimeoutException(
                $"{timeout.Message} Recorded error code: {last?.ErrorCode ?? "none"}.",
                timeout);
        }

        var asset = await ContentRows.ReadAssetForMilestoneAsync(database, milestoneId);
        Assert.NotNull(asset);

        return asset!.Id;
    }

    private static async Task ApproveAsync(OwnerClient owner, Guid assetId, long contentRevision, long evidenceRevision)
    {
        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/assets/{assetId:D}/approve",
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

    private static async Task<Guid> RequestExportAsync(
        Fixture fixture,
        IReadOnlyList<Guid> assetIds,
        string basePath,
        string idempotencyKey)
    {
        var accepted = await RequestExportRawAsync(fixture, assetIds, basePath, idempotencyKey);
        return accepted.OperationId;
    }

    private static async Task<(Guid OperationId, bool Created)> RequestExportRawAsync(
        Fixture fixture,
        IReadOnlyList<Guid> assetIds,
        string basePath,
        string idempotencyKey)
    {
        using var requested = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog",
            new
            {
                approvedAssetIds = assetIds.Select(id => id.ToString("D")).ToArray(),
                basePath,
                idempotencyKey,
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var body = JsonDocument.Parse(await requested.Content.ReadAsStringAsync()).RootElement;

        return (body.GetProperty("operationId").GetGuid(), body.GetProperty("created").GetBoolean());
    }

    private static async Task<byte[]> DownloadAsync(Fixture fixture, Guid operationId)
    {
        using var downloaded = await fixture.Owner.GetAsync(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog/{operationId:D}/download");

        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);

        return await downloaded.Content.ReadAsByteArrayAsync();
    }

    private static async Task<string> StatusShaAsync(Fixture fixture, Guid operationId)
    {
        using var status = await fixture.Owner.GetAsync(
            $"/api/projects/{fixture.ProjectId:D}/exports/static-blog/{operationId:D}");

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var view = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("succeeded", view.GetProperty("status").GetString());

        return view.GetProperty("contentSha256").GetString()!;
    }

    private static async Task<StoredOperation> AwaitErrorAsync(
        Fixture fixture,
        Guid operationId,
        string errorCode)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        StoredOperation? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await OperationRows.ReadAsync(fixture.Database, operationId);

            if (last is not null && string.Equals(last.ErrorCode, errorCode, StringComparison.Ordinal))
            {
                return last;
            }

            if (last is not null && last.Status is "failed" or "succeeded" or "cancelled")
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.NotNull(last);
        Assert.Equal(errorCode, last!.ErrorCode);

        return last;
    }

    private async Task<string> ProvisionedDatabaseAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);

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

    private static async Task<Guid> CreateProjectAsync(OwnerClient owner, string purpose)
    {
        using var response = await owner.SendAsync(await owner.Post(
            "/api/projects",
            new { name = $"{purpose} project", description = $"{purpose} record." }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<Guid> CreateMilestoneAsync(
        OwnerClient owner,
        Guid projectId,
        string title,
        string occurredAt,
        string notes)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/milestones",
            new
            {
                title,
                version = "0.1.0",
                occurredAt,
                type = "feature",
                notes,
                knownLimitations = Array.Empty<string>(),
                nextSteps = Array.Empty<string>(),
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
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

    private static async Task<HeadView> ReadHeadAsync(OwnerClient owner, Guid milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var bundles = document.RootElement.GetProperty("items").EnumerateArray().ToList();
        var head = bundles.MaxBy(bundle => bundle.GetProperty("revision").GetInt64())!;

        var manual = head.GetProperty("items").EnumerateArray()
            .First(item => item.GetProperty("kind").GetString() == "manual");

        return new HeadView(
            head.GetProperty("id").GetGuid(),
            head.GetProperty("revision").GetInt64(),
            Guid.Parse(manual.GetProperty("id").GetString()!));
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

    private static async Task<IReadOnlyList<CandidateView>> CaptureTwoAsync(
        CaptureApiHost host,
        OwnerClient owner,
        Guid milestoneId)
    {
        host.Provider.AnswerWithPng(width: 1440, height: 900);

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/captures",
            new
            {
                url = PublicUrl,
                viewports = new[] { "desktop", "mobile" },
                waitMs = 0,
                fullPage = false,
                expectedBundleRevision = (long?)null,
                idempotencyKey = $"blog-capture-{Guid.NewGuid():N}",
            },
            0));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var operationId = Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetString()!);

        await host.DispatchAsync();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await ChronicleafPostgresFixture.ReadTextAsync(
                host.Database,
                $"SELECT status FROM operations WHERE id = '{operationId}'");

            if (status is "succeeded" or "failed" or "cancelled" or "superseded")
            {
                break;
            }

            await Task.Delay(10);
        }

        using var operation = await owner.GetAsync($"/api/operations/{operationId:D}");
        var operationBody = JsonDocument.Parse(await operation.Content.ReadAsStringAsync());
        Assert.Equal(
            "succeeded",
            operationBody.RootElement.GetProperty("status").GetString());

        using var candidates = await owner.GetAsync($"/api/milestones/{milestoneId:D}/screenshots");
        Assert.Equal(HttpStatusCode.OK, candidates.StatusCode);

        using var document = JsonDocument.Parse(await candidates.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items").EnumerateArray().Select(item => new CandidateView(
            Guid.Parse(item.GetProperty("id").GetString()!),
            Guid.Parse(item.GetProperty("mediaId").GetString()!),
            item.GetProperty("revision").GetInt64())).ToList();

        Assert.Equal(2, items.Count);
        return items;
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);

        await using var stream = entry!.Open();
        using var buffered = new MemoryStream();
        await stream.CopyToAsync(buffered);

        return buffered.ToArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record HeadView(Guid BundleId, long Revision, Guid ManualItemId);

    private sealed record PreviewBody(Guid PreviewId, string ConsentHash, long EvidenceRevision, long VoiceRevision);

    private sealed record CandidateView(Guid Id, Guid MediaId, long Revision);

    /// <summary>
    /// Everything one scenario holds, and the disposal it owes the shared servers.
    /// </summary>
    private sealed record Fixture(
        string Database,
        GroundedMarkdownDraftsApiHost Host,
        OperatorKeyFile KeyFile,
        OwnerClient Owner,
        Guid ProjectId,
        Guid Milestone1Id,
        Guid Milestone2Id,
        Guid Asset1Id,
        Guid Asset2Id,
        Guid CredentialId,
        Guid EvidenceBundle1Id,
        long BundleRevision1,
        Guid EvidenceBundle2Id,
        long BundleRevision2,
        IReadOnlyList<Guid> SelectedMediaIds,
        IReadOnlyList<Guid> UnselectedMediaIds) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Host.DisposeAsync();
            KeyFile.Dispose();
        }
    }

    /// <summary>
    /// A valid structured summary citing the real evidence id. Every claim's text is free of
    /// digits and of every default forbidden term, because the numeral rule and the voice rules
    /// refuse what they cannot trace - and a fixture that tripped them would be testing the
    /// wrong thing on the success path.
    /// </summary>
    private const string FirstSummaryText =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string SecondSummaryText =
        "The slice now ships from a tagged commit. The tag was cut after the checks passed.";

    private static string SummaryJson(Guid evidenceId, string title, string summaryText)
    {
        var id = evidenceId.ToString("d");

        return $$"""
          {
            "milestoneTitle": {{Quote(title)}},
            "summary": {{Quote(summaryText)}},
            "changes": [{"text": "The pipeline runs from a clean checkout.", "evidenceIds": ["{{id}}"]}],
            "problemsSolved": [{"text": "A release could not be rebuilt from its source.", "evidenceIds": ["{{id}}"]}],
            "knownLimitations": [{"text": {{Quote(KnownLimitation)}}, "evidenceIds": ["{{id}}"]}],
            "nextSteps": [{"text": "Cover the other platform.", "evidenceIds": ["{{id}}"]}]
          }
          """;
    }

    /// <summary>
    /// A body that satisfies every local citation rule, built from the claim map the summary
    /// produces rather than hand-written, so the footnote ids are the ones the package assigned.
    /// </summary>
    private static string BodyFor(Guid evidenceId, string title, string summaryText)
    {
        var summaryJson = SummaryJson(evidenceId, title, summaryText);
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
