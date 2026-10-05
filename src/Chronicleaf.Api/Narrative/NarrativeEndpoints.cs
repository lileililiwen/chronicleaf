using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Api.Operations;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Narrative;
using Chronicleaf.Application.Operations;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Narrative;

/// <summary>
/// The private narrative routes (NARRATIVE-MEMORY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Four routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. Narrative history is readable only by the owner it belongs to and by nobody else.
/// </para>
/// <para>
/// <b>The edit is conditional.</b> If-Match carries the entry revision the owner read and the body
/// carries the same value, and both must be current: a stale writer loses rather than merging,
/// because a merged history would be a history nobody wrote. A missing If-Match is 428, a stale
/// revision of either kind is 409, and a body that disagrees with the header is 422.
/// </para>
/// <para>
/// <b>The refresh route answers 202, not a summary.</b> A refresh re-reads every source and
/// rebuilds the summary; it runs in the delivered queue and this route answers with the operation
/// id plus the delivered operation route to read it on. A replay under the same idempotency key
/// answers with the same job and <c>created: false</c>.
/// </para>
/// <para>
/// <b>No route here can approve anything.</b> There is no approve verb, and the summary a refresh
/// commits is a generated document bound to its input hash - not a human decision, and never a
/// substitute for the review package's approval.
/// </para>
/// </remarks>
public static class NarrativeEndpoints
{
    public static IEndpointRouteBuilder MapNarrativeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/projects/{projectId:guid}/narrative", ReadNarrativeAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/projects/{projectId:guid}/narrative", CreateEntryAsync)
            .RequireOwnerSession();

        endpoints
            .MapPatch("/api/projects/{projectId:guid}/narrative/{entryId:guid}", PatchEntryAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/projects/{projectId:guid}/narrative/refresh", RefreshSummaryAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> ReadNarrativeAsync(
        Guid projectId,
        HttpContext context,
        INarrativeService narrative,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var limit = ListLimit(context);

        var (entries, summary) = await narrative
            .ReadNarrativeAsync(owner, projectId, limit, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(new NarrativeResponse(
            [.. entries.Items.Select(NarrativeEntryResponse.From)],
            entries.NextCursor,
            summary is null ? null : ProjectSummaryResponse.From(summary)));
    }

    private static async Task<IResult> CreateEntryAsync(
        Guid projectId,
        HttpContext context,
        CreateNarrativeEntryBody? request,
        INarrativeService narrative,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new ValidationFailedException(
                "text",
                "An entry carries the owner's own words, so write some.");
        }

        var created = await narrative
            .CreateEntryAsync(
                owner,
                projectId,
                new CreateNarrativeEntryInput(
                    request.Kind ?? string.Empty,
                    request.OccurredAt ?? DateTimeOffset.UtcNow,
                    request.Text ?? string.Empty,
                    request.SourceEvidenceIds ?? [],
                    request.Visibility ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{created.Revision.ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(
            NarrativeEntryResponse.From(created),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> PatchEntryAsync(
        Guid projectId,
        Guid entryId,
        HttpContext context,
        PatchNarrativeEntryBody? request,
        INarrativeService narrative,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new ValidationFailedException(
                "expectedRevision",
                "An edit names the revision it read, so name one.");
        }

        var presented = RequiredEntryRevision(context, request.ExpectedRevision);

        _ = projectId;

        var edited = await narrative
            .PatchEntryAsync(
                owner,
                entryId,
                presented,
                new NarrativeEntryPatch(
                    request.Kind,
                    request.OccurredAt,
                    request.Text,
                    request.SourceEvidenceIds,
                    request.ClearSourceEvidenceIds ?? false,
                    request.Visibility),
                cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{edited.Revision.ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(NarrativeEntryResponse.From(edited));
    }

    private static async Task<IResult> RefreshSummaryAsync(
        Guid projectId,
        HttpContext context,
        RefreshNarrativeBody? request,
        INarrativeService narrative,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new ValidationFailedException(
                "idempotencyKey",
                "A refresh carries an idempotency key, so name one.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new ValidationFailedException(
                "idempotencyKey",
                "A refresh carries an idempotency key, so name one.");
        }

        var accepted = await narrative
            .RefreshSummaryAsync(
                owner,
                projectId,
                new NarrativeRefreshRequest(
                    request.Cutoff,
                    request.SelectedEntryIds ?? [],
                    request.IdempotencyKey),
                cancellationToken)
            .ConfigureAwait(false);

        // 202 with the operation's own location: the work has been accepted and durably recorded,
        // and this is where its progress is read.
        return Results.Json(
            new NarrativeRefreshAcceptedResponse(
                accepted.Operation.Id.ToString("D"),
                $"/api/operations/{accepted.Operation.Id:D}",
                OperationStatuses.ToWire(accepted.Operation.Status),
                accepted.Created),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static int? ListLimit(HttpContext context)
    {
        var raw = context.Request.Query["limit"].FirstOrDefault();
        if (raw is null)
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var limit))
        {
            throw new ValidationFailedException("limit", "A limit is a positive page size.");
        }

        return limit;
    }

    /// <summary>
    /// Reads the entry revision an edit is conditional on. The header is the precondition, and the
    /// body's <c>expectedRevision</c> has to agree with it, so two answers to one question never
    /// reach the service.
    /// </summary>
    private static long RequiredEntryRevision(HttpContext context, long? bodyRevision)
    {
        var presented = context.Request.Headers.IfMatch.ToString().Trim();

        if (presented.Length == 0)
        {
            throw new IfMatchRequiredException("narrative entry");
        }

        if (presented.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            presented = presented[2..];
        }

        presented = presented.Trim().Trim('"');

        if (!long.TryParse(presented, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision < 1)
        {
            throw new ValidationFailedException(
                "If-Match",
                "If-Match must be the revision you read, as a decimal number, optionally quoted or weakly marked.");
        }

        if (bodyRevision is not null && bodyRevision.Value != revision)
        {
            throw new ValidationFailedException(
                "expectedRevision",
                "The expected revision must be the one presented in If-Match.");
        }

        return revision;
    }

    private static async Task<OwnerScope> OwnerScopeAsync(
        HttpContext context,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var identity = await access.EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);

        return new OwnerScope(identity.InstallationId, session.ActorId);
    }
}

/// <summary>What an owner sends when they record narrative history.</summary>
internal sealed record CreateNarrativeEntryBody(
    string? Kind,
    DateTimeOffset? OccurredAt,
    string? Text,
    IReadOnlyList<Guid>? SourceEvidenceIds,
    string? Visibility);

/// <summary>What an owner sends when they edit a narrative entry. An absent field is left alone.</summary>
internal sealed record PatchNarrativeEntryBody(
    long? ExpectedRevision,
    string? Kind,
    DateTimeOffset? OccurredAt,
    string? Text,
    IReadOnlyList<Guid>? SourceEvidenceIds,
    bool? ClearSourceEvidenceIds,
    string? Visibility);

/// <summary>What an owner sends when they ask for a summary refresh.</summary>
internal sealed record RefreshNarrativeBody(
    DateTimeOffset? Cutoff,
    IReadOnlyList<Guid>? SelectedEntryIds,
    string? IdempotencyKey);

/// <summary>One narrative entry, as the routes serve it.</summary>
internal sealed record NarrativeEntryResponse(
    Guid Id,
    Guid ProjectId,
    string Kind,
    DateTimeOffset OccurredAt,
    string Text,
    IReadOnlyList<Guid> SourceEvidenceIds,
    long Revision,
    string Visibility)
{
    public static NarrativeEntryResponse From(NarrativeEntryView view) => new(
        view.Id,
        view.ProjectId,
        view.Kind,
        view.OccurredAt,
        view.Text,
        view.SourceEvidenceIds,
        view.Revision,
        view.Visibility);
}

/// <summary>One generated project summary, as the read route serves it.</summary>
internal sealed record ProjectSummaryResponse(
    Guid Id,
    Guid ProjectId,
    Guid? ThroughMilestoneId,
    string InputHash,
    string BodyJson,
    long Revision,
    bool Stale,
    DateTimeOffset UpdatedAt)
{
    public static ProjectSummaryResponse From(ProjectSummaryView view) => new(
        view.Id,
        view.ProjectId,
        view.ThroughMilestoneId,
        view.InputHash,
        view.BodyJson,
        view.Revision,
        view.Stale,
        view.UpdatedAt);
}

/// <summary>One project's narrative: its explicit entries in chronological order and its summary, if any.</summary>
internal sealed record NarrativeResponse(
    IReadOnlyList<NarrativeEntryResponse> Entries,
    string? NextCursor,
    ProjectSummaryResponse? Summary);

/// <summary>A refresh that was accepted into the durable queue.</summary>
internal sealed record NarrativeRefreshAcceptedResponse(
    string OperationId,
    string OperationUrl,
    string Status,
    bool Created);
