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
/// LINKEDIN-COPY-PACKAGE-001 and LINKEDIN-COPY-PACKAGE-002 against a real disposable PostgreSQL,
/// the real <c>Chronicleaf.Api</c> host, the real operation queue and the real evidence, voice,
/// asset, review and media contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the voice
/// service, the asset store, the approvals table, the media store, the routes, the access
/// boundary and the database. The approved story every scenario adapts from is produced by a
/// real generation job through the same scripted provider the drafts suite drives - the only
/// substitution for prose, and the same declared boundary: no stage here may be read as evidence
/// that any provider works. Adapting itself calls no provider at all. The selected picture is a
/// real capture job's candidate, selected through the delivered curation route, behind the same
/// scripted-browser seam the capture suite declares: the PNG bytes are a labelled synthesised
/// fixture, and no scenario here claims a real page was photographed. The capture host is
/// disposed before the drafts host starts, on the same terms as the HTML export suite, so the
/// two hosts' workers never share this database.
/// </para>
/// <para>
/// <b>What is never touched.</b> No LinkedIn endpoint, no account, no token and no OAuth scope
/// exists anywhere in this suite, because none exists in the package: the publish route is
/// asserted as a refusal with no outbound request possible, and the suite proves the refusal
/// changed nothing rather than proving anything about a network it never uses.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> A copy is read out of the raw <c>content_assets</c>,
/// <c>content_revisions</c> and <c>linkedin_copy_media</c> tables, not only from the adapt route,
/// and an approval is read out of the raw <c>content_approvals</c> table. The claims here are
/// about what is <em>stored</em> and what is <em>served</em>, and a value read back through the
/// same serializer that wrote it would agree with itself whether or not the bytes on disk were
/// right.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class LinkedinCopyPackageTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("e7a3c1f9-4b28-4d6e-9f2a-8c5e1b7d3a40");

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The evidence every copy scenario adapts from. Free of digits on purpose, for the same
    /// reason the drafts suite's own note is: the numeral rule refuses a figure that appears in
    /// no evidence item, and a note full of numbers would let claims pass for the wrong reason.
    /// </summary>
    private const string EvidenceNote =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string KnownLimitation = "Only Linux is covered.";

    private const string PictureCaption = "The pipeline digest view.";

    private const string PictureAltText = "A terminal showing two matching digest lines.";

    private const string CallToAction = "If this helped your team, share how you verify releases.";

    // ==============================================================================================
    // LINKEDIN-COPY-PACKAGE-001 Success: adapt stores grounded copy with snapshot and selection.
    // ==============================================================================================

    [Fact]
    public async Task Linkedin_001_Success_adapt_stores_grounded_copy_with_snapshot_and_selection()
    {
        await using var fixture = await ArrangeAsync("li_001_success");

        using var adapted = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
            new
            {
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
                callToAction = CallToAction,
                hashtags = new[] { "#buildinpublic", "#releases" },
                pictures = new[] { new { mediaId = fixture.MediaId!.Value.ToString("D"), altText = PictureAltText } },
            }));

        Assert.True(
            adapted.StatusCode == HttpStatusCode.Created,
            $"adapt answered {(int)adapted.StatusCode}: {await adapted.Content.ReadAsStringAsync()}");

        var answer = JsonDocument.Parse(await adapted.Content.ReadAsStringAsync());
        var assetId = answer.RootElement.GetProperty("assetId").GetGuid();

        Assert.NotEqual(fixture.StoryAssetId, assetId);
        Assert.Equal(1, answer.RootElement.GetProperty("revision").GetInt64());
        Assert.False(answer.RootElement.GetProperty("lengthWarning").GetBoolean());
        Assert.True(answer.RootElement.GetProperty("hasCallToAction").GetBoolean());
        Assert.Equal(
            fixture.MediaId!.Value.ToString("D"),
            Assert.Single(answer.RootElement.GetProperty("selectedMediaIds").EnumerateArray()).GetString());

        // The stored bytes, read out of the rows rather than the route.
        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 1);
        Assert.NotNull(stored);
        Assert.Equal(answer.RootElement.GetProperty("bodyHash").GetString(), stored!.BodyHash);
        Assert.Equal(ContentRevision.Hash(stored.BodyMarkdown), stored.BodyHash);
        Assert.Equal(stored.BodyMarkdown.Length, answer.RootElement.GetProperty("charCount").GetInt32());

        // Supported facts with the citations the claim map assigned, the owner's own closing
        // line and tags, and every footnote defined: a citation is never a reference to nothing.
        Assert.Contains("The pipeline now reproduces from a clean checkout.", stored.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("The pipeline runs from a clean checkout.", stored.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains(KnownLimitation, stored.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains(CallToAction, stored.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("#buildinpublic #releases", stored.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("[^", stored.BodyMarkdown, StringComparison.Ordinal);

        // The asset reads as a LinkedIn copy draft, and the review package reports exactly that:
        // a draft that was never approved, with no approval row anywhere behind it.
        using var read = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}");
        var view = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("draft", view.RootElement.GetProperty("status").GetString());
        Assert.Equal("linkedin-copy", view.RootElement.GetProperty("type").GetString());

        using var review = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}/review");
        var state = JsonDocument.Parse(await review.Content.ReadAsStringAsync());
        Assert.Equal("draft", state.RootElement.GetProperty("reviewStatus").GetString());

        Assert.Empty(await ReviewRows.ListAsync(fixture.Database, assetId));

        // And the source approval is exactly where it was: adapting published nothing about it.
        var sourceApprovals = await ReviewRows.ListAsync(fixture.Database, fixture.StoryAssetId);
        Assert.Single(sourceApprovals);
        Assert.Null(sourceApprovals[0].InvalidatedAt);

        // The selection rows name the picture and the owner's accessibility words.
        var selection = await ReadSelectionAsync(fixture.Database, assetId, 1);
        Assert.Equal(fixture.MediaId!.Value, Assert.Single(selection).MediaId);
        Assert.Equal(PictureAltText, Assert.Single(selection).AltText);

        // The copy carries the voice it was adapted under as a value: a later profile edit
        // cannot reach the stored bytes.
        var snapshotBefore = stored.VoiceSnapshot;
        Assert.Contains("linkedin", snapshotBefore, StringComparison.Ordinal);

        using var profile = await fixture.Owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/voice-profile");
        var profileBody = JsonDocument.Parse(await profile.Content.ReadAsStringAsync());
        var profileRevision = profileBody.RootElement.GetProperty("revision").GetInt64();

        using var moved = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.ProjectId:D}/voice-profile",
            new { presetId = "devlog", overrides = new { language = "en" } },
            profileRevision));

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var reread = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 1);
        Assert.NotNull(reread);
        Assert.Equal(snapshotBefore, reread!.VoiceSnapshot);
    }

    // ==============================================================================================
    // LINKEDIN-COPY-PACKAGE-001 Failure: an explicit project restriction wins over the preset.
    // ==============================================================================================

    [Fact]
    public async Task Linkedin_001_Failure_project_voice_restriction_wins_over_the_preset()
    {
        // The xiaohongshu preset asks for emoji and medium marketing; the project explicitly
        // refuses both. The copy must carry the restriction, not the preset.
        await using var fixture = await ArrangeAsync(
            "li_001_voice",
            presetId: "xiaohongshu",
            overrides: new { emoji = false, marketing = "low", language = "en" });

        using var adapted = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
            new
            {
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
                callToAction = (string?)null,
                hashtags = Array.Empty<string>(),
                pictures = Array.Empty<object>(),
            }));

        Assert.True(
            adapted.StatusCode == HttpStatusCode.Created,
            $"adapt answered {(int)adapted.StatusCode}: {await adapted.Content.ReadAsStringAsync()}");

        var assetId = JsonDocument.Parse(await adapted.Content.ReadAsStringAsync())
            .RootElement.GetProperty("assetId").GetGuid();

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 1);
        Assert.NotNull(stored);

        // Structurally emoji-free: the template never emits what the restriction forbids, so the
        // preset's allowance cannot reach the text.
        Assert.DoesNotContain("🎉", stored!.BodyMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("game-changing", stored.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exciting news", stored.BodyMarkdown, StringComparison.OrdinalIgnoreCase);

        // And the approval the review package performs - which refuses while any voice finding
        // remains - succeeds, which is the proof no finding remained rather than an assertion
        // that one surface agrees with itself.
        using var profile = await fixture.Owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/voice-profile");
        var fields = JsonDocument.Parse(await profile.Content.ReadAsStringAsync())
            .RootElement.GetProperty("fields");

        var emoji = fields.EnumerateArray().Single(field => field.GetProperty("field").GetString() == "emoji");
        Assert.Equal("true", emoji.GetProperty("presetValue").GetString());
        Assert.Equal("false", emoji.GetProperty("effectiveValue").GetString());
        Assert.True(emoji.GetProperty("overridden").GetBoolean());

        await ApproveAsync(fixture.Owner, assetId, 1, fixture.BundleRevision);

        var approvals = await ReviewRows.ListAsync(fixture.Database, assetId);
        Assert.Single(approvals);
    }

    // ==============================================================================================
    // LINKEDIN-COPY-PACKAGE-001 Boundary: no call to action stays informational.
    // ==============================================================================================

    [Fact]
    public async Task Linkedin_001_Boundary_copy_without_call_to_action_stays_informational()
    {
        await using var fixture = await ArrangeAsync("li_001_plain", withPicture: false);

        using var adapted = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
            new
            {
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
                callToAction = (string?)null,
                hashtags = Array.Empty<string>(),
                pictures = Array.Empty<object>(),
            }));

        Assert.True(
            adapted.StatusCode == HttpStatusCode.Created,
            $"adapt answered {(int)adapted.StatusCode}: {await adapted.Content.ReadAsStringAsync()}");

        var answer = JsonDocument.Parse(await adapted.Content.ReadAsStringAsync());
        Assert.False(answer.RootElement.GetProperty("hasCallToAction").GetBoolean());
        Assert.Empty(answer.RootElement.GetProperty("selectedMediaIds").EnumerateArray());

        var assetId = answer.RootElement.GetProperty("assetId").GetGuid();
        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 1);
        Assert.NotNull(stored);

        // Informational: the facts and their citations, and no invented growth promise on the
        // owner's behalf.
        Assert.Contains("The pipeline now reproduces from a clean checkout.", stored!.BodyMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("followers", stored.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("viral", stored.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subscribe", stored.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("comment below", stored.BodyMarkdown, StringComparison.OrdinalIgnoreCase);

        await ApproveAsync(fixture.Owner, assetId, 1, fixture.BundleRevision);

        // A text-only copy exports as copy plus an empty manifest: no pictures invented to fill it.
        using var export = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}/export?format=linkedin");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        using var archive = new ZipArchive(await export.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));
        Assert.Equal(0, manifest.RootElement.GetProperty("images").GetArrayLength());
        Assert.Equal("copy.txt", manifest.RootElement.GetProperty("copyFile").GetString());
    }

    // ==============================================================================================
    // LINKEDIN-COPY-PACKAGE-001 refusals: every invalid adaptation stores nothing.
    // ==============================================================================================

    [Fact]
    public async Task Linkedin_001_Refusals_invalid_adaptations_store_nothing()
    {
        await using var fixture = await ArrangeAsync("li_001_refusals", approveStory: false);

        // An unapproved source is approval_required: no decision stands behind the text.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
                ValidAdapt(fixture, fixture.MediaId)),
            HttpStatusCode.Conflict,
            "approval_required");

        // An unknown source is the delivered 404, with no scope crossed to answer it.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{Guid.NewGuid():D}/adaptations/linkedin",
                ValidAdapt(fixture, fixture.MediaId)),
            HttpStatusCode.NotFound,
            "not_found");

        // A body that names nothing is refused by name rather than read as revision zero.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
                new { }),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        await ApproveStoryAsync(fixture);

        // A moved evidence head is a conflict with the record's own state, not a corrected body.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
                ValidAdapt(fixture, fixture.MediaId, evidenceRevision: fixture.BundleRevision - 1)),
            HttpStatusCode.Conflict,
            "revision_conflict");

        // A hashtags list over the bound is refused before any tag is read.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
                ValidAdapt(
                    fixture,
                    fixture.MediaId,
                    hashtags: new[] { "#one", "#two", "#three", "#four", "#five", "#six" })),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // A tag that is not a tag is refused by name: the rule is deterministic, not one the
        // owner's client has to guess.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
                ValidAdapt(fixture, fixture.MediaId, hashtags: new[] { "#no spaces" })),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // A call to action over its bound is refused before it is read.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
                ValidAdapt(fixture, fixture.MediaId, callToAction: new string('w', 281))),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // A selected picture without accessibility words is refused: a manifest entry with no
        // words would be a picture this product agreed to publish inaccessibly.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
                ValidAdapt(
                    fixture,
                    fixture.MediaId,
                    pictures: new[] { new { mediaId = fixture.MediaId!.Value.ToString("D"), altText = "  " } })),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // Five pictures are one over the bound, and the bound fires before existence is
        // consulted: the ids below name nothing, and the answer is still about the count.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
                ValidAdapt(
                    fixture,
                    fixture.MediaId,
                    pictures: Enumerable.Range(0, 5).Select(index => new
                    {
                        mediaId = Guid.NewGuid().ToString("D"),
                        altText = $"Picture {index}.",
                    }).ToArray())),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // A name that matches no selectable picture is media_unavailable: the frozen bundle holds
        // no such bytes, so the manifest would have a hole in it.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
                ValidAdapt(
                    fixture,
                    fixture.MediaId,
                    pictures: new[] { new { mediaId = Guid.NewGuid().ToString("D"), altText = PictureAltText } })),
            HttpStatusCode.Conflict,
            "media_unavailable");

        // A figure the evidence never stated is refused even in the owner's own words: the copy
        // is grounded text, and a call to action is inside the checked bytes.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
                ValidAdapt(fixture, fixture.MediaId, callToAction: "Version 2 ships this week.")),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // Nothing above stored anything: no LinkedIn copy row exists for this milestone.
        Assert.Null(await ReadLinkedInAssetAsync(fixture.Database, fixture.MilestoneId));
        Assert.Equal(
            "0",
            await ChronicleafPostgresFixture.ReadTextAsync(
                fixture.Database,
                "SELECT COUNT(*)::text FROM linkedin_copy_media"));
    }

    // ==============================================================================================
    // LINKEDIN-COPY-PACKAGE-002 Success: an approved copy exports copy plus manifest.
    // ==============================================================================================

    [Fact]
    public async Task Linkedin_002_Success_approved_copy_exports_copy_and_manifest()
    {
        await using var fixture = await ArrangeAsync("li_002_export");

        var adapted = await AdaptAsync(fixture, CallToAction, ["#buildinpublic"]);
        await ApproveAsync(fixture.Owner, adapted, 1, fixture.BundleRevision);

        using var export = await fixture.Owner.GetAsync($"/api/assets/{adapted:D}/export?format=linkedin");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);

        var fileName = export.Content.Headers.ContentDisposition?.FileNameStar
            ?? export.Content.Headers.ContentDisposition?.FileName;
        Assert.Contains("linkedin.zip", fileName, StringComparison.Ordinal);

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, adapted, 1);
        Assert.NotNull(stored);

        using var archive = new ZipArchive(await export.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);

        // copy.txt is exactly the approved bytes, not a re-render.
        var copyBytes = await ReadEntryAsync(archive, "copy.txt");
        Assert.Equal(Encoding.UTF8.GetBytes(stored!.BodyMarkdown), copyBytes);

        // The manifest names the picture, the owner's words and the verifiable digest.
        var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));
        Assert.Equal(adapted.ToString("D"), manifest.RootElement.GetProperty("assetId").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("revision").GetInt64());

        var image = Assert.Single(manifest.RootElement.GetProperty("images").EnumerateArray());
        Assert.Equal(fixture.MediaId!.Value.ToString("D"), image.GetProperty("mediaId").GetString());
        Assert.Equal(PictureAltText, image.GetProperty("altText").GetString());
        Assert.Equal("image/png", image.GetProperty("mediaType").GetString());

        var expectedPng = TestPng.Create(64, 40);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expectedPng)).ToLowerInvariant(),
            image.GetProperty("sha256").GetString());

        // And the served picture is those exact bytes under the manifest's own path.
        var servedPng = await ReadEntryAsync(archive, image.GetProperty("file").GetString()!);
        Assert.Equal(expectedPng, servedPng);
    }

    // ==============================================================================================
    // LINKEDIN-COPY-PACKAGE-002 Failure: never approved exports nothing; an edit ends the approval.
    // ==============================================================================================

    [Fact]
    public async Task Linkedin_002_Failure_unapproved_and_edited_copies_export_nothing()
    {
        await using var fixture = await ArrangeAsync("li_002_stale");

        var adapted = await AdaptAsync(fixture, null, []);

        // Never approved is approval_required: no decision stands behind the text.
        using var bare = await fixture.Owner.GetAsync($"/api/assets/{adapted:D}/export?format=linkedin");
        Assert.Equal(HttpStatusCode.Conflict, bare.StatusCode);
        Assert.Equal(
            "approval_required",
            JsonDocument.Parse(await bare.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        await ApproveAsync(fixture.Owner, adapted, 1, fixture.BundleRevision);

        using var review = await fixture.Owner.GetAsync($"/api/assets/{adapted:D}/review");
        Assert.Equal(
            "approved",
            JsonDocument.Parse(await review.Content.ReadAsStringAsync()).RootElement.GetProperty("reviewStatus").GetString());

        // An edit appends a head the approval no longer names: the export is stale and the
        // review says invalidated, under the same bindings the Markdown export re-checks.
        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, adapted, 1);
        Assert.NotNull(stored);

        using var edited = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/assets/{adapted:D}",
            1,
            new
            {
                expectedRevision = 1,
                bodyMarkdown = stored!.BodyMarkdown + "\nOwner correction.\n",
                title = (string?)null,
            }));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        using var stale = await fixture.Owner.GetAsync($"/api/assets/{adapted:D}/export?format=linkedin");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "approval_stale",
            JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        using var reread = await fixture.Owner.GetAsync($"/api/assets/{adapted:D}/review");
        Assert.Equal(
            "invalidated",
            JsonDocument.Parse(await reread.Content.ReadAsStringAsync()).RootElement.GetProperty("reviewStatus").GetString());
    }

    // ==============================================================================================
    // LINKEDIN-COPY-PACKAGE-002 Boundary: native publish is refused with no account action.
    // ==============================================================================================

    [Fact]
    public async Task Linkedin_002_Boundary_native_publish_is_refused_without_account_action()
    {
        await using var fixture = await ArrangeAsync("li_002_publish");

        var adapted = await AdaptAsync(fixture, CallToAction, []);
        await ApproveAsync(fixture.Owner, adapted, 1, fixture.BundleRevision);

        var operationsBefore = await ChronicleafPostgresFixture.ReadTextAsync(
            fixture.Database,
            "SELECT COUNT(*)::text FROM operations");
        var auditsBefore = await NonSessionAuditCountAsync(fixture.Database);
        var linkedInBefore = await ChronicleafPostgresFixture.ReadTextAsync(
            fixture.Database,
            "SELECT COUNT(*)::text FROM audit_events WHERE action LIKE 'linkedin-copy%'");

        // Even approved, even reviewed, publication is refused: there is no transport behind it.
        using var refused = await fixture.Owner.SendAsync(
            await fixture.Owner.PostWithoutBody($"/api/assets/{adapted:D}/adaptations/linkedin/publish"));

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
        Assert.Equal(
            linkedInBefore,
            await ChronicleafPostgresFixture.ReadTextAsync(
                fixture.Database,
                "SELECT COUNT(*)::text FROM audit_events WHERE action LIKE 'linkedin-copy%'"));

        using var review = await fixture.Owner.GetAsync($"/api/assets/{adapted:D}/review");
        Assert.Equal(
            "approved",
            JsonDocument.Parse(await review.Content.ReadAsStringAsync()).RootElement.GetProperty("reviewStatus").GetString());

        // A foreign id is still the delivered 404 rather than a transport refusal.
        using var foreign = await fixture.Owner.SendAsync(
            await fixture.Owner.PostWithoutBody($"/api/assets/{Guid.NewGuid():D}/adaptations/linkedin/publish"));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    // ==============================================================================================
    // CONTENT-PACK-PANELS-004: the milestone asset list serves derived pack kinds for pickers.
    // ==============================================================================================

    [Fact]
    public async Task Linkedin_Panels_derived_copy_is_listed_for_select()
    {
        await using var fixture = await ArrangeAsync("li_panels_list");

        using var before = await fixture.Owner.GetAsync($"/api/milestones/{fixture.MilestoneId:D}/assets");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Empty(JsonDocument.Parse(await before.Content.ReadAsStringAsync())
            .RootElement.GetProperty("variants").EnumerateArray());

        var assetId = await AdaptAsync(fixture, CallToAction, []);

        // The default read is exactly what older callers rely on: the derived
        // copy is not in it.
        using var plain = await fixture.Owner.GetAsync($"/api/milestones/{fixture.MilestoneId:D}/assets");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Empty(JsonDocument.Parse(await plain.Content.ReadAsStringAsync())
            .RootElement.GetProperty("variants").EnumerateArray());

        // Asked for packs, the same delivered route names the derived copy
        // with its type, revision and digests.
        using var listed = await fixture.Owner.GetAsync($"/api/milestones/{fixture.MilestoneId:D}/assets?kinds=packs");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var entries = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("variants").EnumerateArray().ToList();
        var entry = Assert.Single(entries);
        Assert.Equal(assetId, entry.GetProperty("assetId").GetGuid());
        Assert.Equal("linkedin-copy", entry.GetProperty("type").GetString());
        Assert.Equal(1, entry.GetProperty("currentRevision").GetInt64());

        // Any other kinds value is refused by name rather than silently defaulted.
        using var bad = await fixture.Owner.GetAsync($"/api/milestones/{fixture.MilestoneId:D}/assets?kinds=stories");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("validation_failed", JsonDocument.Parse(await bad.Content.ReadAsStringAsync())
            .RootElement.GetProperty("code").GetString());
    }

    // ==============================================================================================
    // Length rules: the internal ceiling refuses, the threshold warns, both deterministically.
    // ==============================================================================================

    [Fact]
    public async Task Linkedin_Length_long_source_warns_but_still_stores()
    {
        await using var fixture = await ArrangeAsync("li_len_warn", changeText: new string('w', 4500));

        using var adapted = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
            ValidAdapt(fixture, fixture.MediaId)));

        Assert.True(
            adapted.StatusCode == HttpStatusCode.Created,
            $"adapt answered {(int)adapted.StatusCode}: {await adapted.Content.ReadAsStringAsync()}");

        var answer = JsonDocument.Parse(await adapted.Content.ReadAsStringAsync());
        Assert.True(answer.RootElement.GetProperty("lengthWarning").GetBoolean());
        Assert.True(answer.RootElement.GetProperty("charCount").GetInt32() >= 9000);
    }

    [Fact]
    public async Task Linkedin_Length_over_ceiling_is_refused_before_anything_is_stored()
    {
        await using var fixture = await ArrangeAsync("li_len_long", changeText: new string('w', 6000));

        using var refused = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
            ValidAdapt(fixture, fixture.MediaId)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(
            "linkedin_copy_too_long",
            JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        Assert.Null(await ReadLinkedInAssetAsync(fixture.Database, fixture.MilestoneId));
    }

    private static async Task<string?> NonSessionAuditCountAsync(string connectionString) =>
        await ChronicleafPostgresFixture.ReadTextAsync(
            connectionString,
            "SELECT COUNT(*)::text FROM audit_events WHERE action NOT LIKE 'session.%'");

    private static async Task<Guid?> ReadLinkedInAssetAsync(string connectionString, Guid milestoneId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM content_assets
            WHERE milestone_id = @milestone AND type = 'linkedin-copy'
            """;
        command.Parameters.AddWithValue("milestone", milestoneId);

        var value = await command.ExecuteScalarAsync();
        return value is Guid id ? id : null;
    }

    // ==============================================================================================
    // Migration: the schema carries this package's table with the enforced bounds.
    // ==============================================================================================

    [Fact]
    public async Task Linkedin_Migration_schema_carries_the_selection_table()
    {
        Assert.Contains(ChronicleafSchema.LinkedInCopyPackageMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));

        var database = await postgres.CreateDatabaseAsync("li_migration");

        Assert.Equal(
            "500",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT character_maximum_length::text FROM information_schema.columns " +
                "WHERE table_name = 'linkedin_copy_media' AND column_name = 'alt_text'"));

        Assert.Equal(
            "PK_linkedin_copy_media",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT constraint_name FROM information_schema.table_constraints " +
                "WHERE table_name = 'linkedin_copy_media' AND constraint_type = 'PRIMARY KEY'"));
    }

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
        bool approveStory = true,
        string presetId = "linkedin",
        object? overrides = null,
        string? changeText = null)
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
                new { presetId, overrides = overrides ?? new { } },
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
                    idempotencyKey = $"li-capture-{purpose}",
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
        var summary = SummaryJson(head.ManualItemId, changeText);
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
                idempotencyKey = $"li-{purpose}",
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
            await ApproveStoryAsync(fixture);
        }

        return fixture;
    }

    private static async Task ApproveStoryAsync(Fixture fixture) =>
        await ApproveAsync(fixture.Owner, fixture.StoryAssetId, 1, fixture.BundleRevision);

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

    private static async Task<Guid> AdaptAsync(Fixture fixture, string? callToAction, string[] hashtags)
    {
        using var adapted = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/linkedin",
            ValidAdapt(fixture, fixture.MediaId, callToAction, hashtags)));

        Assert.True(
            adapted.StatusCode == HttpStatusCode.Created,
            $"adapt answered {(int)adapted.StatusCode}: {await adapted.Content.ReadAsStringAsync()}");

        return JsonDocument.Parse(await adapted.Content.ReadAsStringAsync())
            .RootElement.GetProperty("assetId").GetGuid();
    }

    private static object ValidAdapt(
        Fixture fixture,
        Guid? mediaId,
        string? callToAction = CallToAction,
        string[]? hashtags = null,
        long? evidenceRevision = null,
        object? pictures = null)
    {
        object[] resolvedPictures = pictures switch
        {
            null when mediaId is { } media => [new { mediaId = media.ToString("D"), altText = PictureAltText }],
            null => [],
            object[] list => list,
            _ => [],
        };

        return new
        {
            evidenceRevision = evidenceRevision ?? fixture.BundleRevision,
            voiceRevision = fixture.VoiceRevision,
            callToAction,
            hashtags = hashtags ?? ["#buildinpublic"],
            pictures = resolvedPictures,
        };
    }

    private static async Task AssertCodeAsync(
        OwnerClient owner,
        HttpRequestMessage request,
        HttpStatusCode status,
        string code)
    {
        _ = owner;
        using var response = await owner.SendAsync(request);
        Assert.True(
            response.StatusCode == status,
            $"answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(
            code,
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    private static async Task<IReadOnlyList<LinkedInSelectionRow>> ReadSelectionAsync(
        string connectionString,
        Guid assetId,
        long revision)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT media_id, alt_text FROM linkedin_copy_media
            WHERE asset_id = @asset AND revision = @revision
            ORDER BY media_id
            """;
        command.Parameters.AddWithValue("asset", assetId);
        command.Parameters.AddWithValue("revision", revision);

        var rows = new List<LinkedInSelectionRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new LinkedInSelectionRow(reader.GetGuid(0), reader.GetString(1)));
        }

        return rows;
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
    private static string SummaryJson(Guid? evidenceId, string? changeText = null)
    {
        var id = evidenceId is { } value ? value.ToString("d") : null;
        var change = changeText ?? "The pipeline runs from a clean checkout.";

        return $$"""
          {
            "milestoneTitle": "Reproducible release pipeline",
            "summary": "The pipeline now reproduces from a clean checkout.",
            "changes": [{"text": {{Quote(change)}}, "evidenceIds": ["{{id}}"]}],
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

    private sealed record LinkedInSelectionRow(Guid MediaId, string AltText);

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
