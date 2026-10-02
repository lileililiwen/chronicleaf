using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Projects;

/// <summary>
/// The wire shapes of the Project and Milestone routes
/// (PROJECTS-AND-MILESTONES-001, PROJECTS-AND-MILESTONES-002).
/// </summary>
/// <remarks>
/// Response field names are pinned explicitly, as the access package's are, so the contract cannot
/// drift with serializer policy. Timestamps cross the wire as UTC ISO-8601 and revisions as
/// integers, and a revision is also returned as an <c>ETag</c> so a client can present it back
/// verbatim in <c>If-Match</c>.
/// <para>
/// The request shapes are the boundary where a status would be a mistake, so none of them has one.
/// There is no field anywhere on the wire that can set a milestone's status; confirmation and
/// dismissal are the two actions, and only the owner can call them.
/// </para>
/// </remarks>
public sealed record ProjectResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("projectUrl")] string? ProjectUrl,
    [property: JsonPropertyName("repoConnectionId")] string? RepoConnectionId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("archivedAt")] string? ArchivedAt)
{
    public static ProjectResponse From(Project project) =>
        new(
            project.Id.ToString("D"),
            project.Name,
            project.Description,
            project.ProjectUrl,
            project.RepoConnectionId?.ToString("D"),
            project.Revision,
            project.ArchivedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}

public sealed record ProjectListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<ProjectResponse> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor)
{
    public static ProjectListResponse From(ProjectPage page) =>
        new([.. page.Items.Select(ProjectResponse.From)], page.NextCursor);
}

public sealed record MilestoneResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("occurredAt")] string OccurredAt,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("sourceKey")] string? SourceKey,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("knownLimitations")] IReadOnlyList<string> KnownLimitations,
    [property: JsonPropertyName("nextSteps")] IReadOnlyList<string> NextSteps)
{
    public static MilestoneResponse From(Milestone milestone) =>
        new(
            milestone.Id.ToString("D"),
            milestone.ProjectId.ToString("D"),
            milestone.Title,
            milestone.Version,
            milestone.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            milestone.Type,
            milestone.Source,
            milestone.SourceKey,
            milestone.Status,
            milestone.Revision,
            milestone.Notes,
            milestone.KnownLimitations,
            milestone.NextSteps);
}

public sealed record MilestoneListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<MilestoneResponse> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor)
{
    public static MilestoneListResponse From(MilestonePage page) =>
        new([.. page.Items.Select(MilestoneResponse.From)], page.NextCursor);
}

/// <summary>One imported candidate: the key, the one record that key identifies, and whether it was new.</summary>
public sealed record ImportedCandidateResponse(
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("milestoneId")] string MilestoneId,
    [property: JsonPropertyName("created")] bool Created);

public sealed record ImportedCandidatesResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<ImportedCandidateResponse> Items)
{
    public static ImportedCandidatesResponse From(ImportedCandidates imported) =>
        new([.. imported.Items.Select(item => new ImportedCandidateResponse(
            item.SourceKey,
            item.MilestoneId.ToString("D"),
            item.Created))]);
}

/// <summary>The <c>POST /api/projects</c> body.</summary>
public sealed record CreateProjectRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("projectUrl")] string? ProjectUrl,
    [property: JsonPropertyName("repoConnectionId")] string? RepoConnectionId);

/// <summary>
/// The <c>PATCH /api/projects/{id}</c> body. A field that is absent is left alone; an explicit
/// <c>null</c> on a nullable field clears it. That distinction is the reason these are
/// <see cref="JsonElement"/> rather than plain strings: a form that saves one field must not empty
/// the others, and a form that empties a field must be able to say so.
/// </summary>
public sealed record UpdateProjectRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("projectUrl")] JsonElement? ProjectUrl,
    [property: JsonPropertyName("repoConnectionId")] JsonElement? RepoConnectionId);

/// <summary>The <c>POST /api/projects/{id}/milestones</c> body: a milestone the owner typed.</summary>
public sealed record CreateMilestoneRequest(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("occurredAt")] string? OccurredAt,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("knownLimitations")] IReadOnlyList<string>? KnownLimitations,
    [property: JsonPropertyName("nextSteps")] IReadOnlyList<string>? NextSteps);

/// <summary>
/// The <c>PATCH /api/milestones/{id}</c> body. There is no status field, here or anywhere: an edit
/// changes what a milestone says, never whether it counts.
/// </summary>
public sealed record UpdateMilestoneRequest(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("version")] JsonElement? Version,
    [property: JsonPropertyName("occurredAt")] JsonElement? OccurredAt,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("knownLimitations")] IReadOnlyList<string>? KnownLimitations,
    [property: JsonPropertyName("nextSteps")] IReadOnlyList<string>? NextSteps);

/// <summary>
/// The <c>POST /api/projects/{id}/milestones/import</c> body: upstream records that arrive as
/// candidates. There is no status field here either, which is the point.
/// </summary>
public sealed record ImportCandidatesRequest(
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ImportCandidateRequest>? Candidates);

