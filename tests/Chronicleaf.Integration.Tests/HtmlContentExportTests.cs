using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Access;
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
/// HTML-CONTENT-EXPORT-001 and HTML-CONTENT-EXPORT-002 against a real disposable PostgreSQL, the
/// real <c>Chronicleaf.Api</c> host, the real operation queue and the real evidence, voice, asset,
/// media and approval contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the voice service,
/// the asset store, the approvals table, the media store, the routes, the access boundary and the
/// database. The draft a scenario exports is produced by a real generation job through the same
/// scripted provider the drafts suite drives, and the pictures it carries are produced by a real
/// capture job through the same recording provider the capture suite drives - the two declared
/// substitutions, and no stage here may be read as evidence that any provider works or that a real
/// page was photographed.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> An export is unzipped and every entry is compared against
/// the manifest the artifact carries: the claims here are about what is <em>served</em> and what
/// the served bytes say about themselves, and a checksum that agrees with itself whether or not
/// the bytes on disk were right would prove nothing.
/// </para>
/// <para>
/// The two requirements' success, failure and boundary scenarios are covered one for one, the
/// approval bindings this package reuses rather than redefines are covered as carry-over, and the
/// renderer's own refusal branches are covered without a database. Each test is named after the
/// scenario it covers.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class HtmlContentExportTests(ChronicleafPostgresFixture postgres)
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

    private const string KnownLimitation = "Only Linux is covered.";

    private const string PictureCaption = "The pricing table after the redesign.";

    // ==============================================================================================
    // HTML-CONTENT-EXPORT-001 Success: an approved asset exports with its text and its allowlisted
    // pictures, checksummed.
    // ==============================================================================================

    [Fact]
    public async Task Html_001_Success_approved_markdown_renders_text_and_allowlisted_images()
    {
        await using var fixture = await ArrangeAsync("html_001_success", withPicture: true);
        await ApproveAsync(fixture, 1, fixture.BundleRevision);

        using var exported = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=html");

        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("application/zip", exported.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", exported.Content.Headers.ContentDisposition?.DispositionType);
        Assert.EndsWith(".zip", FileNameOf(exported), StringComparison.Ordinal);

        var bytes = await exported.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        // Exactly the generated entries: nothing from owner text can become a path, so nothing
        // else can be here.
        Assert.Equal(
            ["images/image-0.png", "index.html", "manifest.json"],
            [.. archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal)]);

        var document = await ReadEntryAsync(archive, "index.html");
        var picture = await ReadEntryAsync(archive, "images/image-0.png");
        var manifest = await ReadEntryAsync(archive, "manifest.json");

        var text = Encoding.UTF8.GetString(document);

        // The approved text survived the shape change: headings, prose and the citation
        // are all in the document. (Link handling is covered where the body carries links.)
        Assert.Contains("The pipeline now reproduces from a clean checkout.", text, StringComparison.Ordinal);
        Assert.Contains("<h1>Reproducible release pipeline</h1>", text, StringComparison.Ordinal);
        Assert.Contains("<sup>", text, StringComparison.Ordinal);

        // The allowlisted picture is the packaged relative path with the owner's caption, and the
        // bytes are a real PNG - the ones the capture job stored, not a placeholder.
        Assert.Contains("<img src=\"images/image-0.png\"", text, StringComparison.Ordinal);
        Assert.Contains(PictureCaption, text, StringComparison.Ordinal);
        Assert.Equal(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            picture[..8]);

        // The manifest describes exactly what was packed, with checksums over the packed bytes.
        using var manifestJson = JsonDocument.Parse(Encoding.UTF8.GetString(manifest));
        var root = manifestJson.RootElement;
        Assert.Equal(HtmlExportAdapter.TemplateVersion, root.GetProperty("templateVersion").GetString());
        Assert.Equal(fixture.AssetId.ToString("D"), root.GetProperty("assetId").GetString());
        Assert.Equal(1, root.GetProperty("contentRevision").GetInt64());
        Assert.Equal(fixture.BodyHash, root.GetProperty("bodyHash").GetString());

        var files = root.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);

        var documentEntry = files.Single(file => file.GetProperty("path").GetString() == "index.html");
        Assert.Equal(Hash(document), documentEntry.GetProperty("sha256").GetString());
        Assert.Equal(document.LongLength, documentEntry.GetProperty("bytes").GetInt64());

        var pictureEntry = files.Single(file => file.GetProperty("path").GetString() == "images/image-0.png");
        Assert.Equal(Hash(picture), pictureEntry.GetProperty("sha256").GetString());
        Assert.Equal(picture.LongLength, pictureEntry.GetProperty("bytes").GetInt64());
    }

    // ==============================================================================================
    // HTML-CONTENT-EXPORT-001 Failure: script and remote executable markup produce no executable
    // user code.
    // ==============================================================================================

    [Fact]
    public async Task Html_001_Failure_script_and_remote_markup_produce_no_executable_code()
    {
        await using var fixture = await ArrangeAsync("html_001_xss");
        await EditBodyAsync(
            fixture,
            $"{fixture.BodyText}\n\n<script>alert('review')</script>\n\n"
            + "<iframe src=\"https://evil.invalid/frame\"></iframe>\n\n"
            + "<style>body{display:none}</style>\n\n"
            + "<img src=\"https://evil.invalid/x.png\" onerror=\"alert('review')\">\n\n"
            + "[an unsafe link](javascript:alert('review'))\n\n"
            + "![a remote picture](https://evil.invalid/x.png)\n\n"
            + "**unclosed bold stays text\n\n"
            + "[a safe link](https://example.invalid/notes)\n");
        await ApproveAsync(fixture, 2, fixture.BundleRevision);

        using var exported = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=html");

        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("text/html", exported.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".html", FileNameOf(exported), StringComparison.Ordinal);

        // No pictures were selected, so this is the single-file shape: one document, no archive.
        var text = await exported.Content.ReadAsStringAsync();

        // Nothing the owner wrote became an element or a reachable destination: no script, no
        // frame, no image, no unsafe protocol in any attribute, and exactly one style element -
        // the template's own. An event handler would need a literal quote to become an
        // attribute; the owner's quotes are all escaped entities, so they stay characters.
        Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" onerror=\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"javascript:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOccurrences(text, "<style>"));

        // The hostile runs are still visible as the characters their author wrote - the export
        // preserves text, it does not silently drop what it refused to run.
        Assert.Contains("&lt;script&gt;alert", text, StringComparison.Ordinal);
        Assert.Contains("&lt;iframe", text, StringComparison.Ordinal);
        Assert.Contains("&lt;style&gt;", text, StringComparison.Ordinal);
        Assert.Contains("javascript:alert", text, StringComparison.Ordinal);
        Assert.Contains("![a remote picture](https://evil.invalid/x.png)", text, StringComparison.Ordinal);
        Assert.Contains("**unclosed bold stays text", text, StringComparison.Ordinal);

        // And the safe subset survived: the safe link navigates with opener protection, and the
        // approved prose is intact.
        Assert.Contains(
            "<a href=\"https://example.invalid/notes\" rel=\"noopener noreferrer\">",
            text,
            StringComparison.Ordinal);
        Assert.Contains("The pipeline now reproduces from a clean checkout.", text, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // HTML-CONTENT-EXPORT-001 Boundary: a missing approved picture fails media_unavailable rather
    // than serving a silently picture-less artifact.
    // ==============================================================================================

    [Fact]
    public async Task Html_001_Boundary_missing_approved_image_fails_media_unavailable()
    {
        await using var fixture = await ArrangeAsync("html_001_gone", withPicture: true);
        await ApproveAsync(fixture, 1, fixture.BundleRevision);

        // The bytes are lost behind the approval's back: the row stays as the record that
        // they were, but its status is no longer servable - which is exactly what the media
        // store's own contract says makes a record unreadable. The approval still binds the
        // picture, so the export is refused rather than served picture-less.
        await using (var connection = new NpgsqlConnection(fixture.Database))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE media SET status = 'deleted' WHERE id = '{fixture.SelectedMediaIds[0]:D}'";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        using var refused = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=html");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var envelope = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("media_unavailable", envelope.RootElement.GetProperty("code").GetString());
    }

    // ==============================================================================================
    // HTML-CONTENT-EXPORT-002 Success: the single-file artifact carries its own receipt.
    // ==============================================================================================

    [Fact]
    public async Task Html_002_Success_single_file_carries_content_hash_and_template_receipt()
    {
        await using var fixture = await ArrangeAsync("html_002_receipt");
        await ApproveAsync(fixture, 1, fixture.BundleRevision);

        // The named-revision path authorizes the same approval: an export that names the approved
        // revision is the same decision answered again, not a second decision.
        using var exported = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=html&revision=1");

        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("text/html", exported.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", exported.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(
            $"chronicleaf-{fixture.AssetId:N}-r1.html",
            FileNameOf(exported));

        var bytes = await exported.Content.ReadAsByteArrayAsync();
        var text = Encoding.UTF8.GetString(bytes);

        Assert.StartsWith("<!DOCTYPE html>", text, StringComparison.Ordinal);

        // The receipt: the exact body hash the approval bound and the template that rendered it,
        // readable without rendering anything.
        Assert.Contains(
            $"<meta name=\"chronicleaf-body-hash\" content=\"{fixture.BodyHash}\">",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            $"<meta name=\"chronicleaf-template-version\" content=\"{HtmlExportAdapter.TemplateVersion}\">",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "<meta name=\"chronicleaf-content-revision\" content=\"1\">",
            text,
            StringComparison.Ordinal);

        // The title is the milestone title the revision carries, escaped rather than trusted.
        Assert.Contains("<title>Reproducible release pipeline</title>", text, StringComparison.Ordinal);
        Assert.Contains("<h1>Reproducible release pipeline</h1>", text, StringComparison.Ordinal);

        // The audit row records that HTML bytes left the building, not a copy of them.
        var metadata = await ChronicleafPostgresFixture.ReadTextAsync(
            fixture.Database,
            "SELECT safe_metadata::text FROM audit_events WHERE action = 'content.review.exported' "
            + "ORDER BY occurred_at DESC LIMIT 1");
        Assert.False(string.IsNullOrEmpty(metadata));
        var detail = JsonDocument.Parse(metadata!).RootElement.GetProperty("detail").GetString()!;
        Assert.Contains("revision=1", detail, StringComparison.Ordinal);
        Assert.Contains("format=html", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.BodyText, metadata!, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // HTML-CONTENT-EXPORT-002 Failure: a cancelled export returns no partial archive.
    // ==============================================================================================

    [Fact]
    public async Task Html_002_Failure_cancelled_export_returns_no_partial_archive()
    {
        var adapter = new HtmlExportAdapter();
        var snapshot = new ApprovedAssetSnapshot(
            Guid.NewGuid(),
            1,
            "Reproducible release pipeline",
            "# Reproducible release pipeline\n\nThe pipeline now reproduces.\n",
            ContentRevision.Hash("# Reproducible release pipeline\n\nThe pipeline now reproduces.\n"),
            1,
            [new ApprovedExportPicture(Guid.NewGuid(), "A caption.", "image/png", [1, 2, 3])]);

        // The token is already cancelled before the first byte is read: the adapter throws
        // instead of returning anything, so there is no completed partial archive to mistake for
        // a result.
        Exception? caught = null;
        try
        {
            await adapter.ExportAsync(snapshot, new CancellationToken(canceled: true));
        }
        catch (OperationCanceledException thrown)
        {
            caught = thrown;
        }

        Assert.NotNull(caught);
    }

    // ==============================================================================================
    // HTML-CONTENT-EXPORT-002 Boundary: traversal text cannot escape the output root.
    // ==============================================================================================

    [Fact]
    public async Task Html_002_Boundary_traversal_text_cannot_escape_output_root()
    {
        await using var fixture = await ArrangeAsync("html_002_traversal", withPicture: true);

        await EditBodyAsync(
            fixture,
            $"{fixture.BodyText}\n\n../../evil.html\n\n"
            + "![escape](../../etc/passwd)\n\n"
            + "[local](./local-file)\n\n"
            + "![absolute](/etc/shadow)\n",
            title: "../../evil title");

        await ApproveAsync(fixture, 2, fixture.BundleRevision);

        using var exported = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=html");

        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);

        var bytes = await exported.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        // The archive holds exactly the generated entries: no owner text - body, caption or
        // title - reached a path, so no entry can name a parent directory.
        var names = archive.Entries.Select(entry => entry.FullName).ToList();
        Assert.Equal(
            ["images/image-0.png", "index.html", "manifest.json"],
            [.. names.Order(StringComparer.Ordinal)]);
        Assert.All(names, name => Assert.DoesNotContain("..", name, StringComparison.Ordinal));

        // The hostile title reached the document as escaped text, never the filename.
        Assert.Equal($"chronicleaf-{fixture.AssetId:N}-r2.zip", FileNameOf(exported));
        var text = Encoding.UTF8.GetString(await ReadEntryAsync(archive, "index.html"));
        Assert.Contains("../../evil title", text, StringComparison.Ordinal);
        Assert.Contains("../../evil.html", text, StringComparison.Ordinal);
        Assert.Contains("![escape](../../etc/passwd)", text, StringComparison.Ordinal);

        // Every image reference in the document is the packaged relative path: the source image
        // runs stayed characters and the gallery points inside the archive.
        Assert.DoesNotContain("src=\"..", text, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"/", text, StringComparison.Ordinal);
        Assert.Contains("src=\"images/image-0.png\"", text, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // Binding carry-over: the HTML shape reuses the review package's approval bindings rather than
    // redefining them.
    // ==============================================================================================

    [Fact]
    public async Task Html_Binding_export_without_approval_returns_approval_required()
    {
        await using var fixture = await ArrangeAsync("html_bind_no_approval");

        using var refused = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=html");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var envelope = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("approval_required", envelope.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Html_Binding_export_after_edit_returns_approval_stale()
    {
        await using var fixture = await ArrangeAsync("html_bind_stale");
        await ApproveAsync(fixture, 1, fixture.BundleRevision);

        await EditBodyAsync(fixture, $"{fixture.BodyText}\n\nA human correction, in the owner's own words.\n");

        using var stale = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=html");

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var envelope = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("approval_stale", envelope.RootElement.GetProperty("code").GetString());
    }

    // ==============================================================================================
    // Adapter refusals without a database: the archive bound and generated filenames.
    // ==============================================================================================

    [Fact]
    public async Task Html_Adapter_oversized_archive_is_refused_before_packing()
    {
        var adapter = new HtmlExportAdapter();
        var snapshot = new ApprovedAssetSnapshot(
            Guid.NewGuid(),
            1,
            "Reproducible release pipeline",
            "Words.\n",
            ContentRevision.Hash("Words.\n"),
            1,
            [new ApprovedExportPicture(
                Guid.NewGuid(),
                "A caption.",
                "image/png",
                new byte[HtmlExportAdapter.MaxArchiveBytes + 1])]);

        var thrown = await Assert.ThrowsAsync<PayloadTooLargeException>(
            () => adapter.ExportAsync(snapshot, CancellationToken.None));

        Assert.Equal(ApiError.PayloadTooLarge, thrown.Code);
    }

    [Fact]
    public async Task Html_Adapter_hostile_title_never_reaches_a_filename()
    {
        var adapter = new HtmlExportAdapter();
        var assetId = Guid.NewGuid();
        var snapshot = new ApprovedAssetSnapshot(
            assetId,
            1,
            "../../evil <script>alert('title')</script>",
            "Words.\n",
            ContentRevision.Hash("Words.\n"),
            1,
            []);

        var artifact = await adapter.ExportAsync(snapshot, CancellationToken.None);

        Assert.Equal($"chronicleaf-{assetId:N}-r1.html", artifact.FileName);
        Assert.DoesNotContain("..", artifact.FileName, StringComparison.Ordinal);

        var text = Encoding.UTF8.GetString(artifact.Bytes);
        Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", text, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // The fixture.
    // ==============================================================================================

    /// <summary>
    /// Builds everything a scenario needs: a real database, a real project, a confirmed milestone
    /// with real recorded evidence, a real sealed credential, a real authorized preview, a stored
    /// draft produced by a real generation job, and the asset that draft belongs to. With a picture,
    /// a real capture job runs first and its candidate is selected, so the evidence head the draft
    /// grounds in already carries the approval-covered picture.
    /// </summary>
    private async Task<Fixture> ArrangeAsync(string purpose, bool withPicture = false)
    {
        var database = await ProvisionedDatabaseAsync(purpose);
        var keyFile = OperatorKeyFile.Create();
        var clock = new FixedTimeProvider(Clock);

        Guid projectId;
        Guid milestoneId;
        Guid credentialId;

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

            // The generated body is English, so the voice it is judged under must be too: the
            // default profile declares Chinese and would report language findings on every field,
            // which is the blocked scenario's own condition rather than this fixture's.
            using var voiced = await setupOwner.SendAsync(await setupOwner.Put(
                $"/api/projects/{projectId:D}/voice-profile",
                new { presetId = "technical-blog", overrides = new { language = "en" } },
                0));

            Assert.Equal(HttpStatusCode.OK, voiced.StatusCode);
        }

        // One worker at a time shares this database. Each host runs its own job server over
        // the same job storage, so a capture host that is still alive when the draft is
        // dispatched could dequeue the draft job - and its container holds no generation
        // double, so the draft would fail against the real adapter. The capture host is
        // therefore disposed before the drafts host starts; the candidates, the selection and
        // the media bytes it wrote persist in the database and on disk.
        List<Guid> selectedMedia = [];
        HeadView head;

        if (withPicture)
        {
            await using var capture = CaptureApiHost.Start(
                database, clock, new RecordingScreenshotProvider(clock));
            using var pictureOwner = await OwnerClient.CreateAsync(capture);

            var candidates = await CaptureTwoAsync(capture, pictureOwner, milestoneId);
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
            head = await ReadHeadAsync(pictureOwner, milestoneId);
            Assert.Equal(2, head.Revision);
            selectedMedia.Add(first.MediaId);
        }
        else
        {
            await using var reader = GroundedMarkdownDraftsApiHost.StartProduction(
                database, clock, keyFile.FilePath);
            using var readerOwner = await OwnerClient.CreateAsync(reader);
            head = await ReadHeadAsync(readerOwner, milestoneId);
        }

        // The drafts host shares the database - and therefore the milestone, the evidence and
        // the stored media - with the capture host above. Only the seams differ: this host
        // answers generation, that one answers captures.
        var summary = SummaryJson(head.ManualItemId);
        var scripted = ScriptedDraftProvider.With(summary, BodyFor(summary));

        var host = GroundedMarkdownDraftsApiHost.Start(database, clock, keyFile.FilePath, scripted);
        var owner = await OwnerClient.CreateAsync(host);

        using (var probe = host.Services.CreateScope())
        {
            var resolver = probe.ServiceProvider.GetRequiredService<ILLMProviderResolver>();
            var found = resolver.Find(ScriptedDraftProvider.Slot);
            Assert.Same(scripted, found);
        }

        credentialId = await SaveCredentialAsync(owner);

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
                idempotencyKey = $"html-{purpose}",
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
                $"{timeout.Message} Recorded error code: {last?.ErrorCode ?? "none"}. "
                + $"Provider calls: {scripted.SummaryCalls} summaries, "
                + $"{scripted.MarkdownCalls} markdown.",
                timeout);
        }

        var asset = await ContentRows.ReadAssetForMilestoneAsync(database, milestoneId);
        Assert.NotNull(asset);

        var revision = await ContentRows.ReadRevisionAsync(database, asset!.Id, 1);
        Assert.NotNull(revision);

        return new Fixture(
            database,
            host,
            keyFile,
            owner,
            scripted,
            projectId,
            milestoneId,
            asset.Id,
            credentialId,
            revision!.BodyMarkdown,
            revision.BodyHash,
            head.BundleId,
            head.Revision,
            selectedMedia);
    }

    private static async Task ApproveAsync(Fixture fixture, long contentRevision, long evidenceRevision)
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

    private static async Task EditBodyAsync(Fixture fixture, string body, string? title = null)
    {
        var asset = await ContentRows.ReadAssetForMilestoneAsync(fixture.Database, fixture.MilestoneId);
        Assert.NotNull(asset);

        using var edited = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/assets/{fixture.AssetId:D}",
            asset!.CurrentRevision,
            new { expectedRevision = asset.CurrentRevision, bodyMarkdown = body, title }));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
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
                idempotencyKey = $"html-capture-{Guid.NewGuid():N}",
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

    private static string FileNameOf(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);

        return (disposition.FileNameStar ?? disposition.FileName ?? string.Empty).Trim('"');
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var position = 0;

        while ((position = text.IndexOf(value, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += value.Length;
        }

        return count;
    }

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
        ScriptedDraftProvider Provider,
        Guid ProjectId,
        Guid MilestoneId,
        Guid AssetId,
        Guid CredentialId,
        string BodyText,
        string BodyHash,
        Guid EvidenceBundleId,
        long BundleRevision,
        IReadOnlyList<Guid> SelectedMediaIds) : IAsyncDisposable
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

    /// <summary>
    /// A body that satisfies every local citation rule, built from the claim map the summary
    /// produces rather than hand-written, so the footnote ids are the ones the package assigned.
    /// </summary>
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
