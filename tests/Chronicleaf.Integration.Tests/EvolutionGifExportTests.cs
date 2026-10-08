using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evolution;
using Chronicleaf.Application.EvolutionGifs;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The evolution-gif-export scenarios against a real disposable PostgreSQL and the real
/// API host (EVOLUTION-GIF-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real here.</b> The database is a genuine PostgreSQL server started from a pinned
/// image, migrated with the committed migrations, and every assertion about state reads it back.
/// The API host, the owner session boundary, the stores, the delivered curation and evidence
/// stores and all six GIF routes are the production ones. Selected pictures are produced the
/// honest way: captures requested through the real route and run through the real operation
/// queue with the recording provider behind the capture package's own declared seam, then
/// selected through the real curation route - so a scenario asserting on animation frames is
/// asserting on the composition of stored bytes, never on a browser having visited a page.
/// </para>
/// <para>
/// <b>What is substituted, and why.</b> Only <see cref="IScreenshotProvider"/>, which is the
/// capture package's declared seam. Nothing in this environment can confine a browser, so no
/// scenario here claims a real page was photographed; the PNGs are the labelled synthesised
/// fixture the capture suite already uses, decoded and re-encoded by this package's own
/// deterministic GIF codec.
/// </para>
/// <para>
/// <b>What decodes the GIF.</b> The <see cref="GifProbe"/> below is an independent GIF89a
/// reader written for these tests: it parses the header, the global table, the loop extension,
/// every graphic control extension and every image descriptor, and decompresses the LZW stream
/// itself. Frame order, dimensions, delays and the loop flag are asserted against what it
/// reads, and frame pixels are asserted against the source PNGs decoded by the delivered
/// evolution PNG reader - so the encoder is verified by two readers it does not own.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class EvolutionGifExportTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid InstallationId = new("8c2f4a71-3b6d-4e19-9f2c-5d8a1b6e7c42");

    // ===========================================================================================
    // EVOLUTION-GIF-EXPORT-001 Success: chronological frames, durations, loop and manifest.
    // ===========================================================================================

    [Fact]
    public async Task Gif_001_Success_chronological_frames_durations_loop_and_manifest_match()
    {
        var scenario = await ScenarioAsync("gif_001_success");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "GIF 演进");
        var oldest = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-08-01T09:00:00Z");
        var middle = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-09-01T09:00:00Z");
        var newest = await CreateConfirmedMilestoneAsync(owner, project, "v0.3", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPngFor("gold", width: 64, height: 40);
        host.Provider.AnswerWithPngFor("green", width: 40, height: 64);
        host.Provider.AnswerWithPngFor("grey", width: 48, height: 48);
        var oldestShot = await CaptureAndSelectAsync(host, owner, oldest, "https://example.com/gold", ["desktop"], "Gold.");
        var middleShot = await CaptureAndSelectAsync(host, owner, middle, "https://example.com/green", ["desktop"], "Green.");
        var newestShot = await CaptureAndSelectAsync(host, owner, newest, "https://example.com/grey", ["desktop"], "Grey.");

        // Requested out of order: the canvas order is chronological unless a manual order fixes it.
        using var rendered = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new
            {
                milestoneIds = new[] { middle.ToString("D"), newest.ToString("D"), oldest.ToString("D") },
                durationsMs = new[] { 500, 1000, 1500 },
                loop = true,
                idempotencyKey = "gif-001-success",
            }));

        Assert.Equal(HttpStatusCode.Created, rendered.StatusCode);

        using var manifest = JsonDocument.Parse(await rendered.Content.ReadAsStringAsync());
        var root = manifest.RootElement;
        var gifId = root.GetProperty("id").GetString()!;
        var selectionHash = root.GetProperty("selectionHash").GetString()!;
        Assert.Equal(64, selectionHash.Length);
        Assert.Equal("draft", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("selectionCurrent").GetBoolean());
        Assert.Equal(3, root.GetProperty("frameCount").GetInt32());
        Assert.Equal(3000, root.GetProperty("totalDurationMs").GetInt32());
        Assert.True(root.GetProperty("loop").GetBoolean());

        var body = root.GetProperty("manifest");
        Assert.Equal("evolution-gif-manifest/1", body.GetProperty("version").GetString());
        Assert.Equal(
            [oldest.ToString("D"), middle.ToString("D"), newest.ToString("D")],
            [.. body.GetProperty("frameOrder").EnumerateArray().Select(frame => frame.GetString()!)]);
        Assert.Equal([500, 1000, 1500], [.. body.GetProperty("durationsMs").EnumerateArray().Select(delay => delay.GetInt32())]);
        Assert.True(body.GetProperty("loop").GetBoolean());

        var sources = body.GetProperty("sources");
        Assert.Equal(3, sources.GetArrayLength());
        Assert.Equal(oldestShot.ScreenshotId.ToString("D"), sources[0].GetProperty("screenshotId").GetString());
        Assert.Equal(middleShot.ScreenshotId.ToString("D"), sources[1].GetProperty("screenshotId").GetString());
        Assert.Equal(newestShot.ScreenshotId.ToString("D"), sources[2].GetProperty("screenshotId").GetString());
        Assert.Equal("Gold.", sources[0].GetProperty("caption").GetString());
        Assert.Equal(500, sources[0].GetProperty("delayMs").GetInt32());
        Assert.Equal(64, sources[0].GetProperty("width").GetInt32());
        Assert.Equal(40, sources[0].GetProperty("height").GetInt32());

        // Every frame cites the media revision it was built from.
        Assert.Equal(oldestShot.MediaId.ToString("D"), sources[0].GetProperty("mediaId").GetString());
        Assert.Equal(64, sources[0].GetProperty("mediaSha256").GetString()!.Length);

        // The artifact hash is exportable: the preview bytes hash to exactly the bound value.
        using var preview = await owner.GetAsync($"/api/evolution/gifs/{gifId}/preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/gif", preview.Content.Headers.ContentType?.MediaType);

        var previewBytes = await preview.Content.ReadAsByteArrayAsync();
        var artifactHash = root.GetProperty("artifactSha256").GetString()!;
        Assert.Equal(artifactHash, Convert.ToHexString(SHA256.HashData(previewBytes)).ToLowerInvariant());

        var decoded = GifProbe.Decode(previewBytes);
        Assert.Equal(64, decoded.Width);
        Assert.Equal(64, decoded.Height);
        Assert.True(decoded.Loop);
        Assert.Equal(3, decoded.Frames.Count);
        Assert.Equal([50, 100, 150], decoded.Frames.Select(frame => frame.DelayCs).ToList());

        // Each frame carries its source's pixels centered on the canvas, in canvas order.
        AssertFramePixels(decoded.Frames[0].Pixels, decoded.Width, decoded.Height, TestPng.Create(64, 40));
        AssertFramePixels(decoded.Frames[1].Pixels, decoded.Width, decoded.Height, TestPng.Create(40, 64));
        AssertFramePixels(decoded.Frames[2].Pixels, decoded.Width, decoded.Height, TestPng.Create(48, 48));

        // A rebuild of the unchanged selection is byte-identical: determinism is a hash, not a claim.
        using var rebuilt = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new
            {
                milestoneIds = new[] { oldest.ToString("D"), middle.ToString("D"), newest.ToString("D") },
                durationsMs = new[] { 500, 1000, 1500 },
                loop = true,
                idempotencyKey = "gif-001-rebuild",
            }));
        Assert.Equal(HttpStatusCode.Created, rebuilt.StatusCode);
        using var rebuiltDocument = JsonDocument.Parse(await rebuilt.Content.ReadAsStringAsync());
        Assert.Equal(artifactHash, rebuiltDocument.RootElement.GetProperty("artifactSha256").GetString());

        using var rebuiltPreview = await owner.GetAsync(
            $"/api/evolution/gifs/{rebuiltDocument.RootElement.GetProperty("id").GetString()}/preview");
        Assert.Equal(previewBytes, await rebuiltPreview.Content.ReadAsByteArrayAsync());
    }

    // ===========================================================================================
    // EVOLUTION-GIF-EXPORT-001 Success: manual order and a single duration for all frames.
    // ===========================================================================================

    [Fact]
    public async Task Gif_001_Success_manual_order_and_single_duration_are_reviewed_playback()
    {
        var scenario = await ScenarioAsync("gif_001_order");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "顺序播放");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPngFor("one", width: 32, height: 24);
        host.Provider.AnswerWithPngFor("two", width: 24, height: 32);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/one", ["desktop"], "One.");
        await CaptureAndSelectAsync(host, owner, second, "https://example.com/two", ["desktop"], "Two.");

        using var rendered = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                manualOrder = new[] { second.ToString("D"), first.ToString("D") },
                durationsMs = new[] { 2000 },
            }));

        Assert.Equal(HttpStatusCode.Created, rendered.StatusCode);
        using var manifest = JsonDocument.Parse(await rendered.Content.ReadAsStringAsync());
        var root = manifest.RootElement;
        var gifId = root.GetProperty("id").GetString()!;
        Assert.False(root.GetProperty("loop").GetBoolean());
        Assert.Equal(4000, root.GetProperty("totalDurationMs").GetInt32());
        Assert.Equal(
            [second.ToString("D"), first.ToString("D")],
            [.. root.GetProperty("manifest").GetProperty("frameOrder").EnumerateArray().Select(frame => frame.GetString()!)]);

        using var preview = await owner.GetAsync($"/api/evolution/gifs/{gifId}/preview");
        var decoded = GifProbe.Decode(await preview.Content.ReadAsByteArrayAsync());
        Assert.False(decoded.Loop);
        Assert.Equal([200, 200], decoded.Frames.Select(frame => frame.DelayCs).ToList());
        AssertFramePixels(decoded.Frames[0].Pixels, decoded.Width, decoded.Height, TestPng.Create(24, 32));
        AssertFramePixels(decoded.Frames[1].Pixels, decoded.Width, decoded.Height, TestPng.Create(32, 24));
    }

    // ===========================================================================================
    // EVOLUTION-GIF-EXPORT-001 Failure: missing bytes fail with no partial artifact.
    // ===========================================================================================

    [Fact]
    public async Task Gif_001_Failure_missing_picture_bytes_fail_without_a_partial_artifact()
    {
        var scenario = await ScenarioAsync("gif_001_missing");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "缺图动画");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 32);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/m1", ["desktop"], "First.");
        var secondShot = await CaptureAndSelectAsync(host, owner, second, "https://example.com/m2", ["desktop"], "Second.");

        // A ready row whose object is gone: the bytes were lost after the review, not refused
        // before it. Deleting the stored object faults the read the way a lost volume would.
        var storageKey = await ChronicleafPostgresFixture.ReadTextAsync(
            scenario.Database,
            $"SELECT storage_key FROM media WHERE id = '{secondShot.MediaId:D}'");
        Assert.NotNull(storageKey);
        File.Delete(Path.Combine(scenario.MediaRoot, storageKey));

        using var missing = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new { milestoneIds = new[] { first.ToString("D"), second.ToString("D") } }));

        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal("media_unavailable", await ReadCodeAsync(missing));

        using var listed = await owner.GetAsync($"/api/projects/{project:D}/evolution/gifs");
        Assert.Equal(0, JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
    }

    // ===========================================================================================
    // EVOLUTION-GIF-EXPORT-001 Failure: candidates are never depicted.
    // ===========================================================================================

    [Fact]
    public async Task Gif_001_Failure_candidates_are_never_depicted_and_pins_must_be_selected()
    {
        var scenario = await ScenarioAsync("gif_001_candidate");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "候选不动画");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");
        var third = await CreateConfirmedMilestoneAsync(owner, project, "v0.3", "2026-10-02T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 32);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/c1", ["desktop"], "Chosen.");
        var candidate = await CaptureOnlyAsync(host, owner, second, "https://example.com/c2", ["desktop"]);
        await CaptureAndSelectAsync(host, owner, third, "https://example.com/c3", ["desktop"], "Also chosen.");

        // A milestone with only a candidate cannot be a frame: an animation has no gap band.
        using var gapped = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new { milestoneIds = new[] { first.ToString("D"), second.ToString("D") } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, gapped.StatusCode);
        Assert.Equal("insufficient_selected", await ReadCodeAsync(gapped));

        // Naming the candidate does not promote it: it is refused as a pin.
        using var pinned = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new
            {
                milestoneIds = new[] { first.ToString("D"), third.ToString("D") },
                selectedScreenshotIds = new[] { candidate.ScreenshotId.ToString("D") },
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, pinned.StatusCode);
        Assert.Equal("validation_failed", await ReadCodeAsync(pinned));

        using var listed = await owner.GetAsync($"/api/projects/{project:D}/evolution/gifs");
        Assert.Equal(0, JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
    }

    // ===========================================================================================
    // EVOLUTION-GIF-EXPORT-001 Boundary: over-cap requests are refused before storage.
    // ===========================================================================================

    [Fact]
    public async Task Gif_001_Boundary_over_cap_requests_are_rejected_before_anything_is_stored()
    {
        var scenario = await ScenarioAsync("gif_001_caps");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "上限动画");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 32);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/d1", ["desktop"], "First.");
        await CaptureAndSelectAsync(host, owner, second, "https://example.com/d2", ["desktop"], "Second.");

        // One frame is not an animation.
        using var single = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new { milestoneIds = new[] { first.ToString("D") } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, single.StatusCode);
        Assert.Equal("validation_failed", await ReadCodeAsync(single));

        // Thirteen milestones are more than the manifest stays reviewable for.
        var thirteen = Enumerable.Range(0, 13).Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        using var many = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new { milestoneIds = thirteen }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, many.StatusCode);

        // A flicker frame, a non-centisecond duration and a wrong count are all refused.
        foreach (var bad in new[] { new[] { 100, 1000 }, new[] { 505, 1000 }, new[] { 1000, 1000, 1000 } })
        {
            using var refused = await owner.SendAsync(await owner.Post(
                $"/api/projects/{project:D}/evolution/gifs",
                new
                {
                    milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                    durationsMs = bad,
                }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("validation_failed", await ReadCodeAsync(refused));
        }

        // A frame wider than the canvas ceiling is an oversized render, storing nothing.
        host.Provider.Reset();
        host.Provider.AnswerWithPngFor("wide", width: 1400, height: 100);
        var wide = await CreateConfirmedMilestoneAsync(owner, project, "v0.4", "2026-10-03T09:00:00Z");
        await CaptureAndSelectAsync(host, owner, wide, "https://example.com/wide", ["desktop"], "Wide.");

        using var large = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new { milestoneIds = new[] { first.ToString("D"), wide.ToString("D") } }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Equal("payload_too_large", await ReadCodeAsync(large));

        using var listed = await owner.GetAsync($"/api/projects/{project:D}/evolution/gifs");
        Assert.Equal(0, JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
    }

    // ===========================================================================================
    // EVOLUTION-GIF-EXPORT-001 Idempotency: same key replays, changed inputs conflict.
    // ===========================================================================================

    [Fact]
    public async Task Gif_001_Idempotent_renders_replay_and_changed_inputs_conflict()
    {
        var scenario = await ScenarioAsync("gif_001_idempotent");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "幂等动画");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 32);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/i1", ["desktop"], "First.");
        await CaptureAndSelectAsync(host, owner, second, "https://example.com/i2", ["desktop"], "Second.");

        var body = new
        {
            milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
            durationsMs = new[] { 1000, 1000 },
            idempotencyKey = "gif-replay-key",
        };

        using var created = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs", body));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();

        using var replayed = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs", body));
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        using var replayedDocument = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync());
        Assert.Equal(createdId, replayedDocument.RootElement.GetProperty("id").GetString());
        Assert.False(replayedDocument.RootElement.GetProperty("created").GetBoolean());

        using var conflicted = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                durationsMs = new[] { 2000, 2000 },
                idempotencyKey = "gif-replay-key",
            }));
        Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode);
        Assert.Equal("operation_idempotency_conflict", await ReadCodeAsync(conflicted));

        using var listed = await owner.GetAsync($"/api/projects/{project:D}/evolution/gifs");
        Assert.Equal(1, JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
    }

    // ===========================================================================================
    // EVOLUTION-GIF-EXPORT-002 Success: static preview is the first frame; draft is reviewable.
    // ===========================================================================================

    [Fact]
    public async Task Gif_002_Success_static_preview_is_the_first_frame_until_explicit_playback()
    {
        var scenario = await ScenarioAsync("gif_002_static");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "静态预览");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPngFor("alpha", width: 40, height: 24);
        host.Provider.AnswerWithPngFor("beta", width: 24, height: 40);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/alpha", ["desktop"], "Alpha.");
        await CaptureAndSelectAsync(host, owner, second, "https://example.com/beta", ["desktop"], "Beta.");

        using var rendered = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new { milestoneIds = new[] { first.ToString("D"), second.ToString("D") } }));
        var gifId = JsonDocument.Parse(await rendered.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        // The draft animation is reviewable before any approval.
        using var preview = await owner.GetAsync($"/api/evolution/gifs/{gifId}/preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/gif", preview.Content.Headers.ContentType?.MediaType);
        Assert.Equal("false", preview.Headers.GetValues("X-Evolution-Gif-Approval-Live").Single());

        var animation = GifProbe.Decode(await preview.Content.ReadAsByteArrayAsync());

        // The static preview is exactly the frame playback starts with: what a reduced-motion
        // owner sees is what approval saw, with no motion until explicit playback.
        using var still = await owner.GetAsync($"/api/evolution/gifs/{gifId}/preview?mode=static");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
        Assert.Equal("image/png", still.Content.Headers.ContentType?.MediaType);

        var staticBytes = await still.Content.ReadAsByteArrayAsync();
        Assert.True(EvolutionPng.TryDecodeToGrey(staticBytes, out var staticGrey) && staticGrey is not null);
        Assert.Equal(animation.Width, staticGrey.Width);
        Assert.Equal(animation.Height, staticGrey.Height);
        Assert.Equal(animation.Frames[0].Pixels, staticGrey.Pixels);

        using var unknown = await owner.GetAsync($"/api/evolution/gifs/{gifId}/preview?mode=flipbook");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
    }

    // ===========================================================================================
    // EVOLUTION-GIF-EXPORT-002: approval gates export; an edit requires renewed approval.
    // ===========================================================================================

    [Fact]
    public async Task Gif_002_Approval_gates_export_and_an_edit_after_approval_requires_renewal()
    {
        var scenario = await ScenarioAsync("gif_002_approve");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "批准动画");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 32);
        var firstShot = await CaptureAndSelectAsync(host, owner, first, "https://example.com/p1", ["desktop"], "Original wording.");
        await CaptureAndSelectAsync(host, owner, second, "https://example.com/p2", ["desktop"], "Steady.");

        using var rendered = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new { milestoneIds = new[] { first.ToString("D"), second.ToString("D") } }));
        using var manifest = JsonDocument.Parse(await rendered.Content.ReadAsStringAsync());
        var gifId = manifest.RootElement.GetProperty("id").GetString()!;
        var selectionHash = manifest.RootElement.GetProperty("selectionHash").GetString()!;
        var artifactHash = manifest.RootElement.GetProperty("artifactSha256").GetString()!;

        // A draft exports nothing: approval is the gate, not a label.
        using var premature = await owner.GetAsync($"/api/evolution/gifs/{gifId}/download");
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
        Assert.Equal("approval_required", await ReadCodeAsync(premature));

        // An approval of a hash nobody reviewed authorizes nothing.
        using var wrong = await owner.SendAsync(await owner.Post(
            $"/api/evolution/gifs/{gifId}/approve",
            new { expectedSelectionHash = new string('0', 64) }));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("revision_conflict", await ReadCodeAsync(wrong));

        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/evolution/gifs/{gifId}/approve",
            new { expectedSelectionHash = selectionHash }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("approved", JsonDocument.Parse(await approved.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());

        using var downloaded = await owner.GetAsync($"/api/evolution/gifs/{gifId}/download");
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal("image/gif", downloaded.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", downloaded.Content.Headers.ContentDisposition?.DispositionType ?? string.Empty);
        Assert.Equal($"\"{artifactHash}\"", downloaded.Headers.ETag?.ToString());

        var downloadedBytes = await downloaded.Content.ReadAsByteArrayAsync();
        Assert.Equal(artifactHash, Convert.ToHexString(SHA256.HashData(downloadedBytes)).ToLowerInvariant());

        // A caption edit moves the selection the render was approved against.
        var selected = (await ReadCandidatesAsync(owner, first)).Single(picture => picture.Id == firstShot.ScreenshotId);
        var evidenceRevision = await ReadEvidenceRevisionAsync(owner, first);
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

        using var reread = await owner.GetAsync($"/api/evolution/gifs/{gifId}");
        Assert.False(JsonDocument.Parse(await reread.Content.ReadAsStringAsync()).RootElement.GetProperty("selectionCurrent").GetBoolean());

        using var stale = await owner.GetAsync($"/api/evolution/gifs/{gifId}/download");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("approval_stale", await ReadCodeAsync(stale));

        // A reorder is a new draft with its own review hash, never an edit of the approved row.
        using var reordered = await owner.SendAsync(await owner.Post(
            $"/api/projects/{project:D}/evolution/gifs",
            new
            {
                milestoneIds = new[] { first.ToString("D"), second.ToString("D") },
                manualOrder = new[] { second.ToString("D"), first.ToString("D") },
            }));
        Assert.Equal(HttpStatusCode.Created, reordered.StatusCode);
        using var reorderedDocument = JsonDocument.Parse(await reordered.Content.ReadAsStringAsync());
        Assert.NotEqual(selectionHash, reorderedDocument.RootElement.GetProperty("selectionHash").GetString());
        Assert.Equal("draft", reorderedDocument.RootElement.GetProperty("status").GetString());
    }

    // ===========================================================================================
    // EVOLUTION-GIF-EXPORT-002 Boundary: a cancelled encoder stores nothing.
    // ===========================================================================================

    [Fact]
    public async Task Gif_002_Boundary_a_cancelled_encoder_stores_no_partial_artifact()
    {
        var scenario = await ScenarioAsync("gif_002_cancel");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "取消动画");
        var first = await CreateConfirmedMilestoneAsync(owner, project, "v0.1", "2026-09-01T09:00:00Z");
        var second = await CreateConfirmedMilestoneAsync(owner, project, "v0.2", "2026-10-01T09:00:00Z");

        host.Provider.Reset();
        host.Provider.AnswerWithPng(width: 32, height: 32);
        await CaptureAndSelectAsync(host, owner, first, "https://example.com/x1", ["desktop"], "First.");
        await CaptureAndSelectAsync(host, owner, second, "https://example.com/x2", ["desktop"], "Second.");

        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IEvolutionGifService>();
        var caller = new OwnerScope(InstallationId, Guid.NewGuid());

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RenderAsync(
            caller,
            project,
            new RenderEvolutionGifRequest(
                [first, second],
                selectedScreenshotIds: null,
                manualOrder: null,
                durationsMs: null,
                loop: false,
                expectedSelectionHash: null,
                idempotencyKey: "gif-cancelled"),
            cancelled.Token));

        using var listed = await owner.GetAsync($"/api/projects/{project:D}/evolution/gifs");
        Assert.Equal(0, JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());

        // The codec refuses a dead token before writing a byte, and encodes deterministically.
        var frames = new[] { new GreyImage(4, 4, new byte[16]), new GreyImage(4, 4, new byte[16]) };
        Assert.Throws<OperationCanceledException>(() =>
            EvolutionGifCodec.Encode(frames, [1000, 1000], loop: false, cancelled.Token));
        Assert.Equal(
            EvolutionGifCodec.Encode(frames, [1000, 1000], loop: true),
            EvolutionGifCodec.Encode(frames, [1000, 1000], loop: true));
        Assert.False(EvolutionGifCodec.TryDecodeFirstFrame("not a gif"u8.ToArray(), out _, out _, out _));
    }

    // ===========================================================================================
    // Migration and schema: the GIF table, its bounds and its vocabulary.
    // ===========================================================================================

    [Fact]
    public async Task The_gif_table_index_and_vocabulary_come_from_the_forward_migration_and_readiness_requires_it()
    {
        var database = await postgres.CreateDatabaseAsync("gif_migration");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        Assert.Contains(ChronicleafSchema.EvolutionGifExportMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));

        var missing = ChronicleafSchema.MissingMigrationIds(
            ChronicleafSchema.RequiredMigrationIds
                .Where(id => id != ChronicleafSchema.EvolutionGifExportMigrationId)
                .ToArray());
        Assert.Equal([ChronicleafSchema.EvolutionGifExportMigrationId], missing);

        Assert.Equal(
            "evolution_gifs",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT tablename FROM pg_tables WHERE tablename = 'evolution_gifs'"));

        Assert.Equal(
            "ux_evolution_gifs_scope_id_project_id_idempotency_key",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT indexname FROM pg_indexes WHERE tablename = 'evolution_gifs' " +
                "AND indexname = 'ux_evolution_gifs_scope_id_project_id_idempotency_key'"));

        Assert.Equal(
            "ck_evolution_gifs_status",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT conname::text FROM pg_constraint WHERE conname = 'ck_evolution_gifs_status'"));

        Assert.Equal(
            "ck_evolution_gifs_frame_count",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT conname::text FROM pg_constraint WHERE conname = 'ck_evolution_gifs_frame_count'"));

        Assert.Equal(
            "ck_evolution_gifs_total_duration_ms",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT conname::text FROM pg_constraint WHERE conname = 'ck_evolution_gifs_total_duration_ms'"));
    }

    // ===========================================================================================
    // Helpers.
    // ===========================================================================================

    private static void AssertFramePixels(byte[] canvas, int canvasWidth, int canvasHeight, byte[] sourcePng)
    {
        Assert.True(EvolutionPng.TryDecodeToGrey(sourcePng, out var expected) && expected is not null);

        var left = (canvasWidth - expected.Width) / 2;
        var top = (canvasHeight - expected.Height) / 2;

        for (var row = 0; row < expected.Height; row++)
        {
            for (var column = 0; column < expected.Width; column++)
            {
                Assert.Equal(
                    expected.Pixels[row * expected.Width + column],
                    canvas[(top + row) * canvasWidth + left + column]);
            }
        }
    }

    private async Task<Scenario> ScenarioAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        var mediaRoot = Path.Combine(Path.GetTempPath(), $"chronicleaf-gif-{Guid.NewGuid():N}");
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

    private sealed record Scenario(string Database, CaptureApiHost Host, string MediaRoot);

    private sealed record CandidateView(
        Guid Id,
        Guid MediaId,
        string SourceUrl,
        string Viewport,
        string Status,
        string Caption,
        long Revision);

    // ===========================================================================================
    // GifProbe: an independent GIF89a reader for these tests.
    // ===========================================================================================

    private sealed record DecodedGifFrame(byte[] Pixels, int DelayCs);

    private sealed record DecodedGif(int Width, int Height, bool Loop, IReadOnlyList<DecodedGifFrame> Frames);

    /// <summary>
    /// Reads a GIF89a animation without touching the production codec: header, global table,
    /// the loop extension, every frame's delay and descriptor, and the LZW stream itself.
    /// </summary>
    private static class GifProbe
    {
        public static DecodedGif Decode(byte[] gif)
        {
            Assert.True(gif.Length > 13);
            Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(gif, 0, 6));

            var width = BinaryPrimitives.ReadUInt16LittleEndian(gif.AsSpan(6, 2));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(gif.AsSpan(8, 2));
            Assert.True(width > 0 && height > 0);

            var offset = 13;
            if ((gif[10] & 0x80) != 0)
            {
                offset += 3 * (1 << ((gif[10] & 0x07) + 1));
            }

            var loop = false;
            var pendingDelay = 0;
            var frames = new List<DecodedGifFrame>();

            while (true)
            {
                Assert.True(offset < gif.Length);
                var separator = gif[offset++];

                if (separator == 0x3B)
                {
                    break;
                }

                if (separator == 0x21)
                {
                    var label = gif[offset++];
                    if (label == 0xF9)
                    {
                        Assert.Equal(4, gif[offset++]);
                        offset++;
                        pendingDelay = BinaryPrimitives.ReadUInt16LittleEndian(gif.AsSpan(offset, 2));
                        offset += 3;
                        Assert.Equal(0, gif[offset++]);
                    }
                    else
                    {
                        if (label == 0xFF)
                        {
                            var size = gif[offset++];
                            var tag = System.Text.Encoding.ASCII.GetString(gif, offset, size);
                            offset += size;
                            if (tag == "NETSCAPE2.0")
                            {
                                loop = true;
                            }
                        }

                        SkipSubBlocks(gif, ref offset);
                    }

                    continue;
                }

                Assert.Equal(0x2C, separator);
                var left = BinaryPrimitives.ReadUInt16LittleEndian(gif.AsSpan(offset, 2));
                var top = BinaryPrimitives.ReadUInt16LittleEndian(gif.AsSpan(offset + 2, 2));
                var frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(gif.AsSpan(offset + 4, 2));
                var frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(gif.AsSpan(offset + 6, 2));
                var packed = gif[offset + 8];
                offset += 9;

                Assert.Equal(0, left);
                Assert.Equal(0, top);
                Assert.Equal(width, frameWidth);
                Assert.Equal(height, frameHeight);
                Assert.Equal(0, packed & 0x80);

                var minCodeSize = gif[offset++];
                var data = ReadSubBlocks(gif, ref offset);
                var pixels = DecompressLzw(data, minCodeSize, width * height);

                frames.Add(new DecodedGifFrame(pixels, pendingDelay));
                pendingDelay = 0;
            }

            Assert.True(frames.Count >= 2);
            return new DecodedGif(width, height, loop, frames);
        }

        private static void SkipSubBlocks(byte[] gif, ref int offset)
        {
            while (true)
            {
                var size = gif[offset++];
                if (size == 0)
                {
                    return;
                }

                offset += size;
            }
        }

        private static byte[] ReadSubBlocks(byte[] gif, ref int offset)
        {
            using var blocks = new MemoryStream();
            while (true)
            {
                var size = gif[offset++];
                if (size == 0)
                {
                    return blocks.ToArray();
                }

                blocks.Write(gif, offset, size);
                offset += size;
            }
        }

        private static byte[] DecompressLzw(byte[] data, int minCodeSize, int expected)
        {
            var clear = 1 << minCodeSize;
            var end = clear + 1;
            var prefixes = new int[4096];
            var suffixes = new byte[4096];
            for (var code = 0; code < clear; code++)
            {
                suffixes[code] = (byte)code;
            }

            var codeSize = minCodeSize + 1;
            var next = end + 1;
            var bit = 0;
            int ReadCode()
            {
                var value = 0;
                for (var shift = 0; shift < codeSize; shift++)
                {
                    value |= ((data[bit >> 3] >> (bit & 7)) & 1) << shift;
                    bit++;
                }

                return value;
            }

            var pixels = new List<byte>(expected);
            var previous = -1;
            var first = (byte)0;
            var stack = new Stack<byte>();

            while (true)
            {
                var code = ReadCode();
                Assert.True(code >= 0);

                if (code == clear)
                {
                    codeSize = minCodeSize + 1;
                    next = end + 1;
                    previous = -1;
                    continue;
                }

                if (code == end)
                {
                    break;
                }

                var current = code;
                if (code >= next)
                {
                    Assert.True(previous >= 0);
                    stack.Push(first);
                    current = previous;
                }

                while (current >= clear)
                {
                    stack.Push(suffixes[current]);
                    current = prefixes[current];
                }

                first = suffixes[current];
                stack.Push(first);

                while (stack.Count > 0)
                {
                    pixels.Add(stack.Pop());
                }

                if (previous >= 0 && next < 4096)
                {
                    prefixes[next] = previous;
                    suffixes[next] = first;
                    next++;
                    // The decoder trails the encoder by one table entry, so it widens its
                    // codes one entry earlier than the encoder did: when next reaches 2^size.
                    if (next == (1 << codeSize) && codeSize < 12)
                    {
                        codeSize++;
                    }
                }

                previous = code;
            }

            Assert.Equal(expected, pixels.Count);
            return [.. pixels];
        }
    }
}
