using System.Data.Common;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Projects;

/// <summary>One parameter of a parameterized statement, with its PostgreSQL type named explicitly.</summary>
internal readonly record struct SqlParameter(string Name, NpgsqlDbType Type, object? Value);

/// <summary>
/// The small amount of plumbing the Project and Milestone stores need to write with explicit SQL.
/// </summary>
/// <remarks>
/// These records are immutable in the domain, so an edit is a statement rather than a tracked
/// property assignment, and a statement is the only way to express a revision compare-and-set that
/// the database decides. Every value is a bound parameter, never text spliced into SQL, and the
/// command joins the unit of work's transaction, so a write and its audit row still commit together.
/// </remarks>
internal static class ChronicleafSql
{
    /// <summary>
    /// Runs one statement and returns how many rows it changed. Zero is a real answer here: it is
    /// what a compare-and-set that lost a race returns, and the caller re-reads to say why.
    /// </summary>
    public static async Task<int> ExecuteAsync(
        ChronicleafDbContext dbContext,
        string sql,
        IReadOnlyList<SqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;

        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = CreateCommand(dbContext, connection, sql, parameters, cancellationToken);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs one statement and returns the first column of its first row, or <c>null</c> when it
    /// returned no row. This is how an <c>ON CONFLICT DO NOTHING RETURNING</c> insert reports whether
    /// it created the row: a returned id means this call wrote it, and no row means the row was
    /// already there.
    /// </summary>
    public static async Task<T?> ScalarAsync<T>(
        ChronicleafDbContext dbContext,
        string sql,
        IReadOnlyList<SqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;

        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = CreateCommand(dbContext, connection, sql, parameters, cancellationToken);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return value is null or DBNull ? default : (T)value;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs one query and hands each row to <paramref name="read"/>, so a list query is a statement
    /// the database orders and slices rather than a page assembled in memory.
    /// </summary>
    public static async Task<IReadOnlyList<T>> QueryAsync<T>(
        ChronicleafDbContext dbContext,
        string sql,
        IReadOnlyList<SqlParameter> parameters,
        Func<DbDataReader, T> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(read);

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;

        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = CreateCommand(dbContext, connection, sql, parameters, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var rows = new List<T>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(read(reader));
            }

            return rows;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static NpgsqlCommand CreateCommand(        ChronicleafDbContext dbContext,
        System.Data.Common.DbConnection connection,
        string sql,
        IReadOnlyList<SqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        var command = ((NpgsqlConnection)connection).CreateCommand();
        command.CommandText = sql;

        // Inside a unit of work the command must share its transaction, or the write would commit on
        // its own and the audit row would not be part of the same decision.
        if (dbContext.Database.CurrentTransaction is { } transaction)
        {
            command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        }

        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(parameter.Name, parameter.Type)
            {
                Value = parameter.Value ?? DBNull.Value,
            });
        }

        return command;
    }
}
