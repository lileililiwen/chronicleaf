using Chronicleaf.Application.Outbox;

namespace Chronicleaf.Application.Persistence;

/// <summary>
/// Commits domain writes, audit evidence and outbox events as one PostgreSQL transaction.
/// </summary>
/// <remarks>
/// The contract is deliberately narrow: the operation body performs every write it intends,
/// and the unit of work commits domain, audit and outbox state together or leaves all of it
/// unapplied. Nested use is rejected rather than silently sharing an inner commit point.
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Runs <paramref name="operation"/> inside a single READ COMMITTED transaction and commits
    /// when it completes. Any exception rolls the whole transaction back and is rethrown.
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
}

/// <summary>
/// The write side of the singleton installation. Every call runs inside an
/// <see cref="IUnitOfWork"/> transaction opened by the caller; the store never commits itself.
/// </summary>
public interface IInstallationStore
{
    Task<Core.Installation> ProvisionAsync(
        Guid installationId,
        int schemaVersion,
        string action,
        Guid? actorId,
        OutboxPayload outboxPayload,
        CancellationToken cancellationToken = default);

    Task<Core.Installation?> FindAsync(Guid installationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Core.Installation>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Read side of the append-only audit log.</summary>
public interface IAuditLog
{
    Task<IReadOnlyList<Core.AuditEvent>> ListForTargetAsync(
        Guid targetId,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Read side of the durable outbox.</summary>
public interface IOutboxStore
{
    Task<IReadOnlyList<Core.OutboxEvent>> ListPendingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<long> CountAsync(CancellationToken cancellationToken = default);
}
