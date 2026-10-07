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
/// VIDEO-STORY-SCRIPTS-001 and VIDEO-STORY-SCRIPTS-002 against a real disposable PostgreSQL,
/// the real <c>Chronicleaf.Api</c> host, the real operation queue and the real evidence, voice,
/// asset, review and media contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the voice
/// service, the asset store, the approvals table, the media store, the routes, the access
/// boundary and the database. The approved story every scenario scripts from is produced by a
/// real generation job through the same scripted provider the drafts suite drives - the only
/// substitution for prose, and the same declared boundary: no stage here may be read as evidence
/// that any provider works. Scripting itself calls no provider at all. The depicted pictures are
/// real capture jobs' candidates, selected through the delivered curation route, behind the same
/// scripted-browser seam the capture suite declares: the PNG bytes are a labelled synthesised
/// fixture, and no scenario here claims a real page was photographed. The capture host is
/// disposed before the drafts host starts, on the same terms as the HTML export suite, so the
/// two hosts' workers never share this database.
/// </para>
/// <para>
/// <b>What is never touched.</b> No renderer, no encoder, no screen recorder, no video endpoint,
/// no account and no token exists anywhere in this suite, because none exists in the package:
/// the publish route is asserted as a refusal with no outbound request possible, and the export
/// is asserted to carry scripts and pictures - storyboard.json, narration.md, caption.srt - and
/// never a video track. A suite that asserted a synthesized video would be asserting the thing
/// the design forbids.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> A script is read out of the raw <c>content_assets</c> and
/// <c>content_revisions</c> tables, not only from the generate route, and an approval is read out
/// of the raw <c>content_approvals</c> table. The claims here are about what is <em>stored</em>
/// and what is <em>served</em>, and a value read back through the same serializer that wrote it
/// would agree with itself whether or not the bytes on disk were right.
/// </para>
/// </remarks>
public sealed class VideoStoryScriptsTests(ChronicleafPostgresFixture postgres)
    : IClassFixture<ChronicleafPostgresFixture>
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("e7a3c1f9-4b28-4d6e-9f2a-8c5e1b7d3a40");

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The evidence every script scenario derives from. Free of digits on purpose, for the same
    /// reason the drafts suite's own note is: the numeral rule refuses a figure that appears in
    /// no evidence item, and a note full of numbers would let claims pass for the wrong reason.
    /// </summary>
    private const string EvidenceNote =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string KnownLimitation = "Only Linux is covered.";

    private const string FirstCaption = "The pipeline digest view.";

    private const string SecondCaption = "The pipeline digest view from the second capture.";

    private const string FirstNarration = "The pipeline now reproduces from a clean checkout.";

    private const string SecondNarration = "The build ran twice and produced the same digest.";

    private const string ThirdNarration = "Only Linux is covered, and the limit is recorded.";

    // ==============================================================================================
    // VIDEO-STORY-SCRIPTS-001 Success: a valid 15-second script covers the runtime exactly.
    // ==============================================================================================

    [Fact]
    public async Task VideoStory_001_Success_fifteen_second_script_covers_runtime_exactly()
    {
        await using var fixture = await ArrangeAsync("vs_001_fifteen");

        using var generated = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
            ValidGenerate(fixture, 15, FifteenSecondShots(fixture))));

        Assert.True(
            generated.StatusCode == HttpStatusCode.Created,
            $"generate answered {(int)generated.StatusCode}: {await generated.Content.ReadAsStringAsync()}");

        var answer = JsonDocument.Parse(await generated.Content.ReadAsStringAsync());
        var assetId = answer.RootElement.GetProperty("assetId").GetGuid();

        Assert.Equal(1, answer.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(15, answer.RootElement.GetProperty("durationSeconds").GetInt32());
        Assert.Equal(3, answer.RootElement.GetProperty("shotCount").GetInt32());
        Assert.Equal(15000, answer.RootElement.GetProperty("totalMs").GetInt64());
        Assert.Equal(fixture.StoryAssetId, answer.RootElement.GetProperty("sourceAssetId").GetGuid());
        Assert.Equal(
            [fixture.MediaIdOne!.Value.ToString("D"), fixture.MediaIdTwo!.Value.ToString("D")],
            [.. answer.RootElement.GetProperty("imageOrder").EnumerateArray().Select(entry => entry.GetString()!)]);

        // The stored bytes, read out of the rows rather than the route.
        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 1);
        Assert.NotNull(stored);
        Assert.Equal(answer.RootElement.GetProperty("bodyHash").GetString(), stored!.BodyHash);
        Assert.Equal(ContentRevision.Hash(stored.BodyMarkdown), stored.BodyHash);

        using var board = JsonDocument.Parse(stored.BodyMarkdown);
        Assert.Equal("video-story", board.RootElement.GetProperty("format").GetString());
        Assert.Equal(15, board.RootElement.GetProperty("durationSeconds").GetInt32());

        var shots = board.RootElement.GetProperty("shots").EnumerateArray().ToList();
        Assert.Equal(3, shots.Count);

        // Exact interval coverage without gaps or overlaps: each shot starts where the previous
        // one ends, the first opens at zero and the last closes the runtime.
        Assert.Equal(0, shots[0].GetProperty("startMs").GetInt64());
        Assert.Equal(15000, shots[^1].GetProperty("endMs").GetInt64());

        for (var index = 0; index < shots.Count; index++)
        {
            var shot = shots[index];
            Assert.True(shot.GetProperty("endMs").GetInt64() - shot.GetProperty("startMs").GetInt64() >= 500);

            if (index > 0)
            {
                Assert.Equal(shots[index - 1].GetProperty("endMs").GetInt64(), shot.GetProperty("startMs").GetInt64());
            }

            // Every shot cites evidence and depicts a selected picture: a shot with no source is
            // not a shot in this product.
            Assert.NotEmpty(shots[index].GetProperty("sourceEvidenceIds").EnumerateArray());
            Assert.NotEqual(Guid.Empty.ToString("D"), shot.GetProperty("screenshotMediaId").GetString());
        }

        // The known limitations travel inside the stored script: a script that dropped them
        // would be a plan that forgot what it cannot show.
        Assert.Contains(
            KnownLimitation,
            string.Join("\n", board.RootElement.GetProperty("limitations").EnumerateArray().Select(entry => entry.GetString())));

        // The asset reads as a video-story draft, and the review package reports exactly that:
        // a draft that was never approved, with no approval row anywhere behind it.
        using var read = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}");
        var view = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("draft", view.RootElement.GetProperty("status").GetString());
        Assert.Equal("video-story", view.RootElement.GetProperty("type").GetString());

        using var review = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}/review");
        var state = JsonDocument.Parse(await review.Content.ReadAsStringAsync());
        Assert.Equal("draft", state.RootElement.GetProperty("reviewStatus").GetString());

        Assert.Empty(await ReviewRows.ListAsync(fixture.Database, assetId));

        // The script carries the voice it was scripted under as a value: a later profile edit
        // cannot reach the stored bytes.
        var snapshotBefore = stored.VoiceSnapshot;
        Assert.Contains("devlog", snapshotBefore, StringComparison.Ordinal);

        using var profile = await fixture.Owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/voice-profile");
        var profileBody = JsonDocument.Parse(await profile.Content.ReadAsStringAsync());
        var profileRevision = profileBody.RootElement.GetProperty("revision").GetInt64();

        using var moved = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.ProjectId:D}/voice-profile",
            new { presetId = "devlog", overrides = new { tone = "neutral" } },
            profileRevision));

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var reread = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 1);
        Assert.NotNull(reread);
        Assert.Equal(snapshotBefore, reread!.VoiceSnapshot);
    }

    // ==============================================================================================
    // VIDEO-STORY-SCRIPTS-001 Success: the 30 and 60-second runtimes script the same way.
    // ==============================================================================================

    [Fact]
    public async Task VideoStory_001_Success_thirty_and_sixty_second_scripts_cover_their_runtimes()
    {
        await using var fixture = await ArrangeAsync("vs_001_runtimes");

        foreach (var (duration, shots, totalMs) in new[]
                 {
                     (30, ThirtySecondShots(fixture), 30000L),
                     (60, SixtySecondShots(fixture), 60000L),
                 })
        {
            using var generated = await fixture.Owner.SendAsync(await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                ValidGenerate(fixture, duration, shots)));

            Assert.True(
                generated.StatusCode == HttpStatusCode.Created,
                $"generate {duration}s answered {(int)generated.StatusCode}: {await generated.Content.ReadAsStringAsync()}");

            var answer = JsonDocument.Parse(await generated.Content.ReadAsStringAsync());
            Assert.Equal(duration, answer.RootElement.GetProperty("durationSeconds").GetInt32());
            Assert.Equal(totalMs, answer.RootElement.GetProperty("totalMs").GetInt64());

            var assetId = answer.RootElement.GetProperty("assetId").GetGuid();
            var revision = answer.RootElement.GetProperty("revision").GetInt64();

            var stored = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, revision);
            Assert.NotNull(stored);

            using var board = JsonDocument.Parse(stored!.BodyMarkdown);
            var listed = board.RootElement.GetProperty("shots").EnumerateArray().ToList();

            Assert.Equal(0, listed[0].GetProperty("startMs").GetInt64());
            Assert.Equal(totalMs, listed[^1].GetProperty("endMs").GetInt64());

            for (var index = 1; index < listed.Count; index++)
            {
                Assert.Equal(
                    listed[index - 1].GetProperty("endMs").GetInt64(),
                    listed[index].GetProperty("startMs").GetInt64());
            }
        }
    }

    // ==============================================================================================
    // VIDEO-STORY-SCRIPTS-001 refusals: every invalid script stores nothing.
    // ==============================================================================================

    [Fact]
    public async Task VideoStory_001_Refusals_invalid_scripts_store_nothing()
    {
        await using var fixture = await ArrangeAsync("vs_001_refusals", approveStory: false);

        // An unapproved source is approval_required: no decision stands behind the script.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                ValidGenerate(fixture, 15, FifteenSecondShots(fixture))),
            HttpStatusCode.Conflict,
            "approval_required");

        // An unknown milestone is the delivered 404, with no scope crossed to answer it.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{Guid.NewGuid():D}/video-stories",
                ValidGenerate(fixture, 15, FifteenSecondShots(fixture))),
            HttpStatusCode.NotFound,
            "not_found");

        // A body that names nothing is refused by name rather than read as a zero runtime.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                new { }),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");

        await ApproveStoryAsync(fixture);

        // A runtime outside 15/30/60 is refused: the storyboard is bounded by design.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                ValidGenerate(fixture, 45, FifteenSecondShots(fixture))),
            HttpStatusCode.UnprocessableEntity,
            "invalid_story_schema");

        // A gap between shots is refused: the runtime must be covered exactly.
        var gapped = FifteenSecondShots(fixture);
        gapped[1] = Shot(fixture, 6000, 10000, SecondCaption, SecondNarration, "cut");
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                ValidGenerate(fixture, 15, gapped)),
            HttpStatusCode.UnprocessableEntity,
            "invalid_story_schema");

        // An overlap is refused for the same reason: two shots never share a millisecond.
        var overlapped = FifteenSecondShots(fixture);
        overlapped[1] = Shot(fixture, 4000, 10000, SecondCaption, SecondNarration, "cut");
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                ValidGenerate(fixture, 15, overlapped)),
            HttpStatusCode.UnprocessableEntity,
            "invalid_story_schema");

        // A shot under 500ms is refused: it could not be read aloud or looked at.
        var hurried = FifteenSecondShots(fixture);
        hurried[0] = Shot(fixture, 0, 499, FirstCaption, FirstNarration, "cut");
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                ValidGenerate(fixture, 15, hurried)),
            HttpStatusCode.UnprocessableEntity,
            "invalid_story_schema");

        // An unknown handoff is refused: a shot hands off with cut or fade.
        var dissolved = FifteenSecondShots(fixture);
        dissolved[0] = Shot(fixture, 0, 5000, FirstCaption, FirstNarration, "wipe");
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                ValidGenerate(fixture, 15, dissolved)),
            HttpStatusCode.UnprocessableEntity,
            "invalid_story_schema");

        // A picture the frozen bundle does not hold is an invented picture, not a shot.
        var invented = FifteenSecondShots(fixture);
        invented[0] = Shot(Guid.NewGuid(), 0, 5000, FirstCaption, FirstNarration, "cut", fixture);
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                ValidGenerate(fixture, 15, invented)),
            HttpStatusCode.UnprocessableEntity,
            "invalid_story_schema");

        // An evidence id the frozen bundle does not hold is an unsupported reference.
        var uncited = FifteenSecondShots(fixture);
        uncited[0] = ShotWithEvidence(
            fixture.MediaIdOne!.Value, 0, 5000, FirstCaption, FirstNarration, "cut", Guid.NewGuid());
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                ValidGenerate(fixture, 15, uncited)),
            HttpStatusCode.UnprocessableEntity,
            "invalid_story_schema");

        // A figure the evidence never stated is refused even in the owner's own words: the
        // script cannot claim product behaviour the evidence never recorded.
        var boasting = FifteenSecondShots(fixture);
        boasting[0] = Shot(fixture, 0, 5000, FirstCaption, "The pipeline is 99.99 percent faster now.", "cut");
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                ValidGenerate(fixture, 15, boasting)),
            HttpStatusCode.UnprocessableEntity,
            "invalid_story_schema");

        // A moved evidence head is a conflict with the record's own state, not a corrected body.
        await AssertCodeAsync(
            fixture.Owner,
            await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
                ValidGenerate(fixture, 15, FifteenSecondShots(fixture), evidenceRevision: fixture.BundleRevision - 1)),
            HttpStatusCode.Conflict,
            "revision_conflict");

        // Nothing above stored anything: no video-story row exists for this milestone.
        Assert.Null(await ReadVideoStoryAssetAsync(fixture.Database, fixture.MilestoneId));
    }

    // ==============================================================================================
    // VIDEO-STORY-SCRIPTS-002 Success: an approved script exports scripts plus pictures.
    // ==============================================================================================

    [Fact]
    public async Task VideoStory_002_Success_approved_script_exports_scripts_and_pictures()
    {
        await using var fixture = await ArrangeAsync("vs_002_export");

        var scripted = await GenerateAsync(fixture, 15, FifteenSecondShots(fixture));
        await ApproveAsync(fixture.Owner, scripted, 1, fixture.BundleRevision);

        using var export = await fixture.Owner.GetAsync($"/api/assets/{scripted:D}/export?format=video-story");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);

        var fileName = export.Content.Headers.ContentDisposition?.FileNameStar
            ?? export.Content.Headers.ContentDisposition?.FileName;
        Assert.Contains("video-story.zip", fileName, StringComparison.Ordinal);

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, scripted, 1);
        Assert.NotNull(stored);

        var exportBytes = await export.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(exportBytes), ZipArchiveMode.Read);

        // storyboard.json is exactly the approved bytes, not a re-render.
        var storyboardBytes = await ReadEntryAsync(archive, "storyboard.json");
        Assert.Equal(Encoding.UTF8.GetBytes(stored!.BodyMarkdown), storyboardBytes);

        // narration.md carries the milestone title, every spoken line and the limitations.
        var narration = Encoding.UTF8.GetString(await ReadEntryAsync(archive, "narration.md"));
        Assert.Contains("Reproducible release pipeline", narration, StringComparison.Ordinal);
        Assert.Contains(FirstNarration, narration, StringComparison.Ordinal);
        Assert.Contains(SecondNarration, narration, StringComparison.Ordinal);
        Assert.Contains(ThirdNarration, narration, StringComparison.Ordinal);
        Assert.Contains(KnownLimitation, narration, StringComparison.Ordinal);

        // caption.srt round-trips the shots: three cues whose timings are the stored intervals
        // carrying the stored lines, and nothing else.
        var cues = ParseSrt(Encoding.UTF8.GetString(await ReadEntryAsync(archive, "caption.srt")));
        Assert.Equal(3, cues.Count);
        Assert.Equal(("00:00:00,000", "00:00:05,000", FirstNarration), cues[0]);
        Assert.Equal(("00:00:05,000", "00:00:10,000", SecondNarration), cues[1]);
        Assert.Equal(("00:00:10,000", "00:00:15,000", ThirdNarration), cues[2]);

        // The manifest names the pictures, the owner's captions and the verifiable digests.
        var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));
        Assert.Equal(scripted.ToString("D"), manifest.RootElement.GetProperty("assetId").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(15, manifest.RootElement.GetProperty("durationSeconds").GetInt32());

        var images = manifest.RootElement.GetProperty("images").EnumerateArray().ToList();
        Assert.Equal(2, images.Count);
        Assert.Equal(fixture.MediaIdOne!.Value.ToString("D"), images[0].GetProperty("mediaId").GetString());
        Assert.Equal(FirstCaption, images[0].GetProperty("caption").GetString());
        Assert.Equal("image/png", images[0].GetProperty("mediaType").GetString());

        var firstPng = TestPng.Create(64, 40);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(firstPng)).ToLowerInvariant(),
            images[0].GetProperty("sha256").GetString());

        // And the served pictures are those exact bytes under the manifest's own paths.
        Assert.Equal(firstPng, await ReadEntryAsync(archive, images[0].GetProperty("file").GetString()!));
        Assert.Equal(
            TestPng.Create(80, 48),
            await ReadEntryAsync(archive, images[1].GetProperty("file").GetString()!));

        // The same approved script packs identically twice: nothing here records when the
        // export ran.
        using var again = await fixture.Owner.GetAsync($"/api/assets/{scripted:D}/export?format=video-story");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(exportBytes, await again.Content.ReadAsByteArrayAsync());
    }

    // ==============================================================================================
    // VIDEO-STORY-SCRIPTS-002 Failure: never approved exports nothing; a caption edit and a
    // reorder each end the approval.
    // ==============================================================================================

    [Fact]
    public async Task VideoStory_002_Failure_unapproved_caption_edited_and_reordered_scripts_export_nothing()
    {
        await using var fixture = await ArrangeAsync("vs_002_stale");

        var scripted = await GenerateAsync(fixture, 15, FifteenSecondShots(fixture));

        // Never approved is approval_required: no decision stands behind the scripts.
        using var bare = await fixture.Owner.GetAsync($"/api/assets/{scripted:D}/export?format=video-story");
        Assert.Equal(HttpStatusCode.Conflict, bare.StatusCode);
        Assert.Equal(
            "approval_required",
            JsonDocument.Parse(await bare.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        await ApproveAsync(fixture.Owner, scripted, 1, fixture.BundleRevision);

        using var review = await fixture.Owner.GetAsync($"/api/assets/{scripted:D}/review");
        Assert.Equal(
            "approved",
            JsonDocument.Parse(await review.Content.ReadAsStringAsync()).RootElement.GetProperty("reviewStatus").GetString());

        // A caption edit appends a bundle revision the approval no longer names: the export is
        // stale and the review says invalidated, under the same bindings the Markdown export
        // re-checks.
        using var candidates = await fixture.Owner.GetAsync($"/api/milestones/{fixture.MilestoneId:D}/screenshots");
        var candidate = JsonDocument.Parse(await candidates.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items").EnumerateArray().First();
        var screenshotId = Guid.Parse(candidate.GetProperty("id").GetString()!);
        var screenshotRevision = candidate.GetProperty("revision").GetInt64();

        using var recaptioned = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/screenshots/{screenshotId:D}",
            screenshotRevision,
            new
            {
                caption = "The pipeline digest view, reworded after looking again.",
                expectedRevision = screenshotRevision,
                expectedBundleRevision = fixture.BundleRevision,
            }));

        Assert.Equal(HttpStatusCode.OK, recaptioned.StatusCode);

        using var stale = await fixture.Owner.GetAsync($"/api/assets/{scripted:D}/export?format=video-story");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "approval_stale",
            JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        using var reread = await fixture.Owner.GetAsync($"/api/assets/{scripted:D}/review");
        Assert.Equal(
            "invalidated",
            JsonDocument.Parse(await reread.Content.ReadAsStringAsync()).RootElement.GetProperty("reviewStatus").GetString());
    }

    [Fact]
    public async Task VideoStory_002_Failure_reordered_script_requires_a_new_approval()
    {
        await using var fixture = await ArrangeAsync("vs_002_reorder");

        var scripted = await GenerateAsync(fixture, 15, FifteenSecondShots(fixture));
        await ApproveAsync(fixture.Owner, scripted, 1, fixture.BundleRevision);

        // Reordering the shots appends a head revision the approval no longer names. Both the
        // default export and the explicit first-revision export are stale: the owner already
        // rewrote the script, so the reviewed text is no longer the text they are looking at.
        var fixedOrder = new List<object>
        {
            ShotAt(fixture.MediaIdTwo!.Value, 0, 5000, SecondCaption, ThirdNarration, "fade", fixture),
            ShotAt(fixture.MediaIdOne!.Value, 5000, 10000, SecondCaption, SecondNarration, "cut", fixture),
            ShotAt(fixture.MediaIdOne!.Value, 10000, 15000, FirstCaption, FirstNarration, "cut", fixture),
        };

        using var regenerated = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
            ValidGenerate(fixture, 15, fixedOrder)));

        Assert.Equal(HttpStatusCode.Created, regenerated.StatusCode);
        Assert.Equal(
            2,
            JsonDocument.Parse(await regenerated.Content.ReadAsStringAsync()).RootElement.GetProperty("revision").GetInt64());

        using var stale = await fixture.Owner.GetAsync($"/api/assets/{scripted:D}/export?format=video-story");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "approval_stale",
            JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        using var explicitOld = await fixture.Owner.GetAsync($"/api/assets/{scripted:D}/export?format=video-story&revision=1");
        Assert.Equal(HttpStatusCode.Conflict, explicitOld.StatusCode);
        Assert.Equal(
            "approval_stale",
            JsonDocument.Parse(await explicitOld.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    // ==============================================================================================
    // VIDEO-STORY-SCRIPTS-002 Boundary: native publication is refused and no video is claimed.
    // ==============================================================================================

    [Fact]
    public async Task VideoStory_002_Boundary_native_publish_is_refused_and_no_video_is_claimed()
    {
        await using var fixture = await ArrangeAsync("vs_002_publish");

        var scripted = await GenerateAsync(fixture, 15, FifteenSecondShots(fixture));
        await ApproveAsync(fixture.Owner, scripted, 1, fixture.BundleRevision);

        var operationsBefore = await ChronicleafPostgresFixture.ReadTextAsync(
            fixture.Database,
            "SELECT COUNT(*)::text FROM operations");
        var auditsBefore = await NonSessionAuditCountAsync(fixture.Database);
        var storiesBefore = await ChronicleafPostgresFixture.ReadTextAsync(
            fixture.Database,
            "SELECT COUNT(*)::text FROM audit_events WHERE action LIKE 'video-story%'");

        // Even approved, even reviewed, publication is refused: there is no renderer, no
        // encoder and no transport behind it.
        using var refused = await fixture.Owner.SendAsync(
            await fixture.Owner.PostWithoutBody($"/api/assets/{scripted:D}/video-story/publish"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(
            "publisher_unsupported",
            JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        // A refusal that changed anything would be an account action of its own: no operation
        // was enqueued and no audit row was written.
        Assert.Equal(
            operationsBefore,
            await ChronicleafPostgresFixture.ReadTextAsync(
                fixture.Database,
                "SELECT COUNT(*)::text FROM operations"));
        Assert.Equal(auditsBefore, await NonSessionAuditCountAsync(fixture.Database));
        Assert.Equal(
            storiesBefore,
            await ChronicleafPostgresFixture.ReadTextAsync(
                fixture.Database,
                "SELECT COUNT(*)::text FROM audit_events WHERE action LIKE 'video-story%'"));

        // A foreign id is still the delivered 404 rather than a transport refusal.
        using var foreign = await fixture.Owner.SendAsync(
            await fixture.Owner.PostWithoutBody($"/api/assets/{Guid.NewGuid():D}/video-story/publish"));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        // And the export package claims no video anywhere: scripts and pictures, in a ZIP whose
        // storyboard names its own format and whose entries name no video track.
        using var export = await fixture.Owner.GetAsync($"/api/assets/{scripted:D}/export?format=video-story");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        using var archive = new ZipArchive(
            new MemoryStream(await export.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains("storyboard.json", names);
        Assert.Contains("narration.md", names);
        Assert.Contains("caption.srt", names);
        Assert.DoesNotContain(names, name => name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));

        var storyboard = Encoding.UTF8.GetString(await ReadEntryAsync(archive, "storyboard.json"));
        Assert.Contains("\"format\":\"video-story\"", storyboard, StringComparison.Ordinal);
        Assert.DoesNotContain("videoUrl", storyboard, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("render", storyboard, StringComparison.OrdinalIgnoreCase);
    }

    // ==============================================================================================
    // VIDEO-STORY-PANEL-003: the discovery read names the milestone's script, or a null asset.
    // ==============================================================================================

    [Fact]
    public async Task VideoStory_Panel_Discovery_empty_milestone_reads_null_asset_not_404()
    {
        await using var fixture = await ArrangeAsync("vs_panel_empty");

        using var discovered = await fixture.Owner.GetAsync(
            $"/api/milestones/{fixture.MilestoneId:D}/video-story");

        Assert.Equal(HttpStatusCode.OK, discovered.StatusCode);
        var answer = JsonDocument.Parse(await discovered.Content.ReadAsStringAsync());
        Assert.Equal(
            JsonValueKind.Null,
            answer.RootElement.GetProperty("assetId").ValueKind);
        Assert.Equal(0, answer.RootElement.GetProperty("revision").GetInt64());

        // An absent milestone is still the delivered 404, so the empty answer cannot be used
        // to probe for another owner's milestones.
        using var absent = await fixture.Owner.GetAsync(
            $"/api/milestones/{Guid.NewGuid():D}/video-story");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);

        // And a sessionless read is still 401 before any scope is consulted.
        using var anonymous = fixture.Host.CreateClient();
        using var noSession = await anonymous.GetAsync(
            $"/api/milestones/{fixture.MilestoneId:D}/video-story");
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
    }

    [Fact]
    public async Task VideoStory_Panel_Discovery_names_the_generated_script()
    {
        await using var fixture = await ArrangeAsync("vs_panel_named");

        var scripted = await GenerateAsync(fixture, 30, ThirtySecondShots(fixture));

        using var discovered = await fixture.Owner.GetAsync(
            $"/api/milestones/{fixture.MilestoneId:D}/video-story");

        Assert.Equal(HttpStatusCode.OK, discovered.StatusCode);
        var answer = JsonDocument.Parse(await discovered.Content.ReadAsStringAsync());
        Assert.Equal(scripted, answer.RootElement.GetProperty("assetId").GetGuid());
        Assert.Equal(1, answer.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(30, answer.RootElement.GetProperty("durationSeconds").GetInt32());
        Assert.Equal(3, answer.RootElement.GetProperty("shotCount").GetInt32());
        Assert.Equal(
            fixture.BundleRevision,
            answer.RootElement.GetProperty("evidenceRevision").GetInt64());
        Assert.Equal(
            fixture.VoiceRevision,
            answer.RootElement.GetProperty("voiceRevision").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(
            answer.RootElement.GetProperty("bodyHash").GetString()));
    }

    // ==============================================================================================
    // VIDEO-STORY-PANEL-004: a raw edit of a script body is refused and the export is untouched.
    // ==============================================================================================

    [Fact]
    public async Task VideoStory_Panel_Edit_is_refused_and_the_approved_export_still_serves()
    {
        await using var fixture = await ArrangeAsync("vs_panel_edit");

        var scripted = await GenerateAsync(fixture, 15, FifteenSecondShots(fixture));
        await ApproveAsync(fixture.Owner, scripted, 1, fixture.BundleRevision);

        using var before = await fixture.Owner.GetAsync(
            $"/api/assets/{scripted:D}/export?format=video-story");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var beforeBytes = await before.Content.ReadAsByteArrayAsync();

        // Shots are immutable and re-scripting is the update path: a hand edit would store bytes
        // the export answers 500 for, so it is refused naming the field with the remedy.
        using var refused = await fixture.Owner.SendAsync(
            await fixture.Owner.Patch(
                $"/api/assets/{scripted:D}",
                1,
                new
                {
                    expectedRevision = 1,
                    bodyMarkdown = "Edited storyboard text that is not canonical JSON.",
                    title = (string?)null,
                }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var failure = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", failure.RootElement.GetProperty("code").GetString());
        Assert.True(
            failure.RootElement.GetProperty("fieldErrors").TryGetProperty("bodyMarkdown", out _),
            $"the refusal names bodyMarkdown: {await refused.Content.ReadAsStringAsync()}");

        // Nothing was stored: the head is still revision 1 and the approved export serves the
        // identical bytes it served before the refused write.
        using var reread = await fixture.Owner.GetAsync($"/api/assets/{scripted:D}");
        Assert.Equal(
            1,
            JsonDocument.Parse(await reread.Content.ReadAsStringAsync())
                .RootElement.GetProperty("currentRevision").GetInt64());

        using var after = await fixture.Owner.GetAsync(
            $"/api/assets/{scripted:D}/export?format=video-story");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(beforeBytes, await after.Content.ReadAsByteArrayAsync());
    }

    // ==============================================================================================
    // Shot builders.
    // ==============================================================================================

    private static List<object> FifteenSecondShots(Fixture fixture) =>
        [
            Shot(fixture, 0, 5000, FirstCaption, FirstNarration, "cut"),
            Shot(fixture, 5000, 10000, SecondCaption, SecondNarration, "fade"),
            Shot(fixture, 10000, 15000, FirstCaption, ThirdNarration, "cut"),
        ];

    private static List<object> ThirtySecondShots(Fixture fixture) =>
        [
            Shot(fixture, 0, 10000, FirstCaption, FirstNarration, "cut"),
            Shot(fixture, 10000, 20000, SecondCaption, SecondNarration, "fade"),
            Shot(fixture, 20000, 30000, FirstCaption, ThirdNarration, "cut"),
        ];

    private static List<object> SixtySecondShots(Fixture fixture) =>
        [
            Shot(fixture, 0, 20000, FirstCaption, FirstNarration, "cut"),
            Shot(fixture, 20000, 40000, SecondCaption, SecondNarration, "fade"),
            Shot(fixture, 40000, 60000, FirstCaption, ThirdNarration, "cut"),
        ];

    private static object Shot(
        Fixture fixture,
        long startMs,
        long endMs,
        string caption,
        string narration,
        string transition) =>
        ShotAt(
            (startMs / 5000 % 2 == 0 ? fixture.MediaIdOne : fixture.MediaIdTwo)!.Value,
            startMs,
            endMs,
            caption,
            narration,
            transition,
            fixture);

    private static object Shot(
        Guid mediaId,
        long startMs,
        long endMs,
        string caption,
        string narration,
        string transition,
        Fixture fixture) =>
        ShotAt(mediaId, startMs, endMs, caption, narration, transition, fixture);

    private static object ShotAt(
        Guid mediaId,
        long startMs,
        long endMs,
        string caption,
        string narration,
        string transition,
        Fixture fixture) =>
        ShotWithEvidence(mediaId, startMs, endMs, caption, narration, transition, fixture.ManualItemId);

    private static object ShotWithEvidence(
        Guid mediaId,
        long startMs,
        long endMs,
        string caption,
        string narration,
        string transition,
        Guid evidenceId) =>
        new
        {
            screenshotMediaId = mediaId.ToString("D"),
            caption,
            narration,
            transition,
            startMs,
            endMs,
            sourceEvidenceIds = new[] { evidenceId.ToString("D") },
        };

    private static object ValidGenerate(
        Fixture fixture,
        int durationSeconds,
        IReadOnlyList<object> shots,
        long? evidenceRevision = null) =>
        new
        {
            durationSeconds,
            sourceAssetId = fixture.StoryAssetId.ToString("D"),
            evidenceRevision = evidenceRevision ?? fixture.BundleRevision,
            voiceRevision = fixture.VoiceRevision,
            shots,
        };

    private static async Task<Guid> GenerateAsync(Fixture fixture, int durationSeconds, IReadOnlyList<object> shots)
    {
        using var generated = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/video-stories",
            ValidGenerate(fixture, durationSeconds, shots)));

        Assert.True(
            generated.StatusCode == HttpStatusCode.Created,
            $"generate answered {(int)generated.StatusCode}: {await generated.Content.ReadAsStringAsync()}");

        return JsonDocument.Parse(await generated.Content.ReadAsStringAsync())
            .RootElement.GetProperty("assetId").GetGuid();
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

    private static async Task ApproveStoryAsync(Fixture fixture) =>
        await ApproveAsync(fixture.Owner, fixture.StoryAssetId, 1, fixture.BundleRevision);

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

    private static List<(string Start, string End, string Text)> ParseSrt(string srt)
    {
        var cues = new List<(string, string, string)>();
        var blocks = srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines.Length >= 3, $"cue has an index, a timing and a line: {block}");
            Assert.True(int.TryParse(lines[0].Trim(), out _), $"cue opens with its index: {block}");

            var timings = lines[1].Trim().Split(" --> ", StringSplitOptions.None);
            Assert.Equal(2, timings.Length);

            cues.Add((timings[0], timings[1], string.Join(" ", lines[2..]).Trim()));
        }

        return cues;
    }

    private static async Task<string?> NonSessionAuditCountAsync(string connectionString) =>
        await ChronicleafPostgresFixture.ReadTextAsync(
            connectionString,
            "SELECT COUNT(*)::text FROM audit_events WHERE action NOT LIKE 'session.%'");

    private static async Task<Guid?> ReadVideoStoryAssetAsync(string connectionString, Guid milestoneId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM content_assets
            WHERE milestone_id = @milestone AND type = 'video-story'
            """;
        command.Parameters.AddWithValue("milestone", milestoneId);

        var value = await command.ExecuteScalarAsync();
        return value is Guid id ? id : null;
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

    // ==============================================================================================
    // The fixture.
    // ==============================================================================================

    /// <summary>
    /// Builds everything a scenario needs: a real database, a real project, a confirmed milestone
    /// with real recorded evidence, a real sealed credential, a real authorized preview, a stored
    /// draft produced by a real generation job, the approved story it belongs to, and two real
    /// capture jobs' selected candidates already carried in the evidence head the story grounds in.
    /// </summary>
    private async Task<Fixture> ArrangeAsync(
        string purpose,
        bool approveStory = true,
        string presetId = "devlog",
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

            // The generated body is English, and so is every narration and caption these
            // scenarios script: the voice they are judged under must be English too, or the
            // language rule would refuse the script for reading in the wrong script.
            using var voiced = await setupOwner.SendAsync(await setupOwner.Put(
                $"/api/projects/{projectId:D}/voice-profile",
                new { presetId, overrides = overrides ?? new { } },
                0));

            Assert.True(
                voiced.StatusCode == HttpStatusCode.OK,
                $"voice answered {(int)voiced.StatusCode}: {await voiced.Content.ReadAsStringAsync()}");
        }

        Guid? mediaIdOne = null;
        Guid? mediaIdTwo = null;
        HeadView head;

        await using (var capture = CaptureApiHost.Start(
                         database, clock, new RecordingScreenshotProvider(clock)))
        {
            using var pictureOwner = await OwnerClient.CreateAsync(capture);

            var first = await CaptureAndSelectAsync(
                pictureOwner, capture, milestoneId, $"vs-capture-{purpose}-one", 64, 40, FirstCaption, 1, []);
            var second = await CaptureAndSelectAsync(
                pictureOwner, capture, milestoneId, $"vs-capture-{purpose}-two", 80, 48, SecondCaption, 2, [first.ScreenshotId]);

            mediaIdOne = first.MediaId;
            mediaIdTwo = second.MediaId;

            // The selects appended the second and third bundle revisions: the carried manual fact
            // has a new id there, so the summary cites the head's ids rather than the first
            // revision's.
            await using var reader = GroundedMarkdownDraftsApiHost.StartProduction(
                database, clock, keyFile.FilePath);
            using var readerOwner = await OwnerClient.CreateAsync(reader);
            head = await ReadHeadAsync(readerOwner, milestoneId);
            Assert.Equal(3, head.Revision);
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
                idempotencyKey = $"vs-{purpose}",
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var accepted = JsonDocument.Parse(await requested.Content.ReadAsStringAsync());
        var draftOperationId = accepted.RootElement.GetProperty("operationId").GetGuid();

        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, draftOperationId, "succeeded", JobBudget);

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
            head,
            voiceRevision,
            mediaIdOne,
            mediaIdTwo);

        if (approveStory)
        {
            await ApproveStoryAsync(fixture);
        }

        return fixture;
    }

    private static async Task<(Guid MediaId, Guid ScreenshotId)> CaptureAndSelectAsync(
        OwnerClient owner,
        CaptureApiHost capture,
        Guid milestoneId,
        string idempotencyKey,
        int width,
        int height,
        string caption,
        long? expectedBundleRevision,
        HashSet<Guid> knownScreenshotIds)
    {
        capture.Provider.AnswerWithPng(width: width, height: height);

        using var requested = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/captures",
            new
            {
                url = "https://example.com/pipeline",
                viewports = new[] { "desktop" },
                waitMs = 0,
                fullPage = false,
                expectedBundleRevision = (long?)null,
                idempotencyKey,
            },
            0));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var operationId = Guid.Parse(JsonDocument
            .Parse(await requested.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetString()!);

        await capture.DispatchAsync();
        await OperationRows.AwaitAsync(capture.Database, operationId, "succeeded", JobBudget);

        using var candidates = await owner.GetAsync($"/api/milestones/{milestoneId:D}/screenshots");
        var candidate = JsonDocument.Parse(await candidates.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items").EnumerateArray()
            .First(entry => !knownScreenshotIds.Contains(Guid.Parse(entry.GetProperty("id").GetString()!)));

        var screenshotId = Guid.Parse(candidate.GetProperty("id").GetString()!);
        var screenshotRevision = candidate.GetProperty("revision").GetInt64();
        var mediaId = Guid.Parse(candidate.GetProperty("mediaId").GetString()!);

        using var selected = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{screenshotId:D}",
            screenshotRevision,
            new
            {
                status = "selected",
                caption,
                expectedRevision = screenshotRevision,
                expectedBundleRevision,
            }));

        Assert.True(
            selected.StatusCode == HttpStatusCode.OK,
            $"select answered {(int)selected.StatusCode}: {await selected.Content.ReadAsStringAsync()}");

        return (mediaId, screenshotId);
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
        HeadView Head,
        long VoiceRevision,
        Guid? MediaIdOne,
        Guid? MediaIdTwo) : IAsyncDisposable
    {
        public long BundleRevision => Head.Revision;

        public Guid ManualItemId => Head.ManualItemId;

        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Host.DisposeAsync();
            KeyFile.Dispose();
        }
    }
}
