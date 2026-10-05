using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronicleaf.Api.Evolution;

/// <summary>
/// The body a render POST carries (SCREENSHOT-EVOLUTION-IMAGES-001).
/// </summary>
public sealed class RenderEvolutionBody
{
    [JsonPropertyName("milestoneIds")]
    public List<string>? MilestoneIds { get; set; }

    [JsonPropertyName("layout")]
    public string? Layout { get; set; }

    [JsonPropertyName("selectedScreenshotIds")]
    public List<string>? SelectedScreenshotIds { get; set; }

    [JsonPropertyName("manualOrder")]
    public List<string>? ManualOrder { get; set; }

    [JsonPropertyName("expectedSelectionHash")]
    public string? ExpectedSelectionHash { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// The body an approve POST carries (SCREENSHOT-EVOLUTION-IMAGES-002).
/// </summary>
public sealed class ApproveEvolutionBody
{
    [JsonPropertyName("expectedSelectionHash")]
    public string? ExpectedSelectionHash { get; set; }
}

/// <summary>
/// One rendered comparison, as the evolution routes publish it
/// (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
/// <remarks>
/// Field names are pinned explicitly, as every other package's are, so the contract cannot drift
/// with serializer policy. The manifest cites the media and caption revisions every cell was
/// built from; the PNG itself lives behind the preview and download routes, so this shape never
/// carries pixels, a storage key or a scope.
/// </remarks>
public sealed record EvolutionImageResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("layout")] string Layout,
    [property: JsonPropertyName("manifest")] JsonDocument Manifest,
    [property: JsonPropertyName("selectionHash")] string SelectionHash,
    [property: JsonPropertyName("artifactSha256")] string ArtifactSha256,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("selectionCurrent")] bool SelectionCurrent,
    [property: JsonPropertyName("created")] bool Created);