public sealed record ImportCandidateRequest(
    [property: JsonPropertyName("sourceKey")] string? SourceKey,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("occurredAt")] string? OccurredAt,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("knownLimitations")] IReadOnlyList<string>? KnownLimitations,
    [property: JsonPropertyName("nextSteps")] IReadOnlyList<string>? NextSteps);

/// <summary>
/// Turns the request shapes into the application inputs, and the application inputs back into
/// responses. Keeping the translation here is what lets the application layer stay free of HTTP.
/// </summary>
public static class ProjectMilestoneMapping
{
    /// <summary>
    /// Reads an <c>If-Match</c> revision. The header is required on every edit and every action
    /// (428), it must be a decimal revision, and a bare number, a quoted entity-tag and a weak
    /// entity-tag are all accepted because a client may have taken the value from the <c>ETag</c>
    /// it was given. Anything else is a 422 naming <c>If-Match</c>, never a silent "no revision".
    /// </summary>
    public static long RequiredRevision(HttpContext context, string resource)
    {
        ArgumentNullException.ThrowIfNull(context);

        var presented = context.Request.Headers.IfMatch.ToString().Trim();

        if (presented.Length == 0)
        {
            throw new IfMatchRequiredException(resource);
        }

        if (presented.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            presented = presented[2..];
        }

        presented = presented.Trim().Trim('"');

        if (!long.TryParse(presented, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
        {
            throw new ValidationFailedException(
                "If-Match",
                "If-Match must be the revision you read, as a decimal number, optionally quoted or weakly marked.");
        }

        return revision;
    }

    /// <summary>Publishes the current revision as the entity-tag the client presents back.</summary>
    public static void PublishRevision(HttpContext context, long revision)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Headers.ETag = $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";
    }

    public static CreateProjectInput ToInput(CreateProjectRequest request) =>
        new(
            request.Name ?? string.Empty,
            request.Description ?? string.Empty,
            Guid.TryParse(request.RepoConnectionId, out var connection) ? connection : null,
            request.ProjectUrl);

    public static ProjectPatch ToPatch(UpdateProjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ProjectPatch(
            request.Name,
            request.Description,
            ReadString(request.ProjectUrl),
            IsExplicitNull(request.ProjectUrl),
            ReadGuid(request.RepoConnectionId),
            IsExplicitNull(request.RepoConnectionId));
    }

    public static MilestoneDraft ToDraft(CreateMilestoneRequest request) =>
        new(
            request.Title ?? string.Empty,
            request.Version,
            ReadInstant(request.OccurredAt, "occurredAt"),
            request.Type ?? string.Empty,
            request.Notes ?? string.Empty,
            request.KnownLimitations ?? [],
            request.NextSteps ?? []);

    public static MilestonePatch ToPatch(UpdateMilestoneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new MilestonePatch(
            request.Title,
            ReadString(request.Version),
            IsExplicitNull(request.Version),
            ReadOptionalInstant(request.OccurredAt, "occurredAt"),
            request.Type,
            request.Notes,
            request.KnownLimitations,
            request.NextSteps);
    }

    public static ImportCandidatesInput ToInput(ImportCandidatesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ImportCandidatesInput(
            request.Source ?? string.Empty,
            [.. (request.Candidates ?? []).Select(candidate => new CandidateImport(
                candidate.SourceKey ?? string.Empty,
                new MilestoneDraft(
                    candidate.Title ?? string.Empty,
                    candidate.Version,
                    ReadInstant(candidate.OccurredAt, "occurredAt"),
                    candidate.Type ?? string.Empty,
                    candidate.Notes ?? string.Empty,
                    candidate.KnownLimitations ?? [],
                    candidate.NextSteps ?? [])))]);
    }

    /// <summary>
    /// A present, non-null JSON value as a string; anything absent or null becomes <c>null</c>, which
    /// is how an edit says "leave this field alone".
    /// </summary>
    private static string? ReadString(JsonElement? value) =>
        value is { } element && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static Guid? ReadGuid(JsonElement? value) =>
        value is { } element && element.ValueKind == JsonValueKind.String &&
        Guid.TryParse(element.GetString(), out var parsed)
            ? parsed
            : null;

    private static bool IsExplicitNull(JsonElement? value) =>
        value is { } element && element.ValueKind == JsonValueKind.Null;

    /// <summary>
    /// Reads an instant from a field of a partial edit. An absent field is <c>null</c>, which means
    /// "leave this instant alone"; a present field must be a real ISO-8601 instant, so a value that
    /// is present and unparseable is still a 422 rather than a silent no-op.
    /// </summary>
    private static DateTimeOffset? ReadOptionalInstant(JsonElement? value, string field)
    {
        if (value is not { } element || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new ValidationFailedException(field, "the value must be an ISO-8601 instant.");
        }

        return ReadInstant(element.GetString(), field);
    }

    private static DateTimeOffset ReadInstant(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationFailedException(field, "an ISO-8601 instant is required, for example 2026-10-02T12:00:00Z.");
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new ValidationFailedException(field, "the value must be an ISO-8601 instant.");
        }

        // Whatever offset the client sent, what is stored is one instant in UTC.
        return parsed.ToUniversalTime();
    }
}
