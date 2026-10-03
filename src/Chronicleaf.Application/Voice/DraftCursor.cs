using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Application.Voice;

/// <summary>
/// The scope-bound cursor for a project's draft list (VOICE-PROFILES-001/002).
/// </summary>
/// <remarks>
/// It reuses the delivered <see cref="ScopeCursor"/> envelope rather than inventing a second
/// encoding, so a draft cursor issued for one installation is refused by another exactly as a
/// project cursor is. The sort key is the draft's creation instant in a fixed-width, UTC,
/// round-trip form, because the list is ordered <c>createdAt desc, id desc</c> and a cursor has to
/// compare as text - without a fixed width and a constant format, two instants on the same day
/// would not order the way their chronology says they do.
/// </remarks>
public static class DraftCursor
{
    private const string KeyFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    /// <summary>Encodes the position of the last draft a caller has seen.</summary>
    public static string Encode(Guid scopeId, DateTimeOffset createdAt, Guid id) =>
        ScopeCursor.Encode(
            scopeId,
            createdAt.ToUniversalTime().ToString(
                KeyFormat,
                System.Globalization.CultureInfo.InvariantCulture),
            id);

    /// <summary>
    /// Decodes a cursor this scope issued into the position the next page must sort before.
    /// </summary>
    /// <exception cref="ValidationFailedException">The value is malformed, truncated or from another scope.</exception>
    public static (DateTimeOffset CreatedBefore, Guid IdBefore) Decode(string cursor, Guid scopeId)
    {
        var (sortKey, id) = ScopeCursor.Decode(cursor, scopeId);

        if (!DateTimeOffset.TryParse(
                sortKey,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var createdAt))
        {
            // A cursor that decodes to a key this list cannot order by is refused, not coerced.
            throw new ValidationFailedException("cursor", "The cursor is not a cursor this scope issued.");
        }

        return (createdAt, id);
    }
}
