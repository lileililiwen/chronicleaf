using Chronicleaf.Application.Common;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Operations;

/// <summary>
/// The read side of a durable operation: one scoped lookup and one scoped, bounded list.
/// </summary>
/// <remarks>
/// Every read is scoped. An id that belongs to another scope answers <c>null</c> rather than finding
/// somebody else's operation, so the boundary cannot be probed for what exists elsewhere.
/// </remarks>
public interface IOperationQuery
{
    Task<Operation?> FindAsync(Guid scopeId, Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>The scope's operations, newest first, bounded by <see cref="ListQueryPolicy"/>.</summary>
    Task<IReadOnlyList<Operation>> ListAsync(
        Guid scopeId,
        int? limit = null,
        CancellationToken cancellationToken = default);
}
