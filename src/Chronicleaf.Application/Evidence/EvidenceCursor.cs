using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Application.Evidence;

/// <summary>
/// The scope-bound cursor for a milestone's evidence list.
/// </summary>
/// <remarks>
/// It reuses the delivered <see cref="ScopeCursor"/> envelope rather than a second encoding, so an
/// evidence cursor is refused from another installation exactly as a project cursor is. The sort key
/// is the bundle revision written as a fixed-width decimal, because the list is ordered by revision
/// and a cursor has to compare as text: without the padding, "10" would sort before "9".
/// </remarks>
public static class EvidenceCursor
{
    private const int RevisionKeyWidth = 20;

    /// <summary>Encodes the position of the last bundle a caller has seen.</summary>
    public static string Encode(Guid scopeId, long revision, Guid id) =>
        ScopeCursor.Encode(scopeId, revision.ToString("D" + RevisionKeyWidth, System.Globalization.CultureInfo.InvariantCulture), id);

    /// <summary>
    /// Decodes a cursor this scope issued into the revision and id the next page must sort before.
    /// </summary>
    /// <exception cref="ValidationFailedException">The value is malformed or from another scope.</exception>
    public static (long Revision, Guid Id) Decode(string cursor, Guid scopeId)
    {
        var (sortKey, id) = ScopeCursor.Decode(cursor, scopeId);

        if (!long.TryParse(
                sortKey,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var revision))
        {
            // A cursor that decodes to a payload this list cannot order by is refused, not coerced:
            // guessing would page from a position the caller never asked for.
            throw new ValidationFailedException("cursor", "The cursor is not a cursor this scope issued.");
        }

        return (revision, id);
    }
}
