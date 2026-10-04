using System.Text.Json.Serialization;
using Chronicleaf.Application.Curation;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Curation;

/// <summary>
/// One captured picture with its curation state, as the curation routes publish it
/// (SCREENSHOT-CURATION-001/002).
/// </summary>
/// <remarks>
/// The same collection the capture list publishes, with the columns curation moves. Field names
/// are pinned explicitly, as every other package's are, so the contract cannot drift with
/// serializer policy. The bytes live behind the delivered media route, whose base the list
/// publishes: this shape never carries an image, a storage key or a scope.
/// </remarks>
public sealed record CuratedScreenshotResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("milestoneId")] string MilestoneId,
    [property: JsonPropertyName("mediaId")] string MediaId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sourceUrl")] string SourceUrl,
    [property: JsonPropertyName("finalUrl")] string FinalUrl,
    [property: JsonPropertyName("viewport")] string Viewport,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("capturedAt")] string CapturedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("caption")] string Caption,
    [property: JsonPropertyName("isPrimary")] bool IsPrimary,
    [property: JsonPropertyName("displayOrder")] int DisplayOrder,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("bundleRevision")] long BundleRevision)
{
    public static CuratedScreenshotResponse From(CuratedScreenshot screenshot, long bundleRevision) =>
        new(
            screenshot.Id.ToString("D"),
            screenshot.MilestoneId.ToString("D"),
            screenshot.MediaId.ToString("D"),
            screenshot.ProjectId.ToString("D"),
            screenshot.SourceUrl,
            screenshot.FinalUrl,
            screenshot.Viewport.Name,
            screenshot.Viewport.Width,
            screenshot.Viewport.Height,
            screenshot.CapturedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture),
            screenshot.Status,
            screenshot.Caption,
            screenshot.IsPrimary,
            screenshot.DisplayOrder,
            screenshot.Revision,
            bundleRevision);
}

/// <summary>
/// The body a curation PATCH carries (SCREENSHOT-CURATION-001/002).
/// </summary>
/// <remarks>
/// Every change field is optional: a PATCH names what it wants changed and leaves the rest.
/// <see cref="ExpectedRevision"/> must agree with the If-Match header when both are present, the
/// same rule the delivered evidence routes enforce. <see cref="ExpectedBundleRevision"/> is the
/// evidence revision the owner read, and is required because a selection is published into that
/// evidence: curating against a bundle that has already moved is a 409, not a silent rebase.
/// </remarks>
public sealed record CurateScreenshotBody(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("caption")] string? Caption,
    [property: JsonPropertyName("isPrimary")] bool? IsPrimary,
    [property: JsonPropertyName("expectedRevision")] long? ExpectedRevision,
    [property: JsonPropertyName("expectedBundleRevision")] long? ExpectedBundleRevision);

/// <summary>
/// The body the order POST carries (SCREENSHOT-CURATION-002).
/// </summary>
/// <remarks>
/// The ids must name exactly the milestone's currently selected pictures, once each. The bundle
/// revision is required for the same reason it is on the PATCH: an order written against evidence
/// its owner never saw could drop a picture somebody else just selected.
/// </remarks>
public sealed record OrderScreenshotsBody(
    [property: JsonPropertyName("ids")] IReadOnlyList<string>? Ids,
    [property: JsonPropertyName("expectedBundleRevision")] long? ExpectedBundleRevision);

/// <summary>
/// The answer to an order POST: the selected pictures in their stored order
/// (SCREENSHOT-CURATION-002).
/// </summary>
public sealed record ScreenshotOrderResponse(
    [property: JsonPropertyName("milestoneId")] string MilestoneId,
    [property: JsonPropertyName("ids")] IReadOnlyList<string> Ids,
    [property: JsonPropertyName("bundleRevision")] long BundleRevision);

/// <summary>
/// Turns the curation request shapes into application inputs, so the application layer stays free
/// of HTTP.
/// </summary>
public static class CurationMapping
{
    /// <summary>Builds the curate input, with the revision the client read.</summary>
    public static ScreenshotCurateRequest ToCurate(CurateScreenshotBody? request, long expectedRevision) =>
        new(
            request?.Status,
            request?.Caption,
            request?.IsPrimary,
            expectedRevision,
            request?.ExpectedBundleRevision ?? -1);

    /// <summary>Builds the order input, refusing anything that is not exactly an id list.</summary>
    public static ScreenshotOrderRequest ToOrder(Guid milestoneId, OrderScreenshotsBody? request)
    {
        _ = milestoneId;

        if (request?.ExpectedBundleRevision is null)
        {
            throw new Application.Common.ValidationFailedException(
                "expectedBundleRevision",
                "The expected bundle revision is required.");
        }

        var ids = new List<Guid>();

        foreach (var raw in request.Ids ?? [])
        {
            if (!Guid.TryParse(raw, out var id) || id == Guid.Empty)
            {
                throw new Application.Common.ValidationFailedException(
                    "ids",
                    "Each ordered entry is a screenshot id.");
            }

            ids.Add(id);
        }

        return new ScreenshotOrderRequest(ids, request.ExpectedBundleRevision.Value);
    }
}
