using System.Globalization;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Milestones;

/// <summary>
/// The opaque, scope-bound list cursor both lists use (PROJECTS-AND-MILESTONES-002).
/// </summary>
/// <remarks>
/// A cursor is not a page number and not an offset: it is the sort position of the last row the
/// caller has already seen, encoded so the client cannot read or forge one. Three properties matter.
/// <list type="bullet">
/// <item><description>
/// It is bound to a scope. The scope id travels inside the value and is checked on decode, so a
/// cursor issued for one installation is refused by another instead of quietly paginating the wrong
/// records.
/// </para></description></item>
/// <item><description>
/// It is opaque. The encoding is an implementation detail of this repository; a caller that parses
/// it is depending on something no contract promises.
/// </para></description></item>
/// <item><description>
/// It is validated, not trusted. Anything that does not decode is a 422 naming <c>cursor</c>, never a
/// 500 and never a silently ignored filter.
/// </para></description></item>
/// </list>
/// </remarks>
public static class ScopeCursor
{
    /// <summary>Bumped when the encoding changes, so an old cursor is refused rather than misread.</summary>
    private const int Version = 1;

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>Encodes the sort position of the last row a caller has seen.</summary>
    public static string Encode(Guid scopeId, string sortKey, Guid id)
    {
        ArgumentNullException.ThrowIfNull(sortKey);

        // JSON rather than a joined string, because a sort key is a project name and may contain any
        // character at all - including whatever separator a hand-rolled encoding would have picked.
        var payload = JsonSerializer.Serialize(
            new CursorPayload(Version, scopeId.ToString("D"), sortKey, id.ToString("D")),
            Wire);

        return Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>Encodes a milestone position, whose sort key is the instant it occurred.</summary>
    public static string EncodeMilestone(Guid scopeId, DateTimeOffset occurredAt, Guid id) =>
        Encode(scopeId, occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), id);

    /// <summary>Decodes a cursor this scope issued.</summary>
    /// <exception cref="ValidationFailedException">The value is malformed, truncated or from another scope.</exception>
    public static (string SortKey, Guid Id) Decode(string cursor, Guid scopeId)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var payload = TryDecode(cursor);
        if (payload is null)
        {
            throw Rejected();
        }

        var (version, cursorScope, sortKey, id) = payload.Value;

        if (version != Version ||
            cursorScope != scopeId ||
            sortKey.Length == 0 ||
            id == Guid.Empty)
        {
            // A cursor from another scope is refused here rather than silently reading from a
            // different installation. Nothing about the other scope is disclosed.
            throw Rejected();
        }

        return (sortKey, id);
    }

    /// <summary>Decodes a milestone cursor and returns the instant its row must sort before.</summary>
    public static (DateTimeOffset OccurredAt, Guid Id) DecodeMilestone(string cursor, Guid scopeId)
    {
        var (sortKey, id) = Decode(cursor, scopeId);

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

    private static (int Version, Guid Scope, string SortKey, Guid Id)? TryDecode(string cursor)
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
            !Guid.TryParse(payload.Id, out var id))
        {
            return null;
        }

        return (payload.Version, scope, payload.SortKey, id);
    }

    /// <summary>The encoded shape. Internal to this repository: a client that reads it is depending on
    /// something no contract promises.</summary>
    private sealed record CursorPayload(int Version, string Scope, string SortKey, string Id);

    private static ValidationFailedException Rejected() =>
        new("cursor", "The cursor is not a cursor this scope issued.");

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
