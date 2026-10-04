using System.Globalization;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Application.Timeline;

/// <summary>
/// The opaque, scope- and project-bound timeline cursor (MILESTONE-TIMELINE-002).
/// </summary>
/// <remarks>
/// A cursor is the sort position of the last row the caller has seen
/// (<c>occurredAt asc, id asc</c>), encoded so the client cannot read or forge one. Four
/// properties matter beyond what the milestone lists already prove:
/// <list type="bullet">
/// <item><description>It is bound to a project. A cursor issued for one project is refused by
/// another, so a caller cannot paginate one project's history with another's position.</description></item>
/// <item><description>It is bound to the candidate filter. A cursor issued for the confirmed
/// history is refused when candidates are asked for, and the reverse, because the two views
/// order different rows.</description></item>
/// <item><description>It is opaque and validated, not trusted. Anything that does not decode is
/// a 422 naming <c>cursor</c>, never a 500 and never a silently ignored filter.</description></item>
/// <item><description>Its shape is its own. A milestone-list cursor presented here is refused
/// rather than misread, because the payload carries the project it was issued for.</description></item>
/// </list>
/// </remarks>
public static class TimelineCursor
{
    /// <summary>Bumped when the encoding changes, so an old cursor is refused rather than misread.</summary>
    private const int Version = 1;

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>Encodes the chronological position of the last row a caller has seen.</summary>
    public static string Encode(
        Guid scopeId,
        Guid projectId,
        bool includeCandidates,
        DateTimeOffset occurredAt,
        Guid id)
    {
        var payload = JsonSerializer.Serialize(
            new CursorPayload(
                Version,
                scopeId.ToString("D"),
                projectId.ToString("D"),
                includeCandidates,
                occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                id.ToString("D")),
            Wire);

        return Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Decodes a cursor this scope issued for this project and this filter.
    /// </summary>
    /// <exception cref="ValidationFailedException">The value is malformed or was issued for another scope, project or filter.</exception>
    public static (DateTimeOffset OccurredAt, Guid Id) Decode(
        string cursor,
        Guid scopeId,
        Guid projectId,
        bool includeCandidates)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var payload = TryDecode(cursor);
        if (payload is null)
        {
            throw Rejected();
        }

        var (version, cursorScope, cursorProject, cursorFilter, sortKey, id) = payload.Value;

        if (version != Version ||
            cursorScope != scopeId ||
            cursorProject != projectId ||
            cursorFilter != includeCandidates ||
            id == Guid.Empty)
        {
            // A cursor from another project, another scope or the other filter is refused here
            // rather than silently reading the wrong history. Nothing about the other project is
            // disclosed.
            throw Rejected();
        }

        if (!DateTimeOffset.TryParse(
                sortKey,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var occurredAt))
        {
            throw Rejected();
        }

        return (occurredAt, id);
    }

    private static (int Version, Guid Scope, Guid Project, bool IncludeCandidates, string SortKey, Guid Id)? TryDecode(string cursor)
    {
        if (cursor.Length is 0 or > 1024)
        {
            return null;
        }

        CursorPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CursorPayload>(Base64UrlDecode(cursor), Wire);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or DecoderFallbackException)
        {
            return null;
        }

        if (payload is null ||
            payload.Version != Version ||
            !Guid.TryParse(payload.Scope, out var scope) ||
            !Guid.TryParse(payload.Project, out var project) ||
            !Guid.TryParse(payload.Id, out var id))
        {
            return null;
        }

        return (payload.Version, scope, project, payload.IncludeCandidates, payload.SortKey, id);
    }

    /// <summary>The encoded shape. Internal to this repository: a client that reads it is depending on
    /// something no contract promises.</summary>
    private sealed record CursorPayload(
        int Version,
        string Scope,
        string Project,
        bool IncludeCandidates,
        string SortKey,
        string Id);

    private static ValidationFailedException Rejected() =>
        new("cursor", "The cursor is not a cursor this project issued for this view.");

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
