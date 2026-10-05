using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Chronicleaf.Application.Evolution;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The screenshot-evolution-images scenarios against a real disposable PostgreSQL and the real
/// API host (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real here.</b> The database is a genuine PostgreSQL server started from a pinned
/// image, migrated with the committed migrations, and every assertion about state reads it back.
/// The API host, the owner session boundary, the stores, the delivered media store, the
/// delivered evidence and curation stores and all six evolution routes are the production ones.
/// Selected pictures are produced the honest way: captures requested through the real route and
/// run through the real operation queue with the recording provider behind the capture
/// package's own declared seam, then selected through the real curation route - so a scenario
/// asserting on rendered pixels is asserting on the composition of stored bytes, never on a
/// browser having visited a page.
/// </para>
/// <para>
/// <b>What is substituted, and why.</b> Only <see cref="IScreenshotProvider"/>, which is the
/// capture package's declared seam. Nothing in this environment can confine a browser, so no
/// scenario here claims a real page was photographed; the PNGs are the labelled synthesised
/// fixture the capture suite already uses, decoded and re-composed by this package's own
/// renderer.
/// </para>
/// </remarks>
public sealed class ScreenshotEvolutionImagesTests(ChronicleafPostgresFixture postgres)
    : IClassFixture<ChronicleafPostgresFixture>
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid InstallationId = new("8c2f4a71-3b6d-4e19-9f2c-5d8a1b6e7c42");

    // ===========================================================================================
    // SCREENSHOT-EVOLUTION-IMAGES-001 Success: same-viewport comparison preserves pixels and dates.
    // ===========================================================================================

    [Fact]
    public async Task Evolution_001_Success_same_viewport_comparison_preserves_pixels_provenance_and_dates()
    {
        var scenario = await ScenarioAsync("evo_001_success");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "演进对比");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPngFor("v01", width: 64, height: 40);
        host.Provider.AnswerWithPngFor("v02", width: 40, height: 64);
        var firstShot = await CaptureAndSelectAsync(host, owner, first, "https://example.com/v01", ["desktop"], "First look.");
        var secondShot = await CaptureAndSelectAsync(host, owner, second, "https://example.com/v02", ["desktop"], "Second look.");

        using var rendered = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                layout = "horizontal-timeline",
                idempotencyKey = "evo-001-success",
            }));

        Assert.Equal(HttpStatusCode.Created, rendered.StatusCode);

        using var manifest = JsonDocument.Parse(await rendered.Content.ReadAsStringAsync());
        var root = manifest.RootElement;
        var evolutionId = root.GetProperty("id").GetString()!;
        var selectionHash = root.GetProperty("selectionHash").GetString()!;
        Assert.Equal(64, selectionHash.Length);
        Assert.Equal("draft", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("selectionCurrent").GetBoolean());

        var sources = root.GetProperty("manifest").GetProperty("sources");
        Assert.Equal(2, sources.GetArrayLength());

        // Chronological, earliest first, regardless of request order assumptions.
        Assert.Equal(first.ToString("D"), sources[0].GetProperty("milestoneId").GetString());
        Assert.Equal(second.ToString("D"), sources[1].GetProperty("milestoneId").GetString());
        Assert.Equal("depicted", sources[0].GetProperty("status").GetString());
        Assert.Equal("depicted", sources[1].GetProperty("status").GetString());
        Assert.Equal(firstShot.ScreenshotId.ToString("D"), sources[0].GetProperty("screenshotId").GetString());
        Assert.Equal(firstShot.MediaId.ToString("D"), sources[0].GetProperty("mediaId").GetString());
        Assert.Equal("First look.", sources[0].GetProperty("caption").GetString());
        Assert.Equal("desktop", sources[0].GetProperty("viewport").GetString());
        Assert.Equal("desktop", sources[1].GetProperty("viewport").GetString());

        // The artifact hash is exportable: the preview bytes hash to exactly the bound value.
        using var preview = await owner.GetAsync($"/api/evolution/images/{evolutionId}/preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewBytes = await preview.Content.ReadAsByteArrayAsync();
        Assert.Equal(
            root.GetProperty("artifactSha256").GetString(),
            Convert.ToHexString(SHA256.HashData(previewBytes)).ToLowerInvariant());

        // Pixel preservation: the canvas holds both sources at native size, centered on padding.
        Assert.True(EvolutionPng.TryDecodeToGrey(previewBytes, out var canvas) && canvas is not null);
        Assert.Equal(128, canvas.Width);
        Assert.Equal(64, canvas.Height);

        Assert.True(EvolutionPng.TryDecodeToGrey(TestPng.Create(64, 40), out var greyA) && greyA is not null);
        Assert.True(EvolutionPng.TryDecodeToGrey(TestPng.Create(40, 64), out var greyB) && greyB is not null);
        AssertCanvasRegion(canvas, 128, 0, 12, greyA);
        AssertCanvasRegion(canvas, 128, 76, 0, greyB);

        // Candidates were never compared and nothing was promoted: the manifest cites selected
        // pictures only, and both milestones keep their human curation untouched.
        Assert.Equal(secondShot.ScreenshotId.ToString("D"), sources[1].GetProperty("screenshotId").GetString());
    }

    [Fact]
    public async Task Evolution_001_Success_idempotent_replay_returns_the_bound_row()
    {
        var scenario = await ScenarioAsync("evo_001_idempotent");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "幂等演进");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 32);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/a", ["desktop"], "A.");
        await CaptureAndSelectAsync(host, owner, second, "https://example.com/b", ["desktop"], "B.");

        var body = new
        {
            milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
            layout = "before-after",
            idempotencyKey = "evo-replay-key",
        };

        using var created = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images", body));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();

        using var replayed = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images", body));
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);

        using var replayedDocument = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync());
        Assert.Equal(createdId, replayedDocument.RootElement.GetProperty("id").GetString());
        Assert.False(replayedDocument.RootElement.GetProperty("created").GetBoolean());

        // The same key with other inputs is a conflict that leaves the bound row alone.
        using var conflicted = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                layout = "vertical-long",
                idempotencyKey = "evo-replay-key",
            }));
        Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode);
        Assert.Equal("operation_idempotency_conflict", await ReadCodeAsync(conflicted));

        using var listed = await owner.GetAsync($"/api/projects/{project:D}/evolution/images");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
    }

    // ===========================================================================================
    // SCREENSHOT-EVOLUTION-IMAGES-001 Failure: gaps are reported, candidates never depicted.
    // ===========================================================================================

    [Fact]
    public async Task Evolution_001_Failure_a_milestone_without_a_selected_screenshot_is_an_unavailable_gap_not_a_fabrication()
    {
        var scenario = await ScenarioAsync("evo_001_gap");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "缺口演进");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-08-01T09:00:00Z");
        var middle = await CreateConfirmedMilestoneAsync(owner, project, "v0.5", "2026-09-01T09:00:00Z");
        var last = await CreateConfirmedMilestoneAsync(owner, project, "v1.0", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 16);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/g1", ["desktop"], "Old.");
        await CaptureAndSelectAsync(host, owner, last, "https://example.com/g3", ["desktop"], "New.");

        using var rendered = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), middle.ToString("D"), last.ToString("D") },
                layout = "vertical-long",
            }));

        Assert.Equal(HttpStatusCode.Created, rendered.StatusCode);

        using var manifest = JsonDocument.Parse(await rendered.Content.ReadAsStringAsync());
        var sources = manifest.RootElement.GetProperty("manifest").GetProperty("sources");
        Assert.Equal(3, sources.GetArrayLength());
        Assert.Equal("depicted", sources[0].GetProperty("status").GetString());
        Assert.Equal("unavailable", sources[1].GetProperty("status").GetString());
        Assert.Equal("no-selected-screenshot", sources[1].GetProperty("reason").GetString());
        Assert.Equal(middle.ToString("D"), sources[1].GetProperty("milestoneId").GetString());
        Assert.Equal("depicted", sources[2].GetProperty("status").GetString());

        // The gap band is visibly unavailable: a neutral band no source could have produced.
        var evolutionId = manifest.RootElement.GetProperty("id").GetString()!;
        using var preview = await owner.GetAsync($"/api/evolution/images/{evolutionId}/preview");
        var previewBytes = await preview.Content.ReadAsByteArrayAsync();
        Assert.True(EvolutionPng.TryDecodeToGrey(previewBytes, out var canvas) && canvas is not null);
        Assert.Equal(32, canvas.Width);
        Assert.Equal(48, canvas.Height);

        for (var y = 16; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                Assert.Equal(EvolutionPng.GapGrey, canvas.Pixels[y * 32 + x]);
            }
        }
    }

    [Fact]
    public async Task Evolution_001_Failure_a_candidate_is_never_compared_and_never_promoted()
    {
        var scenario = await ScenarioAsync("evo_001_candidate");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "候选不对比");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 32);
        var chosen = await CaptureAndSelectAsync(host, owner, first, "https://example.com/c1", ["desktop"], "Chosen.");
        var candidate = await CaptureOnlyAsync(host, owner, second, "https://example.com/c2", ["desktop"]);

        // Naming a candidate explicitly is refused: this package never promotes one.
        using var refused = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                layout = "horizontal-timeline",
                selectedScreenshotIds = new[] { chosen.ScreenshotId.ToString("D"), candidate.ScreenshotId.ToString("D") },
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("selectedScreenshotIds", await ReadFieldAsync(refused));

        // And the unselected milestone is a gap: fewer than two depicted pictures is no comparison.
        using var insufficient = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                layout = "horizontal-timeline",
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, insufficient.StatusCode);
        Assert.Equal("insufficient_selected", await ReadCodeAsync(insufficient));

        var curated = await ReadCandidatesAsync(owner, second);
        Assert.Equal("candidate", curated.Single(picture => picture.Id == candidate.ScreenshotId).Status);
    }

    [Fact]
    public async Task Evolution_001_Failure_one_milestone_or_an_unconfirmed_one_is_refused()
    {
        var scenario = await ScenarioAsync("evo_001_shape");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "形状校验");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var draft = await CreateMilestoneAsync(owner, project, "草稿", "2026-09-15T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 32);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/s1", ["desktop"], "Only.");

        using var single = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new { milestoneIds = new[] { first.ToString("D") }, layout = "horizontal-timeline" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, single.StatusCode);
        Assert.Equal("milestoneIds", await ReadFieldAsync(single));

        using var unconfirmed = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), draft.ToString("D") },
                layout = "horizontal-timeline",
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unconfirmed.StatusCode);

        using var missing = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), Guid.NewGuid().ToString("D") },
                layout = "horizontal-timeline",
            }));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var foreignProject = await owner.SendAsync(await owner.Post(
            $"/api/projects/{Guid.NewGuid():D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), draft.ToString("D") },
                layout = "horizontal-timeline",
            }));

        Assert.Equal(HttpStatusCode.NotFound, foreignProject.StatusCode);
    }

    // ===========================================================================================
    // SCREENSHOT-EVOLUTION-IMAGES-001 Boundary: viewports group, except in the gallery.
    // ===========================================================================================

    [Fact]
    public async Task Evolution_001_Boundary_mixed_viewports_require_the_gallery_and_are_labelled_there()
    {
        var scenario = await ScenarioAsync("evo_001_viewport");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "视口分组");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPngFor("desk", width: 48, height: 32);
        host.Provider.AnswerWithPngFor("mob", width: 24, height: 48);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/desk", ["desktop"], "Desktop.");
        await CaptureAndSelectAsync(host, owner, second, "https://example.com/mob", ["mobile"], "Mobile.");

        using var mismatched = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                layout = "horizontal-timeline",
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatched.StatusCode);
        Assert.Equal("comparison_viewport_mismatch", await ReadCodeAsync(mismatched));

        using var gallery = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                layout = "gallery",
            }));

        Assert.Equal(HttpStatusCode.Created, gallery.StatusCode);

        using var manifest = JsonDocument.Parse(await gallery.Content.ReadAsStringAsync());
        var sources = manifest.RootElement.GetProperty("manifest").GetProperty("sources");
        Assert.Equal("desktop", sources[0].GetProperty("viewport").GetString());
        Assert.Equal("mobile", sources[1].GetProperty("viewport").GetString());
    }

    // ===========================================================================================
    // SCREENSHOT-EVOLUTION-IMAGES-002 Success: approval binds the exact artifact hash.
    // ===========================================================================================

    [Fact]
    public async Task Evolution_002_Success_an_approved_artifact_exports_its_exact_bytes()
    {
        var scenario = await ScenarioAsync("evo_002_approve");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "批准导出");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 32);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/e1", ["desktop"], "Before.");
        await CaptureAndSelectAsync(host, owner, second, "https://example.com/e2", ["desktop"], "After.");

        using var rendered = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                layout = "before-after",
            }));

        Assert.Equal(HttpStatusCode.Created, rendered.StatusCode);
        using var manifest = JsonDocument.Parse(await rendered.Content.ReadAsStringAsync());
        var evolutionId = manifest.RootElement.GetProperty("id").GetString()!;
        var selectionHash = manifest.RootElement.GetProperty("selectionHash").GetString()!;
        var artifactHash = manifest.RootElement.GetProperty("artifactSha256").GetString()!;

        // A draft exports nothing: approval is the gate, not a label.
        using var premature = await owner.GetAsync($"/api/evolution/images/{evolutionId}/download");
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
        Assert.Equal("approval_required", await ReadCodeAsync(premature));

        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/evolution/images/{evolutionId}/approve",
            new { expectedSelectionHash = selectionHash }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var approvedDocument = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
        Assert.Equal("approved", approvedDocument.RootElement.GetProperty("status").GetString());

        using var downloaded = await owner.GetAsync($"/api/evolution/images/{evolutionId}/download");
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal("image/png", downloaded.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", downloaded.Content.Headers.ContentDisposition?.DispositionType ?? string.Empty);
        Assert.Equal($"\"{artifactHash}\"", downloaded.Headers.ETag?.ToString());

        var downloadedBytes = await downloaded.Content.ReadAsByteArrayAsync();
        Assert.Equal(artifactHash, Convert.ToHexString(SHA256.HashData(downloadedBytes)).ToLowerInvariant());

        // Approving the same manifest again is the same decision answered again.
        using var repeated = await owner.SendAsync(await owner.Post(
            $"/api/evolution/images/{evolutionId}/approve",
            new { expectedSelectionHash = selectionHash }));
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.True(JsonDocument.Parse(await repeated.Content.ReadAsStringAsync()).RootElement.GetProperty("created").GetBoolean());
    }

    // ===========================================================================================
    // SCREENSHOT-EVOLUTION-IMAGES-002 Failure: a moved selection supersedes, then stales.
    // ===========================================================================================

    [Fact]
    public async Task Evolution_002_Failure_a_caption_edit_supersedes_the_render_and_stales_the_approval()
    {
        var scenario = await ScenarioAsync("evo_002_stale");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "选择移动");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 32);
        var firstShot = await CaptureAndSelectAsync(host, owner, first, "https://example.com/f1", ["desktop"], "Original wording.");
        await CaptureAndSelectAsync(host, owner, second, "https://example.com/f2", ["desktop"], "Steady.");

        using var rendered = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                layout = "horizontal-timeline",
            }));

        using var manifest = JsonDocument.Parse(await rendered.Content.ReadAsStringAsync());
        var evolutionId = manifest.RootElement.GetProperty("id").GetString()!;
        var selectionHash = manifest.RootElement.GetProperty("selectionHash").GetString()!;

        // A caption edit moves the selection the render was built from.
        var selected = (await ReadCandidatesAsync(owner, first)).Single(picture => picture.Id == firstShot.ScreenshotId);
        var evidenceRevision = (await ReadEvidenceRevisionAsync(owner, first));
        using var edited = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{firstShot.ScreenshotId:D}",
            selected.Revision,
            new
            {
                caption = "Reworded after looking again.",
                expectedRevision = selected.Revision,
                expectedBundleRevision = evidenceRevision,
            }));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        // The reviewed hash no longer authorizes anything.
        using var superseded = await owner.SendAsync(await owner.Post(
            $"/api/evolution/images/{evolutionId}/approve",
            new { expectedSelectionHash = selectionHash }));
        Assert.Equal(HttpStatusCode.Conflict, superseded.StatusCode);
        Assert.Equal("revision_conflict", await ReadCodeAsync(superseded));

        using var reread = await owner.GetAsync($"/api/evolution/images/{evolutionId}");
        Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
        Assert.False(JsonDocument.Parse(await reread.Content.ReadAsStringAsync()).RootElement.GetProperty("selectionCurrent").GetBoolean());

        // And an approval given before the move stops authorizing the export after it.
        using var fresh = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                layout = "horizontal-timeline",
            }));
        using var freshManifest = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
        var freshId = freshManifest.RootElement.GetProperty("id").GetString()!;
        var freshHash = freshManifest.RootElement.GetProperty("selectionHash").GetString()!;
        Assert.NotEqual(selectionHash, freshHash);

        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/evolution/images/{freshId}/approve",
            new { expectedSelectionHash = freshHash }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var reselected = (await ReadCandidatesAsync(owner, first)).Single(picture => picture.Id == firstShot.ScreenshotId);
        var movedRevision = await ReadEvidenceRevisionAsync(owner, first);
        using var editedAgain = await owner.SendAsync(await owner.Patch(
            $"/api/screenshots/{firstShot.ScreenshotId:D}",
            reselected.Revision,
            new
            {
                caption = "Reworded a third time.",
                expectedRevision = reselected.Revision,
                expectedBundleRevision = movedRevision,
            }));
        Assert.Equal(HttpStatusCode.OK, editedAgain.StatusCode);

        using var stale = await owner.GetAsync($"/api/evolution/images/{freshId}/download");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("approval_stale", await ReadCodeAsync(stale));
    }

    // ===========================================================================================
    // SCREENSHOT-EVOLUTION-IMAGES-002 Boundary: the ceiling refuses, storing nothing usable.
    // ===========================================================================================

    [Fact]
    public async Task Evolution_002_Boundary_an_oversized_render_fails_without_a_usable_partial_image()
    {
        var scenario = await ScenarioAsync("evo_002_large");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "过大渲染");
        var milestones = new List<Guid>();

        for (var index = 0; index < 4; index++)
        {
            milestones.Add(await CreateConfirmedMilestoneAsync(
                owner, project, $"v{index}", $"2026-09-0{index + 1}T09:00:00Z"));
        }

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 4000, height: 3000);

        foreach (var milestone in milestones)
        {
            await CaptureAndSelectAsync(host, owner, milestone, $"https://example.com/big-{milestone:N}", ["desktop"], "Big.");
        }

        using var refused = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/images",
            new
            {
                milestoneIds = milestones.Select(id => id.ToString("D")).ToArray(),
                layout = "horizontal-timeline",
            }));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Equal("payload_too_large", await ReadCodeAsync(refused));

        // Nothing usable was stored: no artifact row for this project.
        using var listed = await owner.GetAsync($"/api/projects/{project:D}/evolution/images");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void Evolution_002_Boundary_the_renderer_refuses_a_canvas_past_the_pixel_ceiling()
    {
        var left = new GreyImage(5000, 5000, new byte[5000 * 5000]);
        var right = new GreyImage(5000, 5000, new byte[5000 * 5000]);

        Assert.Throws<EvolutionTooLargeException>(() =>
            EvolutionPng.Compose(
                [EvolutionSlot.Depicted(left), EvolutionSlot.Depicted(right)],
                EvolutionLayouts.HorizontalTimeline));
    }

    [Fact]
    public void Evolution_renderer_round_trips_grey_rgb_and_rgba_sources_without_invention()
    {
        var greyBytes = TestPng.Create(16, 8);
        Assert.True(EvolutionPng.TryDecodeToGrey(greyBytes, out var grey) && grey is not null);
        Assert.Equal(16, grey.Width);

        var rendered = EvolutionPng.Compose(
            [EvolutionSlot.Depicted(grey), EvolutionSlot.Gap()],
            EvolutionLayouts.VerticalLong);

        Assert.Equal(16, rendered.Width);
        Assert.Equal(16, rendered.Height);

        Assert.True(EvolutionPng.TryDecodeToGrey(rendered.Png, out var canvas) && canvas is not null);

        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                Assert.Equal(grey.Pixels[y * 16 + x], canvas.Pixels[y * 16 + x]);
            }
        }

        for (var y = 8; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                Assert.Equal(EvolutionPng.GapGrey, canvas.Pixels[y * 16 + x]);
            }
        }

        Assert.False(EvolutionPng.TryDecodeToGrey("not an image"u8.ToArray(), out _));
    }

    // ===========================================================================================
    // Migration and schema: the evolution table, its bounds and its vocabulary.
    // ===========================================================================================

    [Fact]
    public async Task The_evolution_table_index_and_vocabulary_come_from_the_forward_migration_and_readiness_requires_it()
    {
        var database = await postgres.CreateDatabaseAsync("evo_migration");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        Assert.Contains(ChronicleafSchema.ScreenshotEvolutionImagesMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));

        var missing = ChronicleafSchema.MissingMigrationIds(
            ChronicleafSchema.RequiredMigrationIds
                .Where(id => id != ChronicleafSchema.ScreenshotEvolutionImagesMigrationId)
                .ToArray());
        Assert.Equal([ChronicleafSchema.ScreenshotEvolutionImagesMigrationId], missing);

        Assert.Equal(
            "evolution_images",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT tablename FROM pg_tables WHERE tablename = 'evolution_images'"));

        Assert.Equal(
            "ux_evolution_images_scope_id_project_id_idempotency_key",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT indexname FROM pg_indexes WHERE tablename = 'evolution_images' " +
                "AND indexname = 'ux_evolution_images_scope_id_project_id_idempotency_key'"));

        Assert.Equal(
            "ck_evolution_images_status",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT conname::text FROM pg_constraint WHERE conname = 'ck_evolution_images_status'"));

        Assert.Equal(
            "ck_evolution_images_layout",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT conname::text FROM pg_constraint WHERE conname = 'ck_evolution_images_layout'"));
    }

    // ===========================================================================================
    // Helpers.
    // ===========================================================================================

    private async Task<Scenario> ScenarioAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        var mediaRoot = Path.Combine(Path.GetTempPath(), $"chronicleaf-evolution-{Guid.NewGuid():N}");
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

    private static async Task<Guid> CreateProjectAsync(OwnerClient owner, string name)
    {
        using var response = await owner.SendAsync(
            await owner.Post("/api/projects", new { name, description = $"{name} 说明。" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<Guid> CreateMilestoneAsync(OwnerClient owner, Guid projectId, string title, string occurredAt)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/milestones",
            new
            {
                title,
                version = "0.1.0",
                occurredAt,
                type = "feature",
                notes = $"{title} 的记录。",
                knownLimitations = Array.Empty<string>(),
                nextSteps = Array.Empty<string>(),
            }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var milestoneId = Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);

        using var evidence = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "The owner recorded this by hand.", expectedRevision = 0 },
            0));
        Assert.Equal(HttpStatusCode.Created, evidence.StatusCode);

        return milestoneId;
    }

    private static async Task<Guid> CreateConfirmedMilestoneAsync(
        OwnerClient owner, Guid projectId, string title, string occurredAt)
    {
        var milestoneId = await CreateMilestoneAsync(owner, projectId, title, occurredAt);

        using var confirmed = await owner.SendAsync(
            await owner.Post($"/api/milestones/{milestoneId:D}/confirm", null, 1));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        return milestoneId;
    }

    private sealed record CapturedShot(Guid ScreenshotId, Guid MediaId, long Revision);

    private static async Task<CapturedShot> CaptureAndSelectAsync(
        CaptureApiHost host,
        OwnerClient owner,
        Guid milestoneId,
        string url,
        IReadOnlyList<string> viewports,
        string caption)
    {
        var shot = await CaptureOnlyAsync(host, owner, milestoneId, url, viewports);
        await SelectAsync(owner, shot.ScreenshotId, shot.Revision, caption, bundleRevision: 1);

        var selected = (await ReadCandidatesAsync(owner, milestoneId))
            .Single(picture => picture.Id == shot.ScreenshotId);

        return shot with { Revision = selected.Revision };
    }

    private static async Task<CapturedShot> CaptureOnlyAsync(
        CaptureApiHost host,
        OwnerClient owner,
        Guid milestoneId,
        string url,
        IReadOnlyList<string> viewports)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/captures",
            new
            {
                url,
                viewports,
                waitMs = 0,
                fullPage = false,
                expectedBundleRevision = (long?)null,
                idempotencyKey = $"capture-{Guid.NewGuid():N}",
            },
            0));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var operationId = Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetString()!);

        await RunJobAsync(host, operationId);
        Assert.Equal("succeeded", await ReadOperationStatusAsync(owner, operationId));

        var candidate = (await ReadCandidatesAsync(owner, milestoneId))
            .Single(picture => picture.SourceUrl == url && viewports.Contains(picture.Viewport));

        return new CapturedShot(candidate.Id, candidate.MediaId, candidate.Revision);
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

    private static async Task RunJobAsync(CaptureApiHost host, Guid operationId)
    {
        await host.DispatchAsync();

        var deadline = DateTime.UtcNow.AddSeconds(60);

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

    private static void AssertCanvasRegion(GreyImage canvas, int canvasWidth, int x, int y, GreyImage expected)
    {
        for (var row = 0; row < expected.Height; row++)
        {
            for (var column = 0; column < expected.Width; column++)
            {
                Assert.Equal(
                    expected.Pixels[row * expected.Width + column],
                    canvas.Pixels[(y + row) * canvasWidth + x + column]);
            }
        }
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
                item.GetProperty("sourceUrl").GetString()!,
                item.GetProperty("viewport").GetString()!,
                item.GetProperty("status").GetString()!,
                item.GetProperty("caption").GetString()!,
                item.GetProperty("revision").GetInt64()))
        ];
    }

    private static async Task<long> ReadEvidenceRevisionAsync(OwnerClient owner, Guid milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("revision").GetInt64();
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

    private sealed record CandidateView(
        Guid Id,
        Guid MediaId,
        string SourceUrl,
        string Viewport,
        string Status,
        string Caption,
        long Revision);
}
