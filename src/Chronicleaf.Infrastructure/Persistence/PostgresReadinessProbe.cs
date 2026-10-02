using System.Data.Common;
using System.Net.Sockets;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Readiness;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// Answers readiness from a real connection attempt against the configured database.
/// </summary>
/// <remarks>
/// Two independent facts are checked, in this order:
/// <list type="number">
/// <item><description>
/// Can PostgreSQL be reached at all? No configuration, a refused connection, a DNS failure or a
/// connect timeout is reported as <see cref="ReadinessStatus.DatabaseUnavailable"/>. The message
/// never contains the connection string, the host or the provider's own error text.
/// </description></item>
/// <item><description>
/// Is the schema this build needs actually applied? The migration history must contain the
/// expected forward migration. A database that cannot even be asked about migrations - because
/// the history table does not exist - and a database whose history stops at an older migration
/// are both reported as <see cref="ReadinessStatus.SchemaMismatch"/>: in both cases the reachable
/// schema is older than required.
/// </description></item>
/// </list>
/// A process that cannot reach its database is never reported as ready, and no in-memory or
/// otherwise simulated store is ever substituted for the real one.
/// </remarks>
public sealed class PostgresReadinessProbe(ChronicleafDbContext dbContext, string? connectionString)
    : IReadinessProbe
{
    /// <summary>How long a readiness check may spend on the database before it gives up.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private const string ProbeTimeoutText = "2s";

    public async Task<ReadinessReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Unavailable("no database connection is configured");
        }

        if (!await CanReachDatabaseAsync(cancellationToken).ConfigureAwait(false))
        {
            return Unavailable($"did not answer within {ProbeTimeoutText}");
        }

        return await CheckSchemaAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a connection and forces a real round trip. Opening alone is not enough: Npgsql pools
    /// connections, so opening can hand back a socket whose server has since gone away. Asking
    /// the server a question is what makes "reachable" true.
    /// </summary>
    private async Task<bool> CanReachDatabaseAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            await dbContext.Database.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            await dbContext.Database
                .SqlQuery<int>(
                    $"""
                     SELECT 1 AS "Value"
                     """)
                .SingleAsync(timeout.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            return false;
        }
        finally
        {
            try
            {
                await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                // The connection is already unusable; readiness reports it as unavailable.
            }
        }
    }

    private async Task<ReadinessReport> CheckSchemaAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var applied = await dbContext.Database
                .SqlQuery<string>(
                    $"""
                     SELECT "MigrationId" AS "Value"
                     FROM "__EFMigrationsHistory"
                     """)
                .ToListAsync(timeout.Token)
                .ConfigureAwait(false);

            if (!ChronicleafSchema.IsCurrent(applied))
            {
                return Mismatch(
                    $"the applied migrations do not include {string.Join(", ", ChronicleafSchema.MissingMigrationIds(applied))}");
            }

            return new ReadinessReport(
                ReadinessStatus.Ready,
                "ready",
                "the expected database schema is reachable",
                ChronicleafDbContext.RequiredSchemaVersion,
                ChronicleafDbContext.RequiredSchemaVersion);
        }
        catch (Exception exception) when (IsSchemaFailure(exception))
        {
            return Mismatch("the database does not carry the schema this build requires");
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // The server went away between the reachability check and this query. That is an
            // unavailable database, not a schema this build does not recognise.
            return Unavailable($"did not answer within {ProbeTimeoutText}");
        }
    }

    private static ReadinessReport Mismatch(string reason) =>
        new(
            ReadinessStatus.SchemaMismatch,
            ApiError.SchemaMismatch,
            $"the database schema does not match this build: {reason}",
            ChronicleafDbContext.RequiredSchemaVersion,
            AppliedSchemaVersion: null);

    private static ReadinessReport Unavailable(string reason) =>
        new(
            ReadinessStatus.DatabaseUnavailable,
            ApiError.DatabaseUnavailable,
            $"the database is unavailable: {reason}",
            ChronicleafDbContext.RequiredSchemaVersion,
            AppliedSchemaVersion: null);

    private static bool IsConnectionFailure(Exception exception) =>
        RootOf(exception) is DbException
            or TimeoutException
            or SocketException
            or IOException
            or OperationCanceledException;

    /// <summary>
    /// Only a database that answered and then reported an unknown object counts as a schema
    /// mismatch. Every other failure - including a server that disappeared between the
    /// reachability check and this query - is an unavailable dependency, never a wrong schema.
    /// </summary>
    private static bool IsSchemaFailure(Exception exception) =>
        RootOf(exception) is PostgresException { SqlState: PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedObject };

    /// <summary>
    /// Provider exceptions are frequently wrapped by EF Core or the host, so the classification
    /// always looks at the innermost cause.
    /// </summary>
    private static Exception RootOf(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }
}
