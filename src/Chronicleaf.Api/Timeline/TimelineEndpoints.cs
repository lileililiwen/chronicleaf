using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Timeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Timeline;

/// <summary>
/// The private timeline route (MILESTONE-TIMELINE-001, MILESTONE-TIMELINE-002).
/// </summary>
/// <remarks>
/// <para>
/// One route, private: it is not marked public, so the access package's default-deny boundary
/// already covers it and the public allow-list stays exactly as the design fixed it. The timeline
/// is always the owner's own history; there is no public projection here, because making one is
/// the journey package's opt-in decision rather than this read's.
/// </para>
/// <para>
/// <b>The cursor is bound to this project and this filter.</b> A cursor issued for another
/// project, another scope or the other candidate filter is refused with a 422 naming
/// <c>cursor</c>, never applied to the wrong history.
/// </para>
/// </remarks>
public static class TimelineEndpoints
{
    public static IEndpointRouteBuilder MapTimelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/projects/{projectId:guid}/timeline", GetTimelineAsync).RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> GetTimelineAsync(
        Guid projectId,
        HttpContext context,
        ITimelineService timeline,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var page = await timeline
            .GetTimelineAsync(
                owner,
                projectId,
                new TimelineRequest(ReadLimit(context), context.Request.Query["cursor"], ReadIncludeCandidates(context)),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(TimelineResponse.From(page));
    }

    /// <summary>
    /// The scope and the identity this request acts under. Both come from the access package, never
    /// from a request body: a request cannot influence whose history it reads.
    /// </summary>
    private static async Task<OwnerScope> OwnerScopeAsync(
        HttpContext context,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var identity = await access.EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);

        return new OwnerScope(identity.InstallationId, session.ActorId);
    }

    private static int? ReadLimit(HttpContext context)
    {
        var raw = context.Request.Query["limit"].ToString();

        return int.TryParse(raw, out var limit) ? limit : null;
    }

    private static bool ReadIncludeCandidates(HttpContext context)
    {
        var raw = context.Request.Query["includeCandidates"].ToString();

        if (raw.Length == 0)
        {
            return false;
        }

        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "1", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "0", StringComparison.Ordinal))
        {
            return false;
        }

        throw new ValidationFailedException(
            "includeCandidates",
            "includeCandidates must be true or false.");
    }
}

/// <summary>One timeline row: what happened, when, and whether the owner has confirmed it.</summary>
public sealed record TimelineItemResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("occurredAt")] string OccurredAt,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status)
{
    public static TimelineItemResponse From(TimelineItem item) =>
        new(
            item.Id.ToString("D"),
            item.Title,
            item.Version,
            item.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            item.Type,
            item.Status);
}

public sealed record TimelineResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<TimelineItemResponse> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor)
{
    public static TimelineResponse From(TimelinePage page) =>
        new([.. page.Items.Select(TimelineItemResponse.From)], page.NextCursor);
}
