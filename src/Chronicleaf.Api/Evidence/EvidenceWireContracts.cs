using System.Globalization;
using System.Text.Json.Serialization;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Evidence;

/// <summary>
/// One collected fact as the API returns it (MILESTONE-EVIDENCE-001).
/// </summary>
/// <remarks>
/// The whole item is returned, including the collected text, because the owner of this installation
/// is the reader and the point of the record is what was actually collected. <c>availability</c>
/// travels with it and is never dropped, so a reader cannot mistake a recorded gap for a payload.
/// </remarks>
public sealed record EvidenceItemResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sourceRef")] string SourceRef,
    [property: JsonPropertyName("sourceRevision")] string SourceRevision,
    [property: JsonPropertyName("capturedAt")] string CapturedAt,
    [property: JsonPropertyName("payloadSha256")] string PayloadSha256,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("mediaId")] string? MediaId,
    [property: JsonPropertyName("redactions")] IReadOnlyList<string> Redactions,
    [property: JsonPropertyName("availability")] string Availability,
    [property: JsonPropertyName("supersedesId")] string? SupersedesId)
{
    public static EvidenceItemResponse From(EvidenceItem item) =>
        new(
            item.Id.ToString("D"),
            item.Kind,
            item.SourceRef,
            item.SourceRevision,
            item.CapturedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            item.PayloadSha256,
            item.Text,
            item.MediaId?.ToString("D"),
            item.Redactions,
            item.Availability,
            item.SupersedesId?.ToString("D"));
}

/// <summary>One immutable snapshot of a milestone's evidence, with its revision.</summary>
public sealed record EvidenceBundleResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("milestoneId")] string MilestoneId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("items")] IReadOnlyList<EvidenceItemResponse> Items)
{
    public static EvidenceBundleResponse From(EvidenceBundle bundle) =>
        new(
            bundle.Id.ToString("D"),
            bundle.MilestoneId.ToString("D"),
            bundle.Revision,
            bundle.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            [.. bundle.Items.Select(EvidenceItemResponse.From)]);
}

/// <summary>
/// The evidence list: the bundles newest first, plus the milestone's current revision.
/// </summary>
/// <remarks>
/// <c>revision</c> is the token a mutation has to present, and it is 0 for a milestone that has no
/// evidence at all. Reporting it here is what lets a client build the append affordance for an
/// empty milestone without inventing a revision, and it is why the empty state is honest: a client
/// knows there is nothing recorded because this says the revision is 0, not because a list came back
/// short.
/// </remarks>
public sealed record EvidenceListResponse(
    [property: JsonPropertyName("milestoneId")] string MilestoneId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("items")] IReadOnlyList<EvidenceBundleResponse> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor)
{
    public static EvidenceListResponse From(Guid milestoneId, EvidencePage page) =>
        new(
            milestoneId.ToString("D"),
            page.Bundles.Count == 0 ? 0 : page.Bundles[0].Revision,
            [.. page.Bundles.Select(EvidenceBundleResponse.From)],
            page.NextCursor);
}

/// <summary>
/// The <c>POST /api/milestones/{id}/evidence/manual</c> body: what the owner states by hand
/// (MILESTONE-EVIDENCE-002).
/// </summary>
/// <remarks>
/// There is no <c>kind</c>, no <c>availability</c> and no <c>sourceRef</c> field here, and that is the
/// design rather than an omission: a request cannot claim to be a commit, cannot claim a source was
/// collected completely, and cannot name where it came from. The service decides all three from what
/// the owner actually wrote.
/// </remarks>
public sealed record ManualEvidenceRequestBody(
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("knownLimitations")] IReadOnlyList<string>? KnownLimitations,
    [property: JsonPropertyName("nextSteps")] IReadOnlyList<string>? NextSteps,
    [property: JsonPropertyName("expectedRevision")] long? ExpectedRevision);

/// <summary>
/// Turns the request shapes into application inputs and application results back into responses, so
/// the application layer stays free of HTTP.
/// </summary>
public static class EvidenceMapping
{
    /// <summary>
    /// Reads the revision a mutation is conditional on. The header is the precondition, and the
    /// body's <c>expectedRevision</c> has to agree with it.
    /// </summary>
    /// <remarks>
    /// Two answers to one question would let a client act on a different revision than the one it
    /// read, and the failure would only surface as a conflict after a request that looked
    /// consistent. So the header is required (428) and authoritative, and a body that names a
    /// different revision is a 422 naming <c>expectedRevision</c> - the request is refused rather
    /// than resolved in favour of either.
    /// </remarks>
    public static long RequiredRevision(HttpContext context, long? bodyRevision)
    {
        ArgumentNullException.ThrowIfNull(context);

        var presented = context.Request.Headers.IfMatch.ToString().Trim();

        if (presented.Length == 0)
        {
            throw new IfMatchRequiredException("milestone's evidence");
        }

        if (presented.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            presented = presented[2..];
        }

        presented = presented.Trim().Trim('"');

        if (!long.TryParse(presented, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
        {
            throw new ValidationFailedException(
                "If-Match",
                "If-Match must be the evidence revision you read, as a decimal number, optionally quoted or weakly marked.");
        }

        if (bodyRevision is not null && bodyRevision.Value != revision)
        {
            throw new ValidationFailedException(
                "expectedRevision",
                "The expected revision must be the one presented in If-Match.");
        }

        return revision;
    }

    /// <summary>Builds the manual input, with the revision the client read.</summary>
    public static ManualEvidenceRequest ToManual(ManualEvidenceRequestBody? request, long expectedRevision) =>
        new(
            request?.Notes,
            request?.KnownLimitations,
            request?.NextSteps,
            expectedRevision);
}
