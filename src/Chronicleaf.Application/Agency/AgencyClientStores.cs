using Chronicleaf.Core;

namespace Chronicleaf.Application.Agency;

/// <summary>
/// The Client side of the storage contract (AGENCY-CLIENT-PROJECTS-001/002). Every method is
/// scoped: a lookup for an id this owner may not reach answers <c>null</c> rather than finding
/// somebody else's record, and every write carries the same scope in its WHERE clause so a scope
/// cannot be crossed by a forged parameter.
/// </summary>
public interface IClientStore
{
    /// <summary>Reads one client in a scope, or <c>null</c> when it is absent or out of scope.</summary>
    Task<Client?> FindAsync(Guid scopeId, Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads clients ordered by name and then id, starting after the cursor position. Passing no
    /// position starts at the beginning.
    /// </summary>
    Task<IReadOnlyList<Client>> ListAsync(
        Guid scopeId,
        string? afterName,
        Guid? afterId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new client. Returns <c>false</c> when another writer inserted the same id first,
    /// which is a collision rather than a success.
    /// </summary>
    Task<bool> InsertAsync(Client client, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies an edit to a live client only when the stored revision is exactly
    /// <paramref name="expectedRevision"/>. Returns how many rows changed: 0 means the write lost a
    /// race, was refused because the client is archived, or the client is gone, and the caller has
    /// to re-read and say which.
    /// </summary>
    Task<int> UpdateAsync(
        Guid scopeId,
        Guid clientId,
        long expectedRevision,
        string name,
        string notes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps <paramref name="archivedAt"/> on a live client, again only at
    /// <paramref name="expectedRevision"/>. This is a soft archive: the row and every project under
    /// it stay exactly as they are, and the revision advances so a later edit is told it is stale.
    /// </summary>
    Task<int> ArchiveAsync(
        Guid scopeId,
        Guid clientId,
        long expectedRevision,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the membership generation of one client in this scope. The client revision is left
    /// alone, so an owner holding a client edit is not told it is stale by a project moving.
    /// Returns how many rows changed: 0 means the client is gone or out of scope.
    /// </summary>
    Task<int> TouchContextAsync(
        Guid scopeId,
        Guid clientId,
        CancellationToken cancellationToken = default);
}
