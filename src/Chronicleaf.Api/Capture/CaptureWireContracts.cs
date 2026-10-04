using System.Globalization;
using System.Text.Json.Serialization;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Capture;

/// <summary>
/// The wire shape of one capture candidate (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// <para>
/// Field names are pinned explicitly, as the access, milestone and GitHub packages' are, so the
/// contract cannot drift with serializer policy.
/// </para>
/// <para>
/// <b>What a client is told:</b> the candidate's id, the media id that addresses the picture through
/// the delivered media route, where the capture was asked for and where the page actually ended up,
/// the viewport, and when the bytes were produced. <b>What it is deliberately not told:</b> the
/// storage key, the byte length, the digest, the scope - all of which belong to the media record the
/// delivered route already publishes, and repeating them here would be a second copy of storage state
/// that could disagree with the first.
/// </para>
/// <para>
/// <b>The curation fields are published as the constants they are.</b> <c>status</c>,
/// <c>isPrimary</c> and <c>caption</c> are here so a client can render the difference between "offered"
/// and "chosen" the moment screenshot-curation starts choosing, without a contract change. Today they
/// always say candidate, false and the empty string, and a client that showed anything else would be
/// reading a field this package cannot set.
/// </para>
/// </remarks>
public sealed record ScreenshotCandidateResponse(
    [property: JsonPropertyName("id")] string Id,
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
    [property: JsonPropertyName("revision")] long Revision)
{
    public static ScreenshotCandidateResponse From(ScreenshotCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return new ScreenshotCandidateResponse(
            candidate.Id.ToString("D"),
            candidate.MediaId.ToString("D"),
            candidate.ProjectId.ToString("D"),
            candidate.SourceUrl,
            candidate.FinalUrl,
            candidate.Viewport.Name,
            candidate.Viewport.Width,
            candidate.Viewport.Height,
            Stamp(candidate.CapturedAt),
            candidate.Status,
            candidate.Caption,
            candidate.IsPrimary,
            candidate.Revision);
    }

    internal static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// The wire shape of a candidate list (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// An object rather than a bare array so a client is not left inferring emptiness from a length. A
/// milestone with nothing captured is <c>{"items": [], "mediaBaseUrl": "/api/media"}</c>, which is a
/// state it can render as an honest empty result rather than as a failed read.
/// </remarks>
public sealed record ScreenshotCandidateListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<ScreenshotCandidateResponse> Items,
    [property: JsonPropertyName("mediaBaseUrl")] string MediaBaseUrl)
{
    /// <summary>
    /// The delivered media route, published rather than left to be built.
    /// </summary>
    /// <remarks>
    /// A client needs the image and this package does not serve it: the bytes live behind the delivered
    /// media store, which decides what is readable. Naming the route here means a panel can show a
    /// candidate's picture without inventing a path, and without this package growing a way to hand out
    /// stored bytes.
    /// </remarks>
    public const string MediaRoute = "/api/media";
}

/// <summary>
/// The body a capture is asked for (SCREENSHOT-CAPTURE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The design's exact shape. <see cref="Url"/> is the one http or https URL to photograph and
/// <see cref="Viewports"/> names one or both of the two viewports, so a request for both is two
/// candidates of the same page rather than two requests.
/// </para>
/// <para>
/// <b>This is the only route in the repository whose body names a host the server will then
/// connect to</b>, and it is behind the same owner boundary as everything else. What makes it safe is
/// not the boundary - it is the URL policy in Chronicleaf.Core, which decides where a capture may go,
/// is applied before the job exists, and is applied again to the address the name resolves to
/// immediately before every connection. A host named here is a host the policy has already agreed may
/// be visited, or the request is refused with 403 and no browser is started.
/// </para>
/// <para>
/// The evidence revision travels in the body and as <c>If-Match</c>, and the server refuses a body
/// that disagrees with the header rather than picking one. The idempotency key is the caller's: a
/// repeat of the same key returns the job the first request created instead of photographing the same
/// pages twice.
/// </para>
/// </remarks>
public sealed record CaptureRequestBody(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("viewports")] IReadOnlyList<string>? Viewports,
    [property: JsonPropertyName("waitSelector")] string? WaitSelector,
    [property: JsonPropertyName("waitMs")] int? WaitMs,
    [property: JsonPropertyName("fullPage")] bool? FullPage,
    [property: JsonPropertyName("expectedBundleRevision")] long? ExpectedBundleRevision,
    [property: JsonPropertyName("idempotencyKey")] string? IdempotencyKey)
{
    /// <summary>
    /// The request a body that did not arrive produces: every field empty, so an absent body is refused
    /// by name rather than being read as "no URL, no viewports" and matched against a milestone that
    /// happens to have no evidence yet.
    /// </summary>
    public static CaptureRequestBody Empty { get; } = new(
        Url: null,
        Viewports: null,
        WaitSelector: null,
        WaitMs: null,
        FullPage: null,
        ExpectedBundleRevision: null,
        IdempotencyKey: null);
}

/// <summary>
/// The answer to a capture request: the job that will perform it, and where to read its progress
/// (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// A 202 and an operation id, never a candidate. A capture is bounded network and browser work with a
/// 45-second limit attached, so it happens in the delivered queue and this response points at the
/// delivered operation route. <c>created</c> says whether this request created the job or replayed one,
/// which is the difference between "it is running" and "it is already running".
/// </remarks>
public sealed record CaptureAcceptedResponse(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("operationUrl")] string OperationUrl,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created")] bool Created);
