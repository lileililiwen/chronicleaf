using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.AI.Contracts;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.AI;
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
/// MEDIUM-IMPORT-PACKAGE-001 and MEDIUM-IMPORT-PACKAGE-002 against a real disposable PostgreSQL,
/// the real <c>Chronicleaf.Api</c> host, the real operation queue and the real evidence, voice,
/// asset, review and media contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the voice
/// service, the asset store, the approvals table, the media store, the routes, the access
/// boundary and the database. The approved story every scenario exports is produced by a real
/// generation job through the same scripted provider the drafts suite drives - the only
/// substitution for prose, and the same declared boundary: no stage here may be read as evidence
/// that any provider works. Packaging itself calls no provider at all. The selected picture is a
/// real capture job's candidate, selected through the delivered curation route, behind the same
/// scripted-browser seam the capture suite declares: the PNG bytes are a labelled synthesised
/// fixture, and no scenario here claims a real page was photographed. The capture host is
/// disposed before the drafts host starts, on the same terms as the HTML export suite, so the
/// two hosts' workers never share this database.
/// </para>
/// <para>
/// <b>What is never touched.</b> No Medium endpoint, no account, no token and no OAuth scope
/// exists anywhere in this suite, because none exists in the package: the publish route is
/// asserted as a refusal with no outbound request possible, and the suite proves the refusal
/// changed nothing rather than proving anything about a network it never uses.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> An article is read out of the raw <c>content_assets</c>
/// and <c>content_revisions</c> tables, not only from the export route, and an approval is read
/// out of the raw <c>content_approvals</c> table. The claims here are about what is
/// <em>stored</em> and what is <em>served</em>, and a value read back through the same
/// serializer that wrote it would agree with itself whether or not the bytes on disk were
/// right.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class MediumImportPackageTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("f2b7d4a1-5c39-4e7f-8b1d-9c6f2a5e4b73");

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The evidence every import scenario derives from. Free of digits on purpose, for the same
    /// reason the drafts suite's own note is: the numeral rule refuses a figure that appears in
    /// no evidence item, and a note full of numbers would let claims pass for the wrong reason.
    /// </summary>
    private const string EvidenceNote =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string KnownLimitation = "Only Linux is covered.";

    private const string PictureCaption = "The pipeline digest view.";

    // ==============================================================================================
    // MEDIUM-IMPORT-PACKAGE-001 Success: an approved story exports article, images and manifest.
    // ==============================================================================================

    [Fact]
    public async Task Medium_001_Success_approved_story_exports_article_images_manifest_and_instructions()
    {
        await using var fixture = await ArrangeAsync("mi_001_success");

        using var export = await fixture.Owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/export?format=medium");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);

        var fileName = export.Content.Headers.ContentDisposition?.FileNameStar
            ?? export.Content.Headers.ContentDisposition?.FileName;
        Assert.Contains("medium.zip", fileName, StringComparison.Ordinal);

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.StoryAssetId, 1);
        Assert.NotNull(stored);

        using var archive = new ZipArchive(await export.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);

        // article.md is exactly the approved bytes, not a re-render.
        var markdownBytes = await ReadEntryAsync(archive, "article.md");
        Assert.Equal(Encoding.UTF8.GetBytes(stored!.BodyMarkdown), markdownBytes);

        // article.html carries the approved prose in its sanitized shape.
        var articleText = Encoding.UTF8.GetString(await ReadEntryAsync(archive, "article.html"));
        Assert.Contains("Reproducible release pipeline", articleText, StringComparison.Ordinal);
        Assert.Contains("The pipeline now reproduces from a clean checkout.", articleText, StringComparison.Ordinal);

        // The manifest attributes the material to its origin: the asset, the revision and the
        // digest of exactly the bytes beside it.
        var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));
        Assert.Equal(fixture.StoryAssetId.ToString("D"), manifest.RootElement.GetProperty("assetId").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("contentRevision").GetInt64());
        Assert.Equal(stored.BodyHash, manifest.RootElement.GetProperty("bodyHash").GetString());
        Assert.Equal("article.html", manifest.RootElement.GetProperty("articleFile").GetString());
        Assert.Equal("article.md", manifest.RootElement.GetProperty("markdownFile").GetString());

        var image = Assert.Single(manifest.RootElement.GetProperty("images").EnumerateArray());

        var expectedPng = TestPng.Create(64, 40);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expectedPng)).ToLowerInvariant(),
            image.GetProperty("sha256").GetString());

        // And the served picture is those exact bytes under the manifest's own path.
        var servedPng = await ReadEntryAsync(archive, image.GetProperty("file").GetString()!);
        Assert.Equal(expectedPng, servedPng);

        // The instructions say what the package is, what it is not, and where the material
        // came from: export-only, no post made, and the asset, revision and digest above.
        var instructions = Encoding.UTF8.GetString(await ReadEntryAsync(archive, "MEDIUM-IMPORT.txt"));
        Assert.Contains("BY HAND", instructions, StringComparison.Ordinal);
        Assert.Contains("no post was made", instructions, StringComparison.Ordinal);
        Assert.Contains(fixture.StoryAssetId.ToString("D"), instructions, StringComparison.Ordinal);
        Assert.Contains(stored.BodyHash, instructions, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // MEDIUM-IMPORT-PACKAGE-001 Success: a text-only story exports with no pictures invented.
    // ==============================================================================================

    [Fact]
    public async Task Medium_001_Success_text_only_story_exports_article_without_images()
    {
        await using var fixture = await ArrangeAsync("mi_001_plain", withPicture: false);

        using var export = await fixture.Owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/export?format=medium");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        using var archive = new ZipArchive(await export.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.StoryAssetId, 1);
        Assert.NotNull(stored);
        Assert.Equal(Encoding.UTF8.GetBytes(stored!.BodyMarkdown), await ReadEntryAsync(archive, "article.md"));

        var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));
        Assert.Equal(0, manifest.RootElement.GetProperty("images").GetArrayLength());

        // No picture entries were invented to fill the package: the only files are the article,
        // its Markdown, the manifest and the instructions.
        var names = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray();
        Assert.Equal(["article.html", "article.md", "manifest.json", "MEDIUM-IMPORT.txt"], names);
    }

    // ==============================================================================================
    // MEDIUM-IMPORT-PACKAGE-001 Failure: native publish is refused with no account action.
    // ==============================================================================================

    [Fact]
    public async Task Medium_001_Failure_native_publish_is_refused_without_account_action()
    {
        await using var fixture = await ArrangeAsync("mi_001_publish");

        var operationsBefore = await ChronicleafPostgresFixture.ReadTextAsync(
            fixture.Database,
            "SELECT COUNT(*)::text FROM operations");
        var auditsBefore = await NonSessionAuditCountAsync(fixture.Database);

        // Even approved, even reviewed, publication is refused: there is no transport behind it.
        using var refused = await fixture.Owner.SendAsync(
            await fixture.Owner.PostWithoutBody($"/api/assets/{fixture.StoryAssetId:D}/adaptations/medium/publish"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(
            "publisher_unsupported",
            JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        // A refusal that changed anything would be an account action of its own: no operation
        // was enqueued and no audit row was written. The access boundary audits its own
        // per-mutation antiforgery rotation, so the comparison excludes the session rows every
        // request carries and counts what the package could have written instead.
        Assert.Equal(
            operationsBefore,
            await ChronicleafPostgresFixture.ReadTextAsync(
                fixture.Database,
                "SELECT COUNT(*)::text FROM operations"));
        Assert.Equal(auditsBefore, await NonSessionAuditCountAsync(fixture.Database));

        using var review = await fixture.Owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/review");
        Assert.Equal(
            "approved",
            JsonDocument.Parse(await review.Content.ReadAsStringAsync()).RootElement.GetProperty("reviewStatus").GetString());

        // A foreign id is still the delivered 404 rather than a transport refusal.
        using var foreign = await fixture.Owner.SendAsync(
            await fixture.Owner.PostWithoutBody($"/api/assets/{Guid.NewGuid():D}/adaptations/medium/publish"));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    // ==============================================================================================
    // MEDIUM-IMPORT-PACKAGE-001 Boundary: no remote URL or successful post is fabricated.
    // ==============================================================================================

    [Fact]
    public async Task Medium_001_Boundary_export_fabricates_no_remote_url_or_post()
    {
        await using var fixture = await ArrangeAsync("mi_001_honest");

        using var first = await fixture.Owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/export?format=medium");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBytes = await first.Content.ReadAsByteArrayAsync();

        using var second = await fixture.Owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/export?format=medium");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBytes = await second.Content.ReadAsByteArrayAsync();

        // The same approved revision packs identically: nothing here records when the export
        // ran, so a second package cannot claim to be a newer post.
        Assert.Equal(firstBytes, secondBytes);

        using var archive = new ZipArchive(new MemoryStream(firstBytes), ZipArchiveMode.Read);

        var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));

        // The manifest attributes an origin, not a destination: no URL, no post id, no
        // publication status of any kind.
        foreach (var property in manifest.RootElement.EnumerateObject())
        {
            Assert.DoesNotContain("url", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("post", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("publish", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("status", property.Name, StringComparison.OrdinalIgnoreCase);
        }

        // No entry reads as a published page: no Medium story address and no success claim.
        foreach (var entry in archive.Entries)
        {
            var text = Encoding.UTF8.GetString(await ReadEntryAsync(archive, entry.FullName));
            Assert.DoesNotContain("medium.com/p/", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("medium.com/@", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("successfully published", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("is now live", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ==============================================================================================
    // MEDIUM-IMPORT-PACKAGE-002 Success: an unedited source exports its approved snapshot.
    // ==============================================================================================

    [Fact]
    public async Task Medium_002_Success_unedited_source_exports_approved_snapshot()
    {
        await using var fixture = await ArrangeAsync("mi_002_snapshot");

        using var export = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.StoryAssetId:D}/export?format=medium&revision=1");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.StoryAssetId, 1);
        Assert.NotNull(stored);

        using var archive = new ZipArchive(await export.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);

        // The export uses the approved snapshot: the Markdown is the stored bytes and the
        // manifest's digest describes exactly those bytes.
        Assert.Equal(Encoding.UTF8.GetBytes(stored!.BodyMarkdown), await ReadEntryAsync(archive, "article.md"));

        var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));
        Assert.Equal(1, manifest.RootElement.GetProperty("contentRevision").GetInt64());
        Assert.Equal(
            ContentRevision.Hash(Encoding.UTF8.GetString(await ReadEntryAsync(archive, "article.md"))),
            manifest.RootElement.GetProperty("bodyHash").GetString());
    }

    // ==============================================================================================
    // MEDIUM-IMPORT-PACKAGE-002 Failure: never approved exports nothing; an edit ends approval.
    // ==============================================================================================

    [Fact]
    public async Task Medium_002_Failure_unapproved_and_edited_sources_export_nothing()
    {
        await using var fixture = await ArrangeAsync("mi_002_stale", approveStory: false);

        // Never approved is approval_required: no decision stands behind the text.
        using var bare = await fixture.Owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/export?format=medium");
        Assert.Equal(HttpStatusCode.Conflict, bare.StatusCode);
        Assert.Equal(
            "approval_required",
            JsonDocument.Parse(await bare.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        await ApproveAsync(fixture.Owner, fixture.StoryAssetId, 1, fixture.BundleRevision);

        using var review = await fixture.Owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/review");
        Assert.Equal(
            "approved",
            JsonDocument.Parse(await review.Content.ReadAsStringAsync()).RootElement.GetProperty("reviewStatus").GetString());

        // A derivative title or body edit creates a separate draft revision requiring its own
        // approval: the export is stale and the review says invalidated, under the same
        // bindings the Markdown export re-checks.
        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.StoryAssetId, 1);
        Assert.NotNull(stored);

        using var edited = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/assets/{fixture.StoryAssetId:D}",
            1,
            new
            {
                expectedRevision = 1,
                bodyMarkdown = stored!.BodyMarkdown + "\nOwner correction.\n",
                title = (string?)null,
            }));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        using var stale = await fixture.Owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/export?format=medium");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "approval_stale",
            JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        using var reread = await fixture.Owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/review");
        Assert.Equal(
            "invalidated",
            JsonDocument.Parse(await reread.Content.ReadAsStringAsync()).RootElement.GetProperty("reviewStatus").GetString());
    }

    // ==============================================================================================
    // MEDIUM-IMPORT-PACKAGE-002 Boundary: a missing image is not a complete archive.
    // ==============================================================================================

    [Fact]
    public async Task Medium_002_Boundary_missing_image_is_not_a_complete_archive()
    {
        await using var fixture = await ArrangeAsync("mi_002_gone");
        Assert.NotNull(fixture.MediaId);

        // The bytes are lost behind the approval's back: the row stays as the record that
        // they were, but its status is no longer servable - which is exactly what the media
        // store's own contract says makes a record unreadable. The approval still binds the
        // picture, so the export is refused rather than served picture-less.
        await using (var connection = new NpgsqlConnection(fixture.Database))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE media SET status = 'deleted' WHERE id = '{fixture.MediaId!.Value:D}'";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        using var refused = await fixture.Owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/export?format=medium");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var envelope = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("media_unavailable", envelope.RootElement.GetProperty("code").GetString());
    }

    private static async Task<string?> NonSessionAuditCountAsync(string connectionString) =>
        await ChronicleafPostgresFixture.ReadTextAsync(
            connectionString,
            "SELECT COUNT(*)::text FROM audit_events WHERE action NOT LIKE 'session.%'");

    // ==============================================================================================
    // The fixture.
    // ==============================================================================================

    /// <summary>
    /// Builds everything a scenario needs: a real database, a real project, a confirmed milestone
    /// with real recorded evidence, a real sealed credential, a real authorized preview, a stored
    /// draft produced by a real generation job, the approved story it belongs to, and - with a
    /// picture - a real capture job's selected candidate already carried in the evidence head the
    /// story grounds in.
    /// </summary>
    private async Task<Fixture> ArrangeAsync(
        string purpose,
        bool withPicture = true,
        bool approveStory = true)
    {
        var database = await ProvisionedDatabaseAsync(purpose);
        var keyFile = OperatorKeyFile.Create();
        var clock = new FixedTimeProvider(Clock);

        Guid projectId;
        Guid milestoneId;

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
            // which would refuse the approval this fixture needs.
            using var voiced = await setupOwner.SendAsync(await setupOwner.Put(
                $"/api/projects/{projectId:D}/voice-profile",
                new { presetId = "linkedin", overrides = new { } },
                0));

            Assert.True(
                voiced.StatusCode == HttpStatusCode.OK,
                $"voice answered {(int)voiced.StatusCode}: {await voiced.Content.ReadAsStringAsync()}");
        }

        Guid? mediaId = null;
        HeadView head;

        if (withPicture)
        {
            await using var capture = CaptureApiHost.Start(
                database, clock, new RecordingScreenshotProvider(clock));
            using var pictureOwner = await OwnerClient.CreateAsync(capture);

            capture.Provider.AnswerWithPng(width: 64, height: 40);

            using var requested = await pictureOwner.SendAsync(await pictureOwner.Post(
                $"/api/milestones/{milestoneId:D}/captures",
                new
                {
                    url = "https://example.com/pipeline",
                    viewports = new[] { "desktop" },
                    waitMs = 0,
                    fullPage = false,
                    expectedBundleRevision = (long?)null,
                    idempotencyKey = $"mi-capture-{purpose}",
                },
                0));

            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
            var operationId = Guid.Parse(JsonDocument
                .Parse(await requested.Content.ReadAsStringAsync())
                .RootElement.GetProperty("operationId").GetString()!);

            await capture.DispatchAsync();
            await OperationRows.AwaitAsync(database, operationId, "succeeded", TimeSpan.FromSeconds(5));

            using var candidates = await pictureOwner.GetAsync($"/api/milestones/{milestoneId:D}/screenshots");
            var candidate = JsonDocument.Parse(await candidates.Content.ReadAsStringAsync())
                .RootElement.GetProperty("items").EnumerateArray().First();

            var screenshotId = Guid.Parse(candidate.GetProperty("id").GetString()!);
            var screenshotRevision = candidate.GetProperty("revision").GetInt64();
            mediaId = Guid.Parse(candidate.GetProperty("mediaId").GetString()!);

            using var selected = await pictureOwner.SendAsync(await pictureOwner.Patch(
                $"/api/screenshots/{screenshotId:D}",
                screenshotRevision,
                new
                {
                    status = "selected",
                    caption = PictureCaption,
                    expectedRevision = screenshotRevision,
                    expectedBundleRevision = 1,
                }));

            Assert.True(
                selected.StatusCode == HttpStatusCode.OK,
                $"select answered {(int)selected.StatusCode}: {await selected.Content.ReadAsStringAsync()}");

            // The select appended the second bundle revision: the carried manual fact has a new
            // id there, so the summary cites the head's ids rather than the first revision's.
            await using var reader = GroundedMarkdownDraftsApiHost.StartProduction(
                database, clock, keyFile.FilePath);
            using var readerOwner = await OwnerClient.CreateAsync(reader);
            head = await ReadHeadAsync(readerOwner, milestoneId);
            Assert.Equal(2, head.Revision);
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
        // answers generation, that one answered captures.
        var summary = SummaryJson(head.ManualItemId);
        var scripted = ScriptedDraftProvider.With(summary, BodyFor(summary));

        var host = GroundedMarkdownDraftsApiHost.Start(database, clock, keyFile.FilePath, scripted);
        var owner = await OwnerClient.CreateAsync(host);

        using (var probe = host.Services.CreateScope())
        {
            var resolver = probe.ServiceProvider.GetRequiredService<ILLMProviderResolver>();
            Assert.Same(scripted, resolver.Find(ScriptedDraftProvider.Slot));
        }

        var credentialId = await SaveCredentialAsync(owner);
        var preview = await CreatePreviewAsync(host, owner, milestoneId, credentialId, head.Revision, head.ManualItemId);

        using var requested2 = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/drafts",
            new
            {
                previewId = preview.PreviewId.ToString("D"),
                consentHash = preview.ConsentHash,
                providerId = ScriptedDraftProvider.Slot,
                model = ScriptedDraftProvider.KnownModel,
                evidenceRevision = preview.EvidenceRevision,
                voiceRevision = preview.VoiceRevision,
                idempotencyKey = $"mi-{purpose}",
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested2.StatusCode);
        var accepted = JsonDocument.Parse(await requested2.Content.ReadAsStringAsync());
        var draftOperationId = accepted.RootElement.GetProperty("operationId").GetGuid();

        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, draftOperationId, "succeeded", TimeSpan.FromSeconds(5));

        var asset = await ContentRows.ReadAssetForMilestoneAsync(database, milestoneId);
        Assert.NotNull(asset);

        using var voiced2 = await owner.GetAsync($"/api/projects/{projectId:D}/voice-profile");
        var voiceRevision = JsonDocument
            .Parse(await voiced2.Content.ReadAsStringAsync())
            .RootElement.GetProperty("revision").GetInt64();

        var fixture = new Fixture(
            database,
            host,
            keyFile,
            owner,
            projectId,
            milestoneId,
            asset!.Id,
            head.Revision,
            voiceRevision,
            mediaId);

        if (approveStory)
        {
            await ApproveAsync(fixture.Owner, fixture.StoryAssetId, 1, fixture.BundleRevision);
        }

        return fixture;
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

    private static async Task<Guid> CreateMilestoneAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/milestones",
            new
            {
                title = "Reproducible release pipeline",
                version = "0.1.0",
                occurredAt = "2026-10-01T09:00:00Z",
                type = "feature",
                notes = "Record of a pipeline that reproduces from a clean checkout.",
                knownLimitations = new[] { KnownLimitation },
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

    private static async Task<byte[]> ReadEntryAsync(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);

        await using var stream = entry!.Open();
        using var buffered = new MemoryStream();
        await stream.CopyToAsync(buffered);

        return buffered.ToArray();
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

    private sealed record HeadView(Guid BundleId, long Revision, Guid ManualItemId);

    private sealed record PreviewBody(Guid PreviewId, string ConsentHash, long EvidenceRevision, long VoiceRevision);

    /// <summary>
    /// Everything one scenario holds, and the disposal it owes the shared server.
    /// </summary>
    private sealed record Fixture(
        string Database,
        GroundedMarkdownDraftsApiHost Host,
        OperatorKeyFile KeyFile,
        OwnerClient Owner,
        Guid ProjectId,
        Guid MilestoneId,
        Guid StoryAssetId,
        long BundleRevision,
        long VoiceRevision,
        Guid? MediaId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Host.DisposeAsync();
            KeyFile.Dispose();
        }
    }
}
