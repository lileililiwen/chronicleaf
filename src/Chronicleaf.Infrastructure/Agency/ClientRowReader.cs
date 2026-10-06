using System.Data.Common;
using Chronicleaf.Core;

namespace Chronicleaf.Infrastructure.Agency;

/// <summary>
/// Reads one stored client row into its domain record.
/// </summary>
/// <remarks>
/// The column order below is the column order of the SELECT that feeds it, and it is stated here
/// rather than guessed at: a row is read positionally so a query cannot silently hand the wrong
/// column to a field. Nothing here interprets a value: a nullable column stays null and a
/// timestamp is already UTC because it is stored as <c>timestamptz</c>.
/// </remarks>
internal static class ClientRowReader
{
    /// <summary>The client columns, in the order <see cref="Read"/> expects.</summary>
    public const string Columns =
        "id, scope_id, name, notes, revision, context_revision, archived_at";

    public static Client Read(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
}
