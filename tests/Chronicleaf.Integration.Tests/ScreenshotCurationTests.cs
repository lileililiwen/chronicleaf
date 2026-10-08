using System.Globalization;
using System.Net;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Capture;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The screenshot-curation scenarios, one per stated outcome, against a real disposable PostgreSQL
/// and the real API host (SCREENSHOT-CURATION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real here.</b> The database is a genuine PostgreSQL server started from a pinned image,
/// migrated with the committed migrations, and every assertion about state reads it back. The API
/// host, the owner session boundary, the antiforgery value, the stores, the delivered media store,
/// the delivered evidence store and all five routes are the production ones. Candidates are produced
/// the honest way: a capture is requested through the real route and run through the real operation
/// queue with the recording provider behind the package's own declared seam, exactly as the
/// screenshot-capture suite does it - so a scenario asserting on stored PNG bytes is asserting on
/// the storage path, never on a browser having visited a page.
/// </para>
/// <para>
/// <b>What is substituted, and why.</b> Only <see cref="IScreenshotProvider"/>, which is the
/// capture package's declared seam. Nothing in this environment can confine a browser, so no
/// scenario here claims a real page was photographed; the PNGs are the labelled synthesised
/// fixture the capture suite already uses.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class ScreenshotCurationTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("8c2f4a71-3b6d-4e19-9f2c-5d8a1b6e7c42");

    private const string PublicUrl = "https://example.com/pricing";
    private const string SecondUrl = "https://example.com/changelog";

    // ===========================================================================================
    // SCREENSHOT-CURATION-001 Success: selecting a screenshot with a caption publishes evidence.
    // ===========================================================================================

    [Fact]
    public async Task Curation_001_Success_selecting_a_screenshot_with_a_caption_appends_an_evidence_revision_referencing_that_media_and_caption()
    {
        var scenario = await ScenarioAsync("cur_001_select");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "定价页面");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];

        using var response = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            first.Revision,
            new
            {
                status = "selected",
                caption = "The pricing table after the redesign.",
                isPrimary = false,
                expectedRevision = first.Revision,
                expectedBundleRevision = 1,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.ToString());

        var curated = await ReadCandidatesAsync(owner, fixture.MilestoneId);
        var selected = Assert.Single(curated, picture => picture.Id == first.Id);
        Assert.Equal("selected", selected.Status);
        Assert.Equal("The pricing table after the redesign.", selected.Caption);
        Assert.False(selected.IsPrimary);
        Assert.Equal(first.Revision + 1, selected.Revision);

        // The design's success clause: a new evidence revision references that media and caption.
        var evidence = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        Assert.Equal(2, evidence.Revision);

        var head = Assert.Single(evidence.Bundles, bundle => bundle.Revision == 2);
        var item = Assert.Single(
            head.Items,
            candidate => candidate.Kind == "screenshot" && candidate.MediaId == first.MediaId.ToString("D"));
        Assert.Equal("The pricing table after the redesign.", item.Text);
        Assert.Equal("complete", item.Availability);
        Assert.Equal(first.Id.ToString("D"), item.SourceRef);
        Assert.Null(item.SupersedesId);
    }

    [Fact]
    public async Task Curation_001_Success_selecting_without_a_caption_stores_provenance_instead_of_an_empty_claim()
    {
        var scenario = await ScenarioAsync("cur_001_no_caption");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "无说明截图");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);

        // The desktop picture, named rather than assumed first: the two candidates share a
        // capture instant and the list order between them is not a contract.
        var first = candidates.Single(candidate => candidate.Viewport == "desktop");

        using var response = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            first.Revision,
            new
            {
                status = "selected",
                expectedRevision = first.Revision,
                expectedBundleRevision = 1,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The row keeps the empty caption the owner left: an empty caption is not rewritten into a
        // sentence nobody wrote.
        var curated = await ReadCandidatesAsync(owner, fixture.MilestoneId);
        Assert.Equal(string.Empty, curated.Single(picture => picture.Id == first.Id).Caption);

        // But a complete evidence item must carry text, so the item carries the provenance the row
        // already holds rather than an empty claim.
        var evidence = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        var item = Assert.Single(
            evidence.Bundles.Single(bundle => bundle.Revision == 2).Items,
            candidate => candidate.Kind == "screenshot");
        Assert.Contains(PublicUrl, item.Text, StringComparison.Ordinal);
        Assert.Contains("desktop", item.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Curation_001_Success_editing_the_caption_of_a_selected_screenshot_appends_a_correcting_revision()
    {
        var scenario = await ScenarioAsync("cur_001_caption_edit");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "说明修订");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];

        await SelectAsync(owner, first.Id, first.Revision, "First wording.", bundleRevision: 1);

        var selected = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);

        using var edited = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            selected.Revision,
            new
            {
                caption = "Second wording, after looking again.",
                expectedRevision = selected.Revision,
                expectedBundleRevision = 2,
            }));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var evidence = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        Assert.Equal(3, evidence.Revision);

        var head = evidence.Bundles.Single(bundle => bundle.Revision == 3);
        var latest = Assert.Single(
            head.Items,
            candidate => candidate.Kind == "screenshot" && candidate.Text == "Second wording, after looking again.");

        // A caption edit corrects the earlier caption rather than duplicating it: the new item
        // names the head item it was appended against, and the earlier bundle still reads what it
        // read. Carried items are new rows per bundle, so the link names the append-time id - the
        // same convention the delivered evidence appends use for a correction.
        var appendedAgainst = evidence.Bundles
            .Single(bundle => bundle.Revision == 2)
            .Items.Single(item => item.Kind == "screenshot");
        Assert.Equal(appendedAgainst.Id, latest.SupersedesId);

        var earlier = evidence.Bundles.Single(bundle => bundle.Revision == 2);
        Assert.Equal("First wording.", earlier.Items.Single(item => item.Kind == "screenshot").Text);
    }

    [Fact]
    public async Task Curation_001_Success_deselecting_appends_a_revision_without_the_picture_and_keeps_history()
    {
        var scenario = await ScenarioAsync("cur_001_deselect");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "取消选择");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];

        await SelectAsync(owner, first.Id, first.Revision, "Kept for a while.", bundleRevision: 1);

        var selected = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);

        using var deselected = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            selected.Revision,
            new
            {
                status = "candidate",
                expectedRevision = selected.Revision,
                expectedBundleRevision = 2,
            }));

        Assert.Equal(HttpStatusCode.OK, deselected.StatusCode);

        var evidence = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        Assert.Equal(3, evidence.Revision);
        Assert.DoesNotContain(
            evidence.Bundles.Single(bundle => bundle.Revision == 3).Items,
            item => item.Kind == "screenshot");

        // Without erasing prior referenced history: the earlier bundle still holds the picture, so
        // content approved against revision 2 still reads what it was approved against.
        Assert.Contains(
            evidence.Bundles.Single(bundle => bundle.Revision == 2).Items,
            item => item.Kind == "screenshot" && item.MediaId == first.MediaId.ToString("D"));

        var curated = await ReadCandidatesAsync(owner, fixture.MilestoneId);
        Assert.Equal("candidate", curated.Single(picture => picture.Id == first.Id).Status);
    }

    [Fact]
    public async Task Curation_001_Success_repeating_an_unchanged_selection_changes_nothing()
    {
        var scenario = await ScenarioAsync("cur_001_noop");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "重复选择");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];

        await SelectAsync(owner, first.Id, first.Revision, "Steady caption.", bundleRevision: 1);

        var selected = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);

        using var repeated = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            selected.Revision,
            new
            {
                status = "selected",
                caption = "Steady caption.",
                expectedRevision = selected.Revision,
                expectedBundleRevision = 2,
            }));

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);

        // A repeated selection with the same words is not a new revision and not a conflict.
        var reread = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);
        Assert.Equal(selected.Revision, reread.Revision);
        Assert.Equal(2, (await ReadEvidenceAsync(owner, fixture.MilestoneId)).Revision);
    }

    // ===========================================================================================
    // SCREENSHOT-CURATION-001 Failure: what is not selected stays outside selected evidence.
    // ===========================================================================================

    [Fact]
    public async Task Curation_001_Failure_a_candidate_that_was_never_selected_stays_outside_selected_evidence()
    {
        var scenario = await ScenarioAsync("cur_001_unselected");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "未选截图");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];
        var second = candidates[1];

        await SelectAsync(owner, first.Id, first.Revision, "Only this one.", bundleRevision: 1);

        var evidence = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        var head = evidence.Bundles.Single(bundle => bundle.Revision == 2);

        Assert.DoesNotContain(
            head.Items,
            item => item.Kind == "screenshot" && item.MediaId == second.MediaId.ToString("D"));

        var curated = await ReadCandidatesAsync(owner, fixture.MilestoneId);
        Assert.Equal("candidate", curated.Single(picture => picture.Id == second.Id).Status);
    }

    [Fact]
    public async Task Curation_001_Failure_a_rejected_picture_cannot_be_made_primary_and_an_unknown_id_is_404()
    {
        var scenario = await ScenarioAsync("cur_001_rejected_primary");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "拒绝设主图");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];

        await RejectAsync(owner, first.Id, first.Revision, bundleRevision: 1);

        var rejected = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);
        Assert.Equal("rejected", rejected.Status);

        using var promote = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            rejected.Revision,
            new
            {
                isPrimary = true,
                expectedRevision = rejected.Revision,
                expectedBundleRevision = 1,
            }));

        // A rejected picture is refused as primary without changing anything.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, promote.StatusCode);
        Assert.Equal("validation_failed", await ReadCodeAsync(promote));

        var reread = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);
        Assert.Equal("rejected", reread.Status);
        Assert.False(reread.IsPrimary);
        Assert.Equal(rejected.Revision, reread.Revision);
        Assert.Equal(1, (await ReadEvidenceAsync(owner, fixture.MilestoneId)).Revision);

        // And an id nobody holds is a 404 rather than a refusal that confirms it exists elsewhere.
        using var foreign = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{Guid.NewGuid():D}",
            1,
            new
            {
                status = "selected",
                expectedRevision = 1,
                expectedBundleRevision = 1,
            }));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Curation_001_Failure_validation_names_the_field_and_writes_nothing()
    {
        var scenario = await ScenarioAsync("cur_001_validation");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "校验失败");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];

        using var unknown = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            first.Revision,
            new
            {
                status = "archived",
                expectedRevision = first.Revision,
                expectedBundleRevision = 1,
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal("status", await ReadFieldAsync(unknown));

        using var tooLong = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            first.Revision,
            new
            {
                caption = new string('字', 1001),
                expectedRevision = first.Revision,
                expectedBundleRevision = 1,
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal("caption", await ReadFieldAsync(tooLong));

        // A mutation without If-Match is 428, because every mutation here is conditional.
        using var bare = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            null,
            new { status = "selected", expectedBundleRevision = 1 }));

        Assert.Equal(HttpStatusCode.PreconditionRequired, bare.StatusCode);

        var reread = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);
        Assert.Equal("candidate", reread.Status);
        Assert.Equal(string.Empty, reread.Caption);
        Assert.Equal(first.Revision, reread.Revision);
        Assert.Equal(1, (await ReadEvidenceAsync(owner, fixture.MilestoneId)).Revision);
    }

    [Fact]
    public async Task Curation_001_Failure_a_stale_bundle_revision_is_a_409_that_writes_nothing()
    {
        var scenario = await ScenarioAsync("cur_001_stale_bundle");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "过期证据版本");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];

        using var stale = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            first.Revision,
            new
            {
                status = "selected",
                expectedRevision = first.Revision,
                expectedBundleRevision = 0,
            }));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("revision_conflict", await ReadCodeAsync(stale));

        var reread = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);
        Assert.Equal("candidate", reread.Status);
        Assert.Equal(first.Revision, reread.Revision);
        Assert.Equal(1, (await ReadEvidenceAsync(owner, fixture.MilestoneId)).Revision);
    }

    [Fact]
    public async Task Curation_001_Failure_an_archived_project_keeps_its_screenshots_unchanged()
    {
        var scenario = await ScenarioAsync("cur_001_archived");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "已归档项目");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];

        using var archived = await owner.SendAsync(await owner.Archive($"/api/projects/{fixture.ProjectId:D}", 1));
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        using var response = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            first.Revision,
            new
            {
                status = "selected",
                expectedRevision = first.Revision,
                expectedBundleRevision = 1,
            }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var reread = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);
        Assert.Equal("candidate", reread.Status);
    }

    [Fact]
    public async Task Curation_001_Failure_selected_media_is_in_use_and_rejected_media_may_retire()
    {
        var scenario = await ScenarioAsync("cur_001_media_in_use");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "媒体引用");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];
        var second = candidates[1];

        await SelectAsync(owner, first.Id, first.Revision, "Pinned by evidence.", bundleRevision: 1);
        await RejectAsync(owner, second.Id, second.Revision, bundleRevision: 2);

        // Physical deletion obeys media_in_use: the selected picture's bytes are referenced by the
        // head bundle and cannot be retired out from under it.
        using var pinned = await owner.SendAsync(await owner.Archive($"/api/media/{first.MediaId:D}", null));
        Assert.Equal(HttpStatusCode.Conflict, pinned.StatusCode);
        Assert.Equal("media_in_use", await ReadCodeAsync(pinned));

        // The rejected picture references nothing in evidence, so its bytes are reclaimable - and
        // the candidate row stays behind as the tombstone rather than being deleted with them.
        using var retired = await owner.SendAsync(await owner.Archive($"/api/media/{second.MediaId:D}", null));
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        var curated = await ReadCandidatesAsync(owner, fixture.MilestoneId);
        Assert.Equal("rejected", curated.Single(picture => picture.Id == second.Id).Status);
    }

    // ===========================================================================================
    // SCREENSHOT-CURATION-001 Boundary: a capture re-run never replaces a human selection.
    // ===========================================================================================

    [Fact]
    public async Task Curation_001_Boundary_a_capture_rerun_leaves_the_human_selection_untouched()
    {
        var scenario = await ScenarioAsync("cur_001_rerun");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "重新截图");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];

        await SelectPrimaryAsync(owner, first.Id, first.Revision, "The chosen one.", bundleRevision: 1);

        var before = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);

        host.Provider.AnswerWithPng(width: 1440, height: 900);
        var operationId = await RequestCaptureAsync(owner, fixture.MilestoneId, SecondUrl, ["desktop"], 0);
        await RunJobAsync(host, operationId);
        Assert.Equal("succeeded", await ReadOperationStatusAsync(owner, operationId));

        // Three pictures now, and the human's choice is exactly where it was: a re-run offers new
        // candidates and never promotes, demotes, recaptions or reorders an old one.
        var after = await ReadCandidatesAsync(owner, fixture.MilestoneId);
        Assert.Equal(3, after.Count);

        var kept = Assert.Single(after, picture => picture.Id == first.Id);
        Assert.Equal("selected", kept.Status);
        Assert.Equal("The chosen one.", kept.Caption);
        Assert.True(kept.IsPrimary);
        Assert.Equal(before.Revision, kept.Revision);

        var offered = Assert.Single(after, picture => picture.SourceUrl == SecondUrl);
        Assert.Equal("candidate", offered.Status);
        Assert.False(offered.IsPrimary);

        Assert.Equal(2, (await ReadEvidenceAsync(owner, fixture.MilestoneId)).Revision);
    }

    // ===========================================================================================
    // SCREENSHOT-CURATION-002 Success: one primary, cleared in the same transaction.
    // ===========================================================================================

    [Fact]
    public async Task Curation_002_Success_changing_the_primary_clears_the_old_one_in_the_same_transaction()
    {
        var scenario = await ScenarioAsync("cur_002_primary");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "主图切换");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];
        var second = candidates[1];

        await SelectPrimaryAsync(owner, first.Id, first.Revision, "First choice.", bundleRevision: 1);

        var selected = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == second.Id);
        await SelectAsync(owner, second.Id, selected.Revision, "Second choice.", bundleRevision: 2);

        var promoted = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == second.Id);

        using var response = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{second.Id:D}",
            promoted.Revision,
            new
            {
                isPrimary = true,
                expectedRevision = promoted.Revision,
                expectedBundleRevision = 3,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // One primary per milestone: the old one cleared in the same transaction that set the new
        // one, so no read between the two statements could have seen both - or neither.
        var primaries = await ChronicleafPostgresFixture.ReadTextAsync(
            scenario.Database,
            $"SELECT count(*) FROM screenshot_candidates WHERE milestone_id = '{fixture.MilestoneId:D}' AND is_primary");

        Assert.Equal("1", primaries);

        var curated = await ReadCandidatesAsync(owner, fixture.MilestoneId);
        Assert.False(curated.Single(picture => picture.Id == first.Id).IsPrimary);
        Assert.True(curated.Single(picture => picture.Id == second.Id).IsPrimary);

        // A primary flip moves no evidence: the bundle still holds both selected pictures.
        Assert.Equal(3, (await ReadEvidenceAsync(owner, fixture.MilestoneId)).Revision);
    }

    [Fact]
    public async Task Curation_002_Success_leaving_the_selected_set_clears_a_primary_without_a_second_request()
    {
        var scenario = await ScenarioAsync("cur_002_primary_cleared");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "主图清除");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];

        await SelectPrimaryAsync(owner, first.Id, first.Revision, "Primary for a while.", bundleRevision: 1);

        var primary = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);
        Assert.True(primary.IsPrimary);

        using var rejected = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            primary.Revision,
            new
            {
                status = "rejected",
                expectedRevision = primary.Revision,
                expectedBundleRevision = 2,
            }));

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        // Rejecting the primary clears the flag in the same write: a rejected primary would
        // otherwise violate the invariant that only a selected picture may be primary.
        var reread = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);
        Assert.Equal("rejected", reread.Status);
        Assert.False(reread.IsPrimary);
    }

    // ===========================================================================================
    // SCREENSHOT-CURATION-002 ordering: an exact, complete sequence of the selected pictures.
    // ===========================================================================================

    [Fact]
    public async Task Curation_002_Success_an_exact_order_of_the_selected_pictures_is_stored_and_served()
    {
        var scenario = await ScenarioAsync("cur_002_order");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "图片排序");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];
        var second = candidates[1];

        await SelectAsync(owner, first.Id, first.Revision, "First captured.", bundleRevision: 1);
        await SelectAsync(owner, second.Id, second.Revision, "Second captured.", bundleRevision: 2);

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/screenshots/order",
            new
            {
                ids = new[] { second.Id.ToString("D"), first.Id.ToString("D") },
                expectedBundleRevision = 3,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ids = await ReadOrderAsync(response);
        Assert.Equal([second.Id.ToString("D"), first.Id.ToString("D")], ids);

        // Stored, not just answered: a fresh read serves the same sequence.
        var curated = await ReadCandidatesAsync(owner, fixture.MilestoneId);
        Assert.Equal(0, curated.Single(picture => picture.Id == second.Id).DisplayOrder);
        Assert.Equal(1, curated.Single(picture => picture.Id == first.Id).DisplayOrder);

        // Ordering moves no evidence: it sequences the rows, and the bundle is untouched.
        Assert.Equal(3, (await ReadEvidenceAsync(owner, fixture.MilestoneId)).Revision);
    }

    [Fact]
    public async Task Curation_002_Failure_an_order_that_drops_adds_or_duplicates_a_picture_is_refused()
    {
        var scenario = await ScenarioAsync("cur_002_order_bad");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "排序校验");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];
        var second = candidates[1];

        await SelectAsync(owner, first.Id, first.Revision, "Kept.", bundleRevision: 1);

        // Dropping the one selected picture would silently unselect it.
        using var dropped = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/screenshots/order",
            new { ids = Array.Empty<string>(), expectedBundleRevision = 2 }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, dropped.StatusCode);

        // Adding the unselected picture would silently select it.
        using var added = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/screenshots/order",
            new
            {
                ids = new[] { first.Id.ToString("D"), second.Id.ToString("D") },
                expectedBundleRevision = 2,
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, added.StatusCode);

        // Naming one picture twice is not a sequence.
        using var duplicated = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/screenshots/order",
            new
            {
                ids = new[] { first.Id.ToString("D"), first.Id.ToString("D") },
                expectedBundleRevision = 2,
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicated.StatusCode);

        // And an order against a bundle that has already moved is a 409, not a silent rebase.
        using var stale = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/screenshots/order",
            new
            {
                ids = new[] { first.Id.ToString("D") },
                expectedBundleRevision = 1,
            }));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var curated = await ReadCandidatesAsync(owner, fixture.MilestoneId);
        Assert.Equal(0, curated.Single(picture => picture.Id == first.Id).DisplayOrder);
    }

    // ===========================================================================================
    // SCREENSHOT-CURATION-002 Boundary: concurrent curation on one revision, one winner.
    // ===========================================================================================

    [Fact]
    public async Task Curation_002_Boundary_two_curations_on_the_same_revision_produce_one_winner_and_one_409()
    {
        var scenario = await ScenarioAsync("cur_002_conflict");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "并发选择");
        var candidates = await CaptureTwoAsync(host, owner, fixture.MilestoneId);
        var first = candidates[0];

        using var winner = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            first.Revision,
            new
            {
                status = "selected",
                caption = "The winner's caption.",
                expectedRevision = first.Revision,
                expectedBundleRevision = 1,
            }));

        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);

        // The same revision, presented again after the row moved, loses: one update wins and the
        // other is told the stored revision rather than merged.
        using var loser = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{first.Id:D}",
            first.Revision,
            new
            {
                status = "selected",
                caption = "The loser's caption.",
                expectedRevision = first.Revision,
                expectedBundleRevision = 2,
            }));

        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal("revision_conflict", await ReadCodeAsync(loser));

        var reread = (await ReadCandidatesAsync(owner, fixture.MilestoneId))
            .Single(picture => picture.Id == first.Id);
        Assert.Equal("The winner's caption.", reread.Caption);
        Assert.Equal(first.Revision + 1, reread.Revision);

        // The loser wrote nothing: one new bundle, not two, and no second screenshot item.
        var evidence = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        Assert.Equal(2, evidence.Revision);
        Assert.Single(evidence.Bundles.Single(bundle => bundle.Revision == 2).Items, item => item.Kind == "screenshot");
    }

    // ===========================================================================================
    // Migration and schema: the curation columns, the one-primary index and the vocabulary.
    // ===========================================================================================

    [Fact]
    public async Task The_curation_columns_index_and_vocabulary_come_from_the_forward_migration_and_readiness_requires_it()
    {
        var database = await postgres.CreateDatabaseAsync("cur_migration");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        // Readiness requires the curation migration by id, and it is the newest required one by
        // position rather than by name - a named "newest" is a value somebody has to remember to
        // update, which is exactly what made the older scenarios in this repository go red.
        Assert.Contains(ChronicleafSchema.ScreenshotCurationMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));

        // A database stopped at the grounded-markdown-drafts schema is short of exactly this one.
        var missing = ChronicleafSchema.MissingMigrationIds(
            ChronicleafSchema.RequiredMigrationIds
                .Where(id => id != ChronicleafSchema.ScreenshotCurationMigrationId)
                .ToArray());
        Assert.Equal([ChronicleafSchema.ScreenshotCurationMigrationId], missing);

        // The columns this package moves exist with the bounds this package enforces.
        Assert.Equal(
            "1000",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT character_maximum_length::text FROM information_schema.columns " +
                "WHERE table_name = 'screenshot_candidates' AND column_name = 'caption'"));
        Assert.Equal(
            "integer",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT data_type FROM information_schema.columns " +
                "WHERE table_name = 'screenshot_candidates' AND column_name = 'display_order'"));

        // One primary per milestone, enforced by the database rather than by a caller being careful.
        Assert.Equal(
            "ux_screenshot_candidates_milestone_primary",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT indexname FROM pg_indexes WHERE tablename = 'screenshot_candidates' " +
                "AND indexname = 'ux_screenshot_candidates_milestone_primary'"));

        // The curation vocabulary, refused by the database rather than interpreted by a reader.
        Assert.Equal(
            "ck_screenshot_candidates_status",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT conname::text FROM pg_constraint WHERE conname = 'ck_screenshot_candidates_status'"));
    }


    // ===========================================================================================
    // Helpers.
    // ===========================================================================================

    private async Task<Scenario> ScenarioAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        var mediaRoot = Path.Combine(Path.GetTempPath(), $"chronicleaf-curation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mediaRoot);

        var screenshots = new RecordingScreenshotProvider(new FixedTimeProvider(Clock));
        var host = CaptureApiHost.Start(database, new FixedTimeProvider(Clock), screenshots, mediaRoot);

        await ProvisionAsync(host);

        return new Scenario(database, host, mediaRoot);
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

    private static async Task<MilestoneFixture> CreateMilestoneAsync(OwnerClient owner, string title)
    {
        using var projectResponse = await owner.SendAsync(
            await owner.Post("/api/projects", new { name = $"{title} 项目", description = $"{title} 说明。" }));
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);

        var projectId = Guid.Parse(JsonDocument
            .Parse(await projectResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);

        using var milestoneResponse = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/milestones",
            new
            {
                title,
                version = "0.1.0",
                occurredAt = "2026-10-01T09:00:00Z",
                type = "feature",
                notes = $"{title} 的记录。",
                knownLimitations = Array.Empty<string>(),
                nextSteps = Array.Empty<string>(),
            }));
        Assert.Equal(HttpStatusCode.Created, milestoneResponse.StatusCode);

        var milestoneId = Guid.Parse(JsonDocument
            .Parse(await milestoneResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);

        // One hand-written evidence item, so every curation below is conditional on a bundle that
        // has something in it rather than on an empty revision that proves nothing.
        using var evidence = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "The owner recorded this by hand.", expectedRevision = 0 },
            0));
        Assert.Equal(HttpStatusCode.Created, evidence.StatusCode);

        return new MilestoneFixture(projectId, milestoneId);
    }

    /// <summary>
    /// Captures one public URL at both viewports through the real queue, and returns the two
    /// candidates the job published.
    /// </summary>
    private static async Task<IReadOnlyList<CandidateView>> CaptureTwoAsync(
        CaptureApiHost host,
        OwnerClient owner,
        Guid milestoneId)
    {
        host.Provider.AnswerWithPng(width: 1440, height: 900);

        var operationId = await RequestCaptureAsync(owner, milestoneId, PublicUrl, ["desktop", "mobile"], 0);
        await RunJobAsync(host, operationId);
        Assert.Equal("succeeded", await ReadOperationStatusAsync(owner, operationId));

        var candidates = await ReadCandidatesAsync(owner, milestoneId);
        Assert.Equal(2, candidates.Count);

        return candidates;
    }

    private static async Task SelectAsync(
        OwnerClient owner,
        Guid screenshotId,
        long screenshotRevision,
        string caption,
        long bundleRevision)
    {
        using var response = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{screenshotId:D}",
            screenshotRevision,
            new
            {
                status = "selected",
                caption,
                expectedRevision = screenshotRevision,
                expectedBundleRevision = bundleRevision,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task SelectPrimaryAsync(
        OwnerClient owner,
        Guid screenshotId,
        long screenshotRevision,
        string caption,
        long bundleRevision)
    {
        using var response = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{screenshotId:D}",
            screenshotRevision,
            new
            {
                status = "selected",
                caption,
                isPrimary = true,
                expectedRevision = screenshotRevision,
                expectedBundleRevision = bundleRevision,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task RejectAsync(
        OwnerClient owner,
        Guid screenshotId,
        long screenshotRevision,
        long bundleRevision)
    {
        using var response = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{screenshotId:D}",
            screenshotRevision,
            new
            {
                status = "rejected",
                expectedRevision = screenshotRevision,
                expectedBundleRevision = bundleRevision,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Guid> RequestCaptureAsync(
        OwnerClient owner,
        Guid milestoneId,
        string url,
        IReadOnlyList<string> viewports,
        int waitMs)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/captures",
            new
            {
                url,
                viewports,
                waitMs,
                fullPage = false,
                expectedBundleRevision = (long?)null,
                idempotencyKey = $"capture-{Guid.NewGuid():N}",
            },
            0));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetString()!);
    }

    private static async Task RunJobAsync(CaptureApiHost host, Guid operationId)
    {
        await host.DispatchAsync();

        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var status = await ChronicleafPostgresFixture.ReadTextAsync(
                host.Database,
                $"SELECT status FROM operations WHERE id = '{operationId}'");

            if (status is "succeeded" or "failed" or "cancelled" or "superseded")
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"The capture operation {operationId} did not reach a terminal status.");
    }

    private static async Task<IReadOnlyList<CandidateView>> ReadCandidatesAsync(OwnerClient owner, Guid milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId:D}/screenshots");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return
        [
            .. document.RootElement.GetProperty("items").EnumerateArray().Select(item => new CandidateView(
                Guid.Parse(item.GetProperty("id").GetString()!),
                Guid.Parse(item.GetProperty("mediaId").GetString()!),
                Guid.Parse(item.GetProperty("projectId").GetString()!),
                item.GetProperty("sourceUrl").GetString()!,
                item.GetProperty("finalUrl").GetString()!,
                item.GetProperty("viewport").GetString()!,
                item.GetProperty("width").GetInt32(),
                item.GetProperty("height").GetInt32(),
                item.GetProperty("capturedAt").GetString()!,
                item.GetProperty("status").GetString()!,
                item.GetProperty("caption").GetString()!,
                item.GetProperty("isPrimary").GetBoolean(),
                item.GetProperty("displayOrder").GetInt32(),
                item.GetProperty("revision").GetInt64()))
        ];
    }

    private static async Task<EvidenceView> ReadEvidenceAsync(OwnerClient owner, Guid milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        return new EvidenceView(
            root.GetProperty("revision").GetInt64(),
            [
                .. root.GetProperty("items").EnumerateArray().Select(bundle => new BundleView(
                    bundle.GetProperty("revision").GetInt64(),
                    [
                        .. bundle.GetProperty("items").EnumerateArray().Select(item => new ItemView(
                            item.GetProperty("id").GetString()!,
                            item.GetProperty("kind").GetString()!,
                            item.GetProperty("sourceRef").GetString()!,
                            item.GetProperty("text").GetString()!,
                            item.GetProperty("mediaId").ValueKind == JsonValueKind.Null
                                ? null
                                : item.GetProperty("mediaId").GetString(),
                            item.GetProperty("availability").GetString()!,
                            item.GetProperty("supersedesId").ValueKind == JsonValueKind.Null
                                ? null
                                : item.GetProperty("supersedesId").GetString()))
                    ]))
            ]);
    }

    private static async Task<IReadOnlyList<string>> ReadOrderAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return [.. document.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!)];
    }

    private static async Task<string> ReadOperationStatusAsync(OwnerClient owner, Guid operationId)
    {
        using var response = await owner.GetAsync($"/api/operations/{operationId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("status").GetString()!;
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<string?> ReadFieldAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("fieldErrors").EnumerateObject().First().Name;
    }

    private sealed record Scenario(string Database, CaptureApiHost Host, string MediaRoot);

    private sealed record MilestoneFixture(Guid ProjectId, Guid MilestoneId);

    private sealed record CandidateView(
        Guid Id,
        Guid MediaId,
        Guid ProjectId,
        string SourceUrl,
        string FinalUrl,
        string Viewport,
        int Width,
        int Height,
        string CapturedAt,
        string Status,
        string Caption,
        bool IsPrimary,
        int DisplayOrder,
        long Revision);

    private sealed record EvidenceView(long Revision, IReadOnlyList<BundleView> Bundles);

    private sealed record BundleView(long Revision, IReadOnlyList<ItemView> Items);

    private sealed record ItemView(
        string Id,
        string Kind,
        string SourceRef,
        string Text,
        string? MediaId,
        string Availability,
        string? SupersedesId);
}
