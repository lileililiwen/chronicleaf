using System.Data;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of the repository's unit of work.
/// </summary>
/// <remarks>
/// Domain writes, audit rows and outbox rows written inside
/// <see cref="ExecuteAsync{T}"/> are flushed and committed as one PostgreSQL READ COMMITTED
/// transaction. If the operation body throws, the transaction is rolled back and the exception is
/// rethrown unchanged, so no partial installation, audit or outbox row survives. Save points
/// inside an already running unit of work are rejected instead of silently becoming an inner
/// commit point.
/// </remarks>
public sealed class EfUnitOfWork(ChronicleafDbContext dbContext) : IUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InternalFailureException("A unit of work is already running for this request.");
        }

        await using IDbContextTransaction transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or ObjectDisposedException)
        {
            // The transaction is already gone: PostgreSQL rolled it back when the connection died.
        }
    }
}
