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
/// XIAOHONGSHU-CONTENT-PACKAGE-001 and XIAOHONGSHU-CONTENT-PACKAGE-002 against a real disposable
/// PostgreSQL, the real <c>Chronicleaf.Api</c> host, the real operation queue and the real
/// evidence, voice, asset, review and media contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the voice
/// service, the asset store, the approvals table, the media store, the routes, the access
/// boundary and the database. The approved story every scenario adapts from is produced by a
/// real generation job through the same scripted provider the drafts suite drives - the only
/// substitution for prose, and the same declared boundary: no stage here may be read as evidence
/// that any provider works. Adapting itself calls no provider at all. The selected pictures are
/// real capture jobs' candidates, selected through the delivered curation route, behind the same
/// scripted-browser seam the capture suite declares: the PNG bytes are a labelled synthesised
/// fixture, and no scenario here claims a real page was photographed. The capture host is
/// disposed before the drafts host starts, on the same terms as the HTML export suite, so the
/// two hosts' workers never share this database.
/// </para>
/// <para>
/// <b>What is never touched.</b> No Xiaohongshu endpoint, no account, no login, no cookie and no
/// token exists anywhere in this suite, because none exists in the package: the publish route is
/// asserted as a refusal with no outbound request possible, and the suite proves the refusal
/// changed nothing rather than proving anything about a network it never uses.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> A pack is read out of the raw <c>content_assets</c>,
/// <c>content_revisions</c> and <c>xiaohongshu_pack_media</c> tables, not only from the adapt
/// route, and an approval is read out of the raw <c>content_approvals</c> table. The claims here
/// are about what is <em>stored</em> and what is <em>served</em>, and a value read back through
/// the same serializer that wrote it would agree with itself whether or not the bytes on disk
/// were right.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class XiaohongshuContentPackageTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("b8c4d2e0-5c39-4e7f-a08b-9d6f2c8e4b51");

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The evidence every pack scenario adapts from. Free of digits on purpose, for the same
    /// reason the drafts suite's own note is: the numeral rule refuses a figure that appears in
    /// no evidence item, and a note full of numbers would let claims pass for the wrong reason.
    /// </summary>
    private const string EvidenceNote =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string KnownLimitation = "Only Linux is covered.";

    private const string PictureCaption = "The pipeline digest view.";

    private const string PictureAltText = "A terminal showing two matching digest lines.";

    private const string CoverCopy = "A note on the pipeline digest view.";

    private const string CallToAction = "If this helped your team, share how you verify releases.";

    /// <summary>
    /// The title the owner chooses. It is the milestone title the fixture records, which is also
    /// the renderer's first derived candidate under the fixture voice, so it is chosen rather
    /// than invented.
    /// </summary>
    private const string PackTitle = "Reproducible release pipeline";

    // ==============================================================================================
    // XIAOHONGSHU-CONTENT-PACKAGE-001 Success: adapt stores a grounded pack with snapshot and order.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_001_Success_adapt_stores_grounded_pack_with_snapshot_and_selection()
    {
        await using var fixture = await ArrangeAsync("xh_001_success");

        using var adapted = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
            new
            {
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
                title = PackTitle,
                tags = new[] { "#buildinpublic", "#releases" },
                coverCopy = CoverCopy,
                callToAction = CallToAction,
                pictures = new[] { new { mediaId = fixture.MediaIds[0].ToString("D"), altText = PictureAltText } },
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
        Assert.Equal(PackTitle, answer.RootElement.GetProperty("selectedTitle").GetString());
        Assert.Equal(
            fixture.MediaIds[0].ToString("D"),
            Assert.Single(answer.RootElement.GetProperty("imageOrder").EnumerateArray()).GetString());

        // The preview offers one to five derived titles, and the choice is one of them: the
        // pack never carries a title from outside its own candidates.
        var candidates = answer.RootElement.GetProperty("titleCandidates").EnumerateArray()
            .Select(candidate => candidate.GetString()).ToList();
        Assert.InRange(candidates.Count, 1, 5);
        Assert.Contains(PackTitle, candidates);

        // The stored bytes, read out of the rows rather than the route.
        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 1);
        Assert.NotNull(stored);
        Assert.Equal(answer.RootElement.GetProperty("bodyHash").GetString(), stored!.BodyHash);
        Assert.Equal(ContentRevision.Hash(stored.BodyMarkdown), stored.BodyHash);
        Assert.Equal(stored.BodyMarkdown.Length, answer.RootElement.GetProperty("charCount").GetInt32());

        // Supported facts with the citations the claim map assigned, the chosen title first, the
        // owner's own cover, closing line and tags, and every footnote defined: a citation is
        // never a reference to nothing.
        Assert.True(stored.BodyMarkdown.StartsWith(PackTitle, StringComparison.Ordinal), "The pack opens on the chosen title.");
        Assert.Contains("The pipeline now reproduces from a clean checkout.", stored.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("The pipeline runs from a clean checkout.", stored.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains(KnownLimitation, stored.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains(CoverCopy, stored.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains(CallToAction, stored.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("#buildinpublic #releases", stored.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("[^", stored.BodyMarkdown, StringComparison.Ordinal);

        // The asset reads as a Xiaohongshu pack draft, and the review package reports exactly
        // that: a draft that was never approved, with no approval row anywhere behind it.
        using var read = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}");
        var view = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("draft", view.RootElement.GetProperty("status").GetString());
        Assert.Equal("xiaohongshu-pack", view.RootElement.GetProperty("type").GetString());

        using var review = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}/review");
        var state = JsonDocument.Parse(await review.Content.ReadAsStringAsync());
        Assert.Equal("draft", state.RootElement.GetProperty("reviewStatus").GetString());

        Assert.Empty(await ReviewRows.ListAsync(fixture.Database, assetId));

        // And the source approval is exactly where it was: adapting published nothing about it.
        var sourceApprovals = await ReviewRows.ListAsync(fixture.Database, fixture.StoryAssetId);
        Assert.Single(sourceApprovals);
        Assert.Null(sourceApprovals[0].InvalidatedAt);

        // The selection rows name the picture, the owner's accessibility words and the image
        // order position.
        var selection = await ReadSelectionAsync(fixture.Database, assetId, 1);
        Assert.Equal(fixture.MediaIds[0], Assert.Single(selection).MediaId);
        Assert.Equal(PictureAltText, Assert.Single(selection).AltText);
        Assert.Equal(0, Assert.Single(selection).Position);

        // The pack carries the voice it was adapted under as a value: a later profile edit
        // cannot reach the stored bytes.
        var snapshotBefore = stored.VoiceSnapshot;
        Assert.Contains("xiaohongshu", snapshotBefore, StringComparison.Ordinal);

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
    // XIAOHONGSHU-CONTENT-PACKAGE-001 Failure: an explicit project restriction wins over the preset.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_001_Failure_project_voice_restriction_wins_over_the_preset()
    {
        // The xiaohongshu preset asks for emoji and medium marketing; the project explicitly
        // refuses both. The pack must carry the restriction, not the preset.
        await using var fixture = await ArrangeAsync(
            "xh_001_voice",
            presetId: "xiaohongshu",
            overrides: new { emoji = false, marketing = "low", language = "en" });

        using var adapted = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
            new
            {
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
                title = PackTitle,
                tags = Array.Empty<string>(),
                coverCopy = (string?)null,
                callToAction = (string?)null,
                pictures = Array.Empty<object>(),
            }));

        Assert.True(
            adapted.StatusCode == HttpStatusCode.Created,
            $"adapt answered {(int)adapted.StatusCode}: {await adapted.Content.ReadAsStringAsync()}");

        var assetId = JsonDocument.Parse(await adapted.Content.ReadAsStringAsync())
            .RootElement.GetProperty("assetId").GetGuid();

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 1);
        Assert.NotNull(stored);

        // Structurally emoji-free and hype-free: the template never emits what the restriction
        // forbids, so the preset's allowance cannot reach the text.
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
    // XIAOHONGSHU-CONTENT-PACKAGE-001 Boundary: no call to action stays informational.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_001_Boundary_pack_without_call_to_action_stays_informational()
    {
        await using var fixture = await ArrangeAsync("xh_001_plain", pictureCount: 0);

        using var adapted = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
            new
            {
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
                title = PackTitle,
                tags = Array.Empty<string>(),
                coverCopy = (string?)null,
                callToAction = (string?)null,
                pictures = Array.Empty<object>(),
            }));

        Assert.True(
            adapted.StatusCode == HttpStatusCode.Created,
            $"adapt answered {(int)adapted.StatusCode}: {await adapted.Content.ReadAsStringAsync()}");

        var answer = JsonDocument.Parse(await adapted.Content.ReadAsStringAsync());
        Assert.False(answer.RootElement.GetProperty("hasCallToAction").GetBoolean());
        Assert.Empty(answer.RootElement.GetProperty("imageOrder").EnumerateArray());

        var assetId = answer.RootElement.GetProperty("assetId").GetGuid();
        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 1);
        Assert.NotNull(stored);

        // Informational: the facts and their citations, and no invented engagement promise on
        // the owner's behalf - no followers, no stars, no virality.
        Assert.Contains("The pipeline now reproduces from a clean checkout.", stored!.BodyMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("followers", stored.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("viral", stored.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stars", stored.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subscribe", stored.BodyMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("comment below", stored.BodyMarkdown, StringComparison.OrdinalIgnoreCase);

        await ApproveAsync(fixture.Owner, assetId, 1, fixture.BundleRevision);

        // A text-only pack exports as copy plus package metadata plus an empty manifest: no
        // pictures invented to fill it.
        using var export = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}/export?format=xiaohongshu");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        using var archive = new ZipArchive(await export.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));
        Assert.Equal(0, manifest.RootElement.GetProperty("images").GetArrayLength());
        Assert.Equal(0, manifest.RootElement.GetProperty("imageOrder").GetArrayLength());
        Assert.Equal("copy.txt", manifest.RootElement.GetProperty("copyFile").GetString());
        Assert.Equal("package.json", manifest.RootElement.GetProperty("packageFile").GetString());
    }

    // ==============================================================================================
    // XIAOHONGSHU-CONTENT-PACKAGE-001 title choice: the preview's candidates, a required choice.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_001_Title_choice_comes_from_derived_candidates()
    {
        await using var fixture = await ArrangeAsync("xh_001_titles", pictureCount: 0);

        // A missing title is refused by name rather than defaulted to the first candidate: the
        // choice is the owner's, and a default would put words in the pack they did not pick.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(fixture, title: null)),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // A title from outside the candidates is refused the same way, even when it is
        // well-formed prose: the pack carries a choice, not a free inscription.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(fixture, title: "A title nobody derived.")),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // Choosing the second candidate stores a pack that opens on exactly that line.
        using var first = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
            ValidAdapt(fixture, title: PackTitle)));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var candidates = JsonDocument.Parse(await first.Content.ReadAsStringAsync())
            .RootElement.GetProperty("titleCandidates").EnumerateArray()
            .Select(candidate => candidate.GetString()).ToList();
        Assert.True(candidates.Count >= 2, "The preview offers a choice, not a single title.");

        var secondTitle = candidates[1]!;
        Assert.NotEqual(PackTitle, secondTitle);

        using var second = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
            ValidAdapt(fixture, title: secondTitle)));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var answer = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var assetId = answer.RootElement.GetProperty("assetId").GetGuid();
        Assert.Equal(2, answer.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(secondTitle, answer.RootElement.GetProperty("selectedTitle").GetString());

        // A new title is a new revision: the independent hash covers the choice, so changing
        // it after approval ends the approval rather than editing it in place.
        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 2);
        Assert.NotNull(stored);
        Assert.True(stored!.BodyMarkdown.StartsWith(secondTitle, StringComparison.Ordinal), "The second revision opens on the second choice.");
        Assert.NotEqual(
            (await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 1))!.BodyHash,
            stored.BodyHash);
    }

    // ==============================================================================================
    // XIAOHONGSHU-CONTENT-PACKAGE-001 refusals: every invalid adaptation stores nothing.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_001_Refusals_invalid_adaptations_store_nothing()
    {
        await using var fixture = await ArrangeAsync("xh_001_refusals", pictureCount: 1, approveStory: false);

        // An unapproved source is approval_required: no decision stands behind the text.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(fixture)),
            HttpStatusCode.Conflict,
            "approval_required");

        // An unknown source is the delivered 404, with no scope crossed to answer it.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{Guid.NewGuid():D}/adaptations/xiaohongshu",
                ValidAdapt(fixture)),
            HttpStatusCode.NotFound,
            "not_found");

        // A body that names nothing is refused by name rather than read as revision zero.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                new { }),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        await ApproveStoryAsync(fixture);

        // A moved evidence head is a conflict with the record's own state, not a corrected body.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(fixture, evidenceRevision: fixture.BundleRevision - 1)),
            HttpStatusCode.Conflict,
            "revision_conflict");

        // A moved voice head is the same conflict on the other axis.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(fixture, voiceRevision: fixture.VoiceRevision - 1)),
            HttpStatusCode.Conflict,
            "revision_conflict");

        // A tags list over the bound is refused before any tag is read.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(
                    fixture,
                    tags: new[] { "#one", "#two", "#three", "#four", "#five", "#six", "#seven", "#eight", "#nine", "#ten", "#eleven" })),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // A tag that is not a tag is refused by name: the rule is deterministic, not one the
        // owner's client has to guess.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(fixture, tags: new[] { "#no spaces" })),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // A call to action over its bound is refused before it is read.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(fixture, callToAction: new string('w', 281))),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // A cover line over its bound is refused before it is read.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(fixture, coverCopy: new string('w', 201))),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // A title over its bound is refused before the candidates are consulted.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(fixture, title: new string('w', 81))),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // A selected picture without accessibility words is refused: a manifest entry with no
        // words would be a picture this product agreed to publish inaccessibly.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(
                    fixture,
                    pictures: new[] { new { mediaId = fixture.MediaIds[0].ToString("D"), altText = "  " } })),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // Twenty-one pictures are one over the bound, and the bound fires before existence is
        // consulted: the ids below name nothing, and the answer is still about the count.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(
                    fixture,
                    pictures: Enumerable.Range(0, 21).Select(index => new
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
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(
                    fixture,
                    pictures: new[] { new { mediaId = Guid.NewGuid().ToString("D"), altText = PictureAltText } })),
            HttpStatusCode.Conflict,
            "media_unavailable");

        // A figure the evidence never stated is refused even in the owner's own words: the pack
        // is grounded text, and the call to action is inside the checked bytes.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
                ValidAdapt(fixture, callToAction: "Version 2 ships this week.")),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        // Nothing above stored anything: no Xiaohongshu pack row exists for this milestone.
        Assert.Null(await ReadPackAssetAsync(fixture.Database, fixture.MilestoneId));
        Assert.Equal(
            "0",
            await ChronicleafPostgresFixture.ReadTextAsync(
                fixture.Database,
                "SELECT COUNT(*)::text FROM xiaohongshu_pack_media"));
    }

    // ==============================================================================================
    // XIAOHONGSHU-CONTENT-PACKAGE-002 Success: an approved pack exports copy, package and manifest.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_002_Success_approved_pack_exports_copy_package_and_manifest()
    {
        await using var fixture = await ArrangeAsync("xh_002_export");

        var adapted = await AdaptAsync(fixture, CallToAction, ["#buildinpublic"]);
        await ApproveAsync(fixture.Owner, adapted, 1, fixture.BundleRevision);

        using var export = await fixture.Owner.GetAsync($"/api/assets/{adapted:D}/export?format=xiaohongshu");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);

        var fileName = export.Content.Headers.ContentDisposition?.FileNameStar
            ?? export.Content.Headers.ContentDisposition?.FileName;
        Assert.Contains("xiaohongshu.zip", fileName, StringComparison.Ordinal);

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, adapted, 1);
        Assert.NotNull(stored);

        using var archive = new ZipArchive(await export.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);

        // copy.txt is exactly the approved bytes, not a re-render.
        var copyBytes = await ReadEntryAsync(archive, "copy.txt");
        Assert.Equal(Encoding.UTF8.GetBytes(stored!.BodyMarkdown), copyBytes);

        // package.json carries the pack's own metadata: what it is, which revision, and the
        // digest that binds it - never a remote URL and never a posted claim.
        var package = JsonDocument.Parse(await ReadEntryAsync(archive, "package.json"));
        Assert.Equal("xiaohongshu-pack", package.RootElement.GetProperty("pack").GetString());
        Assert.Equal(adapted.ToString("D"), package.RootElement.GetProperty("assetId").GetString());
        Assert.Equal(1, package.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(stored.BodyHash, package.RootElement.GetProperty("bodyHash").GetString());
        Assert.Equal(PackTitle, package.RootElement.GetProperty("title").GetString());

        // The manifest names the picture, the owner's words and the verifiable digest, in the
        // owner's image order.
        var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));
        Assert.Equal(adapted.ToString("D"), manifest.RootElement.GetProperty("assetId").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("revision").GetInt64());

        Assert.Equal(
            fixture.MediaIds[0].ToString("D"),
            Assert.Single(manifest.RootElement.GetProperty("imageOrder").EnumerateArray()).GetString());

        var image = Assert.Single(manifest.RootElement.GetProperty("images").EnumerateArray());
        Assert.Equal(fixture.MediaIds[0].ToString("D"), image.GetProperty("mediaId").GetString());
        Assert.Equal(PictureAltText, image.GetProperty("altText").GetString());
        Assert.Equal("image/png", image.GetProperty("mediaType").GetString());

        var expectedPng = TestPng.Create(64, 40);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expectedPng)).ToLowerInvariant(),
            image.GetProperty("sha256").GetString());

        // And the served picture is those exact bytes under the manifest's own path.
        var servedPng = await ReadEntryAsync(archive, image.GetProperty("file").GetString()!);
        Assert.Equal(expectedPng, servedPng);

        // The same approved revision packs identically twice: nothing in the archive records
        // when the export ran.
        using var again = await fixture.Owner.GetAsync($"/api/assets/{adapted:D}/export?format=xiaohongshu");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(
            await export.Content.ReadAsByteArrayAsync(),
            await again.Content.ReadAsByteArrayAsync());
    }

    // ==============================================================================================
    // XIAOHONGSHU-CONTENT-PACKAGE-002 order: the export serves the owner's image arrangement.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_002_Image_order_is_the_owner_arrangement()
    {
        await using var fixture = await ArrangeAsync("xh_002_order", pictureCount: 2);

        // Selected in reverse capture order: the arrangement is the request's, not the store's.
        var first = fixture.MediaIds[1];
        var second = fixture.MediaIds[0];

        using var adapted = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
            new
            {
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
                title = PackTitle,
                tags = Array.Empty<string>(),
                coverCopy = (string?)null,
                callToAction = (string?)null,
                pictures = new[]
                {
                    new { mediaId = first.ToString("D"), altText = "First picture." },
                    new { mediaId = second.ToString("D"), altText = "Second picture." },
                },
            }));

        Assert.Equal(HttpStatusCode.Created, adapted.StatusCode);

        var answer = JsonDocument.Parse(await adapted.Content.ReadAsStringAsync());
        var assetId = answer.RootElement.GetProperty("assetId").GetGuid();

        var ordered = answer.RootElement.GetProperty("imageOrder").EnumerateArray()
            .Select(element => element.GetString()).ToList();
        Assert.Equal(2, ordered.Count);
        Assert.Equal(first.ToString("D"), ordered[0]);
        Assert.Equal(second.ToString("D"), ordered[1]);

        // The stored rows carry the positions the request named.
        var rows = await ReadSelectionAsync(fixture.Database, assetId, 1);
        Assert.Equal(2, rows.Count);
        Assert.Equal(first, rows[0].MediaId);
        Assert.Equal(0, rows[0].Position);
        Assert.Equal(second, rows[1].MediaId);
        Assert.Equal(1, rows[1].Position);

        await ApproveAsync(fixture.Owner, assetId, 1, fixture.BundleRevision);

        using var export = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}/export?format=xiaohongshu");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        using var archive = new ZipArchive(await export.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));

        // The manifest's image order and the archive's entry order both follow the arrangement
        // the approval covers.
        var manifestOrder = manifest.RootElement.GetProperty("imageOrder").EnumerateArray()
            .Select(element => element.GetString()).ToList();
        Assert.Equal(2, manifestOrder.Count);
        Assert.Equal(first.ToString("D"), manifestOrder[0]);
        Assert.Equal(second.ToString("D"), manifestOrder[1]);

        var entryNames = archive.Entries.Select(entry => entry.FullName).ToList();
        Assert.Equal(5, entryNames.Count);
        Assert.Equal("copy.txt", entryNames[0]);
        Assert.Equal("package.json", entryNames[1]);
        Assert.Equal($"images/{first:N}.png", entryNames[2]);
        Assert.Equal($"images/{second:N}.png", entryNames[3]);
        Assert.Equal("manifest.json", entryNames[4]);
    }

    // ==============================================================================================
    // XIAOHONGSHU-CONTENT-PACKAGE-002 Failure: never approved exports nothing; an edit ends approval.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_002_Failure_unapproved_and_edited_packs_export_nothing()
    {
        await using var fixture = await ArrangeAsync("xh_002_stale", pictureCount: 0);

        var adapted = await AdaptAsync(fixture, null, []);

        // Never approved is approval_required: no decision stands behind the text.
        using var bare = await fixture.Owner.GetAsync($"/api/assets/{adapted:D}/export?format=xiaohongshu");
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

        using var stale = await fixture.Owner.GetAsync($"/api/assets/{adapted:D}/export?format=xiaohongshu");
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
    // XIAOHONGSHU-CONTENT-PACKAGE-002 Boundary: native publish is refused with no account action.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_002_Boundary_native_publish_is_refused_without_account_action()
    {
        await using var fixture = await ArrangeAsync("xh_002_publish");

        var adapted = await AdaptAsync(fixture, CallToAction, []);
        await ApproveAsync(fixture.Owner, adapted, 1, fixture.BundleRevision);

        var operationsBefore = await ChronicleafPostgresFixture.ReadTextAsync(
            fixture.Database,
            "SELECT COUNT(*)::text FROM operations");
        var auditsBefore = await NonSessionAuditCountAsync(fixture.Database);
        var packAuditsBefore = await ChronicleafPostgresFixture.ReadTextAsync(
            fixture.Database,
            "SELECT COUNT(*)::text FROM audit_events WHERE action LIKE 'xiaohongshu-pack%'");

        // Even approved, even reviewed, publication is refused: there is no account, no login
        // and no transport behind it.
        using var refused = await fixture.Owner.SendAsync(
            await fixture.Owner.PostWithoutBody($"/api/assets/{adapted:D}/adaptations/xiaohongshu/publish"));

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
            packAuditsBefore,
            await ChronicleafPostgresFixture.ReadTextAsync(
                fixture.Database,
                "SELECT COUNT(*)::text FROM audit_events WHERE action LIKE 'xiaohongshu-pack%'"));

        using var review = await fixture.Owner.GetAsync($"/api/assets/{adapted:D}/review");
        Assert.Equal(
            "approved",
            JsonDocument.Parse(await review.Content.ReadAsStringAsync()).RootElement.GetProperty("reviewStatus").GetString());

        // A foreign id is still the delivered 404 rather than a transport refusal.
        using var foreign = await fixture.Owner.SendAsync(
            await fixture.Owner.PostWithoutBody($"/api/assets/{Guid.NewGuid():D}/adaptations/xiaohongshu/publish"));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    // ==============================================================================================
    // CONTENT-PACK-PANELS-004: the milestone asset list serves derived pack kinds for pickers.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_Panels_derived_pack_is_listed_for_select()
    {
        await using var fixture = await ArrangeAsync("xh_panels_list");

        using var before = await fixture.Owner.GetAsync($"/api/milestones/{fixture.MilestoneId:D}/assets");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Empty(JsonDocument.Parse(await before.Content.ReadAsStringAsync())
            .RootElement.GetProperty("variants").EnumerateArray());

        var assetId = await AdaptAsync(fixture, CallToAction, ["#buildinpublic"]);

        // The default read is exactly what older callers rely on: the derived
        // pack is not in it.
        using var plain = await fixture.Owner.GetAsync($"/api/milestones/{fixture.MilestoneId:D}/assets");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Empty(JsonDocument.Parse(await plain.Content.ReadAsStringAsync())
            .RootElement.GetProperty("variants").EnumerateArray());

        // Asked for packs, the same delivered route names the derived pack
        // with its type, revision and digests.
        using var listed = await fixture.Owner.GetAsync($"/api/milestones/{fixture.MilestoneId:D}/assets?kinds=packs");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var entries = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("variants").EnumerateArray().ToList();
        var entry = Assert.Single(entries);
        Assert.Equal(assetId, entry.GetProperty("assetId").GetGuid());
        Assert.Equal("xiaohongshu-pack", entry.GetProperty("type").GetString());
        Assert.Equal(1, entry.GetProperty("currentRevision").GetInt64());
    }

    // ==============================================================================================
    // Length rules: the internal ceiling refuses, the threshold warns, both deterministically.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_Length_long_source_warns_but_still_stores()
    {
        await using var fixture = await ArrangeAsync("xh_len_warn", pictureCount: 0, changeText: new string('w', 4500));

        using var adapted = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
            ValidAdapt(fixture)));

        Assert.True(
            adapted.StatusCode == HttpStatusCode.Created,
            $"adapt answered {(int)adapted.StatusCode}: {await adapted.Content.ReadAsStringAsync()}");

        var answer = JsonDocument.Parse(await adapted.Content.ReadAsStringAsync());
        Assert.True(answer.RootElement.GetProperty("lengthWarning").GetBoolean());
        Assert.True(answer.RootElement.GetProperty("charCount").GetInt32() >= 9000);
    }

    [Fact]
    public async Task Xiaohongshu_Length_over_ceiling_is_refused_before_anything_is_stored()
    {
        await using var fixture = await ArrangeAsync("xh_len_long", pictureCount: 0, changeText: new string('w', 6000));

        using var refused = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
            ValidAdapt(fixture)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(
            "xiaohongshu_pack_too_long",
            JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        Assert.Null(await ReadPackAssetAsync(fixture.Database, fixture.MilestoneId));
    }

    private static async Task<string?> NonSessionAuditCountAsync(string connectionString) =>
        await ChronicleafPostgresFixture.ReadTextAsync(
            connectionString,
            "SELECT COUNT(*)::text FROM audit_events WHERE action NOT LIKE 'session.%'");

    private static async Task<Guid?> ReadPackAssetAsync(string connectionString, Guid milestoneId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM content_assets
            WHERE milestone_id = @milestone AND type = 'xiaohongshu-pack'
            """;
        command.Parameters.AddWithValue("milestone", milestoneId);

        var value = await command.ExecuteScalarAsync();
        return value is Guid id ? id : null;
    }

    // ==============================================================================================
    // Migration: the schema carries this package's table with the enforced bounds.
    // ==============================================================================================

    [Fact]
    public async Task Xiaohongshu_Migration_schema_carries_the_selection_table()
    {
        Assert.Contains(ChronicleafSchema.XiaohongshuContentPackageMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));

        var database = await postgres.CreateDatabaseAsync("xh_migration");

        Assert.Equal(
            "500",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT character_maximum_length::text FROM information_schema.columns " +
                "WHERE table_name = 'xiaohongshu_pack_media' AND column_name = 'alt_text'"));

        Assert.Equal(
            "PK_xiaohongshu_pack_media",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT constraint_name FROM information_schema.table_constraints " +
                "WHERE table_name = 'xiaohongshu_pack_media' AND constraint_type = 'PRIMARY KEY'"));

        Assert.Equal(
            "integer",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT data_type FROM information_schema.columns " +
                "WHERE table_name = 'xiaohongshu_pack_media' AND column_name = 'position'"));
    }

    // ==============================================================================================
    // The fixture.
    // ==============================================================================================

    /// <summary>
    /// Builds everything a scenario needs: a real database, a real project, a confirmed milestone
    /// with real recorded evidence, a real sealed credential, a real authorized preview, a stored
    /// draft produced by a real generation job, the approved story it belongs to, and - with
    /// pictures - real capture jobs' selected candidates already carried in the evidence head the
    /// story grounds in.
    /// </summary>
    private async Task<Fixture> ArrangeAsync(
        string purpose,
        int pictureCount = 1,
        bool approveStory = true,
        string presetId = "xiaohongshu",
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
            // xiaohongshu preset declares Chinese and would report language findings on every
            // field, which would refuse the approval this fixture needs.
            using var voiced = await setupOwner.SendAsync(await setupOwner.Put(
                $"/api/projects/{projectId:D}/voice-profile",
                new { presetId, overrides = overrides ?? new { language = "en" } },
                0));

            Assert.True(
                voiced.StatusCode == HttpStatusCode.OK,
                $"voice answered {(int)voiced.StatusCode}: {await voiced.Content.ReadAsStringAsync()}");
        }

        var mediaIds = new List<Guid>();
        HeadView head;

        if (pictureCount > 0)
        {
            await using var capture = CaptureApiHost.Start(
                database, clock, new RecordingScreenshotProvider(clock));
            using var pictureOwner = await OwnerClient.CreateAsync(capture);

            capture.Provider.AnswerWithPng(width: 64, height: 40);

            for (var picture = 0; picture < pictureCount; picture++)
            {
                using var requested = await pictureOwner.SendAsync(await pictureOwner.Post(
                    $"/api/milestones/{milestoneId:D}/captures",
                    new
                    {
                        url = $"https://example.com/pipeline-{purpose}-{picture}",
                        viewports = new[] { "desktop" },
                        waitMs = 0,
                        fullPage = false,
                        expectedBundleRevision = (long?)null,
                        idempotencyKey = $"xh-capture-{purpose}-{picture}",
                    },
                    0));

                Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
                var operationId = Guid.Parse(JsonDocument
                    .Parse(await requested.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("operationId").GetString()!);

                await capture.DispatchAsync();
                await OperationRows.AwaitAsync(database, operationId, "succeeded", TimeSpan.FromSeconds(5));

                using var candidates = await pictureOwner.GetAsync($"/api/milestones/{milestoneId:D}/screenshots");
                var items = JsonDocument.Parse(await candidates.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("items").EnumerateArray().ToList();

                // The capture this iteration requested, not one an earlier iteration already
                // selected: re-selecting the same candidate with the same words is a no-op
                // that appends no bundle revision.
                var candidate = items.First(item =>
                    !mediaIds.Contains(Guid.Parse(item.GetProperty("mediaId").GetString()!)));

                var screenshotId = Guid.Parse(candidate.GetProperty("id").GetString()!);
                var screenshotRevision = candidate.GetProperty("revision").GetInt64();
                var mediaId = Guid.Parse(candidate.GetProperty("mediaId").GetString()!);

                using var selected = await pictureOwner.SendAsync(await pictureOwner.Patch(
                    $"/api/screenshots/{screenshotId:D}",
                    screenshotRevision,
                    new
                    {
                        status = "selected",
                        caption = PictureCaption,
                        expectedRevision = screenshotRevision,
                        expectedBundleRevision = picture + 1,
                    }));

                Assert.True(
                    selected.StatusCode == HttpStatusCode.OK,
                    $"select answered {(int)selected.StatusCode}: {await selected.Content.ReadAsStringAsync()}");

                mediaIds.Add(mediaId);
            }

            // The selects appended bundle revisions past the manual fact: the summary cites the
            // head's ids rather than the first revision's.
            await using var reader = GroundedMarkdownDraftsApiHost.StartProduction(
                database, clock, keyFile.FilePath);
            using var readerOwner = await OwnerClient.CreateAsync(reader);
            head = await ReadHeadAsync(readerOwner, milestoneId);
            Assert.Equal(1 + pictureCount, head.Revision);
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
                idempotencyKey = $"xh-{purpose}",
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
            mediaIds);

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
            $"/api/assets/{fixture.StoryAssetId:D}/adaptations/xiaohongshu",
            ValidAdapt(fixture, callToAction: callToAction, tags: hashtags)));

        Assert.True(
            adapted.StatusCode == HttpStatusCode.Created,
            $"adapt answered {(int)adapted.StatusCode}: {await adapted.Content.ReadAsStringAsync()}");

        return JsonDocument.Parse(await adapted.Content.ReadAsStringAsync())
            .RootElement.GetProperty("assetId").GetGuid();
    }

    private static object ValidAdapt(
        Fixture fixture,
        string? title = PackTitle,
        string[]? tags = null,
        string? coverCopy = CoverCopy,
        string? callToAction = CallToAction,
        long? evidenceRevision = null,
        long? voiceRevision = null,
        object? pictures = null)
    {
        object[] resolvedPictures = pictures switch
        {
            null when fixture.MediaIds.Count == 1 => [new { mediaId = fixture.MediaIds[0].ToString("D"), altText = PictureAltText }],
            null => [],
            object[] list => list,
            _ => [],
        };

        return new
        {
            evidenceRevision = evidenceRevision ?? fixture.BundleRevision,
            voiceRevision = voiceRevision ?? fixture.VoiceRevision,
            title,
            tags = tags ?? ["#buildinpublic"],
            coverCopy,
            callToAction,
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

    private static async Task<IReadOnlyList<XiaohongshuSelectionRow>> ReadSelectionAsync(
        string connectionString,
        Guid assetId,
        long revision)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT media_id, alt_text, position FROM xiaohongshu_pack_media
            WHERE scope_id = (SELECT scope_id FROM content_assets WHERE id = @asset)
              AND asset_id = @asset AND revision = @revision
            ORDER BY position
            """;
        command.Parameters.AddWithValue("asset", assetId);
        command.Parameters.AddWithValue("revision", revision);

        var rows = new List<XiaohongshuSelectionRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new XiaohongshuSelectionRow(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2)));
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

    private sealed record XiaohongshuSelectionRow(Guid MediaId, string AltText, int Position);

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
        IReadOnlyList<Guid> MediaIds) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Host.DisposeAsync();
            KeyFile.Dispose();
        }
    }
}
