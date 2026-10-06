using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chronicleaf.Application.Agency;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Agency;

/// <summary>
/// The wire shapes of the agency client routes (AGENCY-CLIENT-PROJECTS-001/002).
/// </summary>
/// <remarks>
/// Response field names are pinned explicitly, as every other package's are, so the contract cannot
/// drift with serializer policy. Timestamps cross the wire as UTC ISO-8601 and revisions as
/// integers, and a revision is also returned as an <c>ETag</c> so a client can present it back
/// verbatim in <c>If-Match</c>.
/// <para>
/// The report context carries both revisions: the client revision for edits and the context
/// revision the report pins. A membership change moves only the latter, so a report that pinned
/// the old generation is recognizably stale without invalidating a client edit in flight.
/// </para>
/// </remarks>
public sealed record ClientResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("contextRevision")] long ContextRevision,
    [property: JsonPropertyName("archivedAt")] string? ArchivedAt)
{
    public static ClientResponse From(Client client) =>
        new(
            client.Id.ToString("D"),
            client.Name,
            client.Notes,
            client.Revision,
            client.ContextRevision,
            client.ArchivedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}

public sealed record ClientListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<ClientResponse> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor)
{
    public static ClientListResponse From(ClientPage page) =>
        new([.. page.Items.Select(ClientResponse.From)], page.NextCursor);
}

public sealed record ClientProjectEntryResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("archivedAt")] string? ArchivedAt)
{
    public static ClientProjectEntryResponse From(Project project) =>
        new(
            project.Id.ToString("D"),
            project.Name,
            project.Revision,
            project.ArchivedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}

public sealed record ClientProjectListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<ClientProjectEntryResponse> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor)
{
    public static ClientProjectListResponse From(ClientProjectPage page) =>
        new([.. page.Items.Select(ClientProjectEntryResponse.From)], page.NextCursor);
}

public sealed record ClientReportEntryResponse(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("projectRevision")] long ProjectRevision);

public sealed record ClientReportContextResponse(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("clientRevision")] long ClientRevision,
    [property: JsonPropertyName("contextRevision")] long ContextRevision,
    [property: JsonPropertyName("projects")] IReadOnlyList<ClientReportEntryResponse> Projects)
{
    public static ClientReportContextResponse From(ClientReportContext context) =>
        new(
            context.ClientId.ToString("D"),
            context.ClientRevision,
            context.ContextRevision,
            [.. context.Projects.Select(entry => new ClientReportEntryResponse(
                entry.ProjectId.ToString("D"),
                entry.ProjectName,
                entry.ProjectRevision))]);
}

/// <summary>The <c>POST /api/clients</c> body.</summary>
public sealed record CreateClientRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("notes")] string? Notes);

/// <summary>
/// The <c>PATCH /api/clients/{id}</c> body. A field that is absent is left alone.
/// </summary>
public sealed record UpdateClientRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("notes")] string? Notes);

/// <summary>
/// The <c>PATCH /api/projects/{id}/client</c> body, read as a document rather than a record: the
/// key must be present, and an explicit JSON <c>null</c> clears the project back to personal while
/// a missing key is a 422. A <c>JsonElement?</c> record property cannot express that distinction -
/// a JSON null arrives as C# null, identical to a key that was never sent - so the document is
/// inspected directly. The project revision travels in <c>If-Match</c> like every other edit; a
/// body revision is accepted as a fallback for callers that can only send one, and the header
/// wins when both are present.
/// </summary>
public static class AgencyClientDocument
{
    public const string MissingKeyMessage =
        "clientId is required: a client id, or null to clear the project back to personal.";

    public const string MalformedValueMessage =
        "clientId must be a client id, or null to clear the project back to personal.";
}

/// <summary>
/// Turns the request shapes into the application inputs. Keeping the translation here is what lets
/// the application layer stay free of HTTP.
/// </summary>
public static class AgencyClientMapping
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

    public static CreateClientInput ToInput(CreateClientRequest request) =>
        new(
            request.Name ?? string.Empty,
            request.Notes ?? string.Empty);

    public static ClientPatch ToPatch(UpdateClientRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ClientPatch(request.Name, request.Notes);
    }

    /// <summary>
    /// Reads the assignment: the key must be present (explicit <c>null</c> clears), and a string
    /// value must be a project-client id rather than an arbitrary token.
    /// </summary>
    /// <summary>
    /// Reads the assignment from the request document: the key must be present (explicit
    /// <c>null</c> clears), and a string value must be a client id rather than an arbitrary token.
    /// </summary>
    public static ClientAssignment ToAssignment(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("clientId", out var element))
        {
            throw new ValidationFailedException("clientId", AgencyClientDocument.MissingKeyMessage);
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return new ClientAssignment(null);
        }

        if (element.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(element.GetString(), out var clientId) ||
            clientId == Guid.Empty)
        {
            throw new ValidationFailedException("clientId", AgencyClientDocument.MalformedValueMessage);
        }

        return new ClientAssignment(clientId);
    }

    /// <summary>
    /// Reads the fallback body revision: a present integer, or <c>null</c> when the body carries
    /// none. Anything else is a 422 naming the field, never a silent "no revision".
    /// </summary>
    public static long? ToBodyRevision(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("expectedRevision", out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var revision))
        {
            throw new ValidationFailedException(
                "expectedRevision",
                "expectedRevision must be the revision you read, as an integer.");
        }

        return revision;
    }
}
