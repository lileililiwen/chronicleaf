using System.Data.Common;
using Chronicleaf.Core;

namespace Chronicleaf.Infrastructure.Projects;

/// <summary>
/// Reads one stored row into its domain record.
/// </summary>
/// <remarks>
/// The column order below is the column order of the SELECT that feeds it, and it is stated here
/// rather than guessed at: a row is read positionally so a query cannot silently hand the wrong
/// column to a field. Nothing here interprets a value, converts an instant or hides a null: a
/// nullable column stays null and a timestamp is already UTC because it is stored as
/// <c>timestamptz</c>.
/// </remarks>
internal static class ProjectRowReader
{
    /// <summary>
    /// The project columns, in the order <see cref="Read"/> expects. The primary key is not read,
    /// because no lookup is by position.
    /// </summary>
    public const string Columns =
        "id, scope_id, name, description, repo_connection_id, project_url, revision, archived_at";

    public static Project Read(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));
}

/// <summary>Reads one stored milestone row into its domain record, positionally as above.</summary>
internal static class MilestoneRowReader
{
    /// <summary>
    /// The milestone columns in the order <see cref="Read"/> expects, qualified by
    /// <paramref name="table"/> so a join cannot leave an unqualified name ambiguous.
    /// </summary>
    public static string QualifiedColumns(string table) =>
        $"""
         {table}.id, {table}.project_id, {table}.title, {table}.version, {table}.occurred_at,
         {table}.type, {table}.source, {table}.source_key, {table}.status, {table}.revision,
         {table}.notes, {table}.known_limitations, {table}.next_steps
         """;

    public static Milestone Read(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetInt64(9),
            reader.GetString(10),
            reader.GetFieldValue<string[]>(11),
            reader.GetFieldValue<string[]>(12));
}
