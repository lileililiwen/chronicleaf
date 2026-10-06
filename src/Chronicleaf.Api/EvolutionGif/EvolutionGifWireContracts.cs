using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronicleaf.Api.EvolutionGifs;

/// <summary>
/// The body a GIF render POST carries (EVOLUTION-GIF-EXPORT-001).
/// </summary>
public sealed class RenderEvolutionGifBody
{
    [JsonPropertyName("milestoneIds")]
    public List<string>? MilestoneIds { get; set; }

    [JsonPropertyName("selectedScreenshotIds")]
    public List<string>? SelectedScreenshotIds { get; set; }

    [JsonPropertyName("manualOrder")]
    public List<string>? ManualOrder { get; set; }

    [JsonPropertyName("durationsMs")]
    public List<int>? DurationsMs { get; set; }

    [JsonPropertyName("loop")]
    public bool? Loop { get; set; }

    [JsonPropertyName("expectedSelectionHash")]
    public string? ExpectedSelectionHash { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// The body a GIF approve POST carries (EVOLUTION-GIF-EXPORT-002).
/// </summary>
public sealed class ApproveEvolutionGifBody
{
    [JsonPropertyName("expectedSelectionHash")]
    public string? ExpectedSelectionHash { get; set; }
}

/// <summary>
/// One rendered animation, as the evolution GIF routes publish it
/// (EVOLUTION-GIF-EXPORT-001/002).
/// </summary>
/// <remarks>
/// Field names are pinned explicitly, as every other package's are, so the contract cannot drift
/// with serializer policy. The manifest cites the media and caption revisions every frame was
/// built from, in canvas order, with the playback the review authorized; the GIF itself lives
/// behind the preview and download routes, so this shape never carries pixels, a storage key or
/// a scope.
/// </remarks>
public sealed record EvolutionGifResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("manifest")] JsonDocument Manifest,
    [property: JsonPropertyName("selectionHash")] string SelectionHash,
    [property: JsonPropertyName("artifactSha256")] string ArtifactSha256,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("frameCount")] int FrameCount,
    [property: JsonPropertyName("totalDurationMs")] int TotalDurationMs,
    [property: JsonPropertyName("loop")] bool Loop,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("selectionCurrent")] bool SelectionCurrent,
    [property: JsonPropertyName("created")] bool Created);
