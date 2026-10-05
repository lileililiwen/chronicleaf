using System.Text.Json.Serialization;

namespace Chronicleaf.Api.GitHubEnrichment;

/// <summary>
/// The body an enrichment is asked for (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// The evidence revision travels in the body and as <c>If-Match</c>, and the server refuses a body
/// that disagrees with the header rather than picking one. The idempotency key is the caller's: a
/// repeat of the same key returns the job the first request created instead of starting a second
/// enrichment of the same evidence. No refs, no branches and no SHAs travel here: the SHAs an
/// enrichment queries are the ones the milestone's own bundle already carries.
/// </remarks>
public sealed record GitHubEnrichmentWireRequest(
    [property: JsonPropertyName("expectedBundleRevision")] long? ExpectedBundleRevision,
    [property: JsonPropertyName("idempotencyKey")] string? IdempotencyKey)
{
    /// <summary>
    /// The request a body that did not arrive produces: every field empty and the revision absent,
    /// so an empty body is refused by name rather than being read as "revision zero" and matching
    /// a milestone that happens to have no evidence yet.
    /// </summary>
    public static GitHubEnrichmentWireRequest Empty { get; } = new(
        ExpectedBundleRevision: null,
        IdempotencyKey: null);
}

/// <summary>
/// The answer to an enrichment request: the job that will perform it, and where to read its
/// progress (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// A 202 and an operation id, never a bundle. The enrichment is a bounded set of network reads
/// with a rate limit attached, so the work happens in the delivered queue and this response points
/// at the delivered operation route. <c>operationUrl</c> is included so a client does not have to
/// build the path itself, and <c>created</c> says whether this request created the job or replayed
/// one.
/// </remarks>
public sealed record GitHubEnrichmentAcceptedResponse(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("operationUrl")] string OperationUrl,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created")] bool Created);
