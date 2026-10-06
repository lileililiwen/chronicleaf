using Chronicleaf.Core;

namespace Chronicleaf.Application.Milestones;

/// <summary>
/// One audit row staged by a Project or Milestone mutation (PROJECTS-AND-MILESTONES-001/002).
/// </summary>
/// <remarks>
/// Only ids, the action, the revision and a short fixed label are recorded. No note text, no known
/// limitation, no URL, no title and no credential ever reaches the audit trail, because the trail is
/// readable more widely than the records it describes.
/// </remarks>
/// <param name="ActorId">The owner who made the change.</param>
/// <param name="Action">A stable action name, at most 80 characters.</param>
/// <param name="TargetId">The project or milestone that changed.</param>
/// <param name="Revision">The revision the change produced.</param>
/// <param name="Detail">A short fixed label, chosen from this package's own vocabulary.</param>
public sealed record DomainAudit(Guid? ActorId, string Action, Guid TargetId, long Revision, string Detail);

/// <summary>
/// Stages audit rows. The rows are committed in the same unit of work as the change they describe,
/// so an audit row exists if and only if the change it records was committed.
/// </summary>
public interface IAuditWriter
{
    void Record(DomainAudit audit);
}

/// <summary>
/// The Project side of the storage contract. Every method is scoped: a lookup for an id this owner
/// may not reach answers <c>null</c> rather than finding somebody else's record, and every write
/// carries the same scope in its WHERE clause so a scope cannot be crossed by a forged parameter.
/// </summary>
public interface IProjectStore
{
    /// <summary>Reads one project in a scope, or <c>null</c> when it is absent or out of scope.</summary>
    Task<Project?> FindAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads projects ordered by name and then id, starting after the cursor position. Passing no
    /// position starts at the beginning.
    /// </summary>
    Task<IReadOnlyList<Project>> ListAsync(
        Guid scopeId,
        string? afterName,
        Guid? afterId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new project. Returns <c>false</c> when another writer inserted the same id first,
    /// which is a collision rather than a success.
    /// </summary>
    Task<bool> InsertAsync(Project project, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies an edit to a live project only when the stored revision is exactly
    /// <paramref name="expectedRevision"/>. Returns how many rows changed: 0 means the write lost a
    /// race, was refused because the project is archived, or the project is gone, and the caller has
    /// to re-read and say which.
    /// </summary>
    Task<int> UpdateAsync(
        Guid scopeId,
        Guid projectId,
        long expectedRevision,
        string name,
        string description,
        string? projectUrl,
        Guid? repoConnectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps <paramref name="archivedAt"/> on a live project, again only at
    /// <paramref name="expectedRevision"/>. This is a soft archive: the row and everything under it
    /// stay exactly as they are, and the revision advances so a later edit is told it is stale.
    /// </summary>
    Task<int> ArchiveAsync(
        Guid scopeId,
        Guid projectId,
        long expectedRevision,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Groups a live project under <paramref name="clientId"/> (or clears it back to personal when
    /// <c>null</c>), again only at <paramref name="expectedRevision"/>
    /// (AGENCY-CLIENT-PROJECTS-001). The client itself is validated by the caller: this write names
    /// the id it was given, and the revision advances so a later edit is told it is stale.
    /// </summary>
    Task<int> SetClientAsync(
        Guid scopeId,
        Guid projectId,
        long expectedRevision,
        Guid? clientId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the projects grouped under one client in a scope, ordered by name and then id,
    /// starting after the cursor position (AGENCY-CLIENT-PROJECTS-001). Passing no position starts
    /// at the beginning. An archived membership is still listed: the archive preserves history.
    /// </summary>
    Task<IReadOnlyList<Project>> ListByClientAsync(
        Guid scopeId,
        Guid clientId,
        string? afterName,
        Guid? afterId,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Milestone side of the storage contract, scoped through the owning project so a milestone can
/// never be reached without its project's scope being checked first.
/// </summary>
public interface IMilestoneStore
{
    /// <summary>Reads one milestone inside a scope, or <c>null</c> when it is absent or out of scope.</summary>
    Task<Milestone?> FindAsync(Guid scopeId, Guid milestoneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the one milestone a project already has for an upstream key, or <c>null</c>. This is the
    /// lookup that makes a repeated import idempotent.
    /// </summary>
    Task<Milestone?> FindBySourceAsync(
        Guid scopeId,
        Guid projectId,
        string source,
        string sourceKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads milestones of one project ordered <c>occurredAt desc, id desc</c>, optionally narrowed
    /// to a status, starting after the cursor position.
    /// </summary>
    Task<IReadOnlyList<Milestone>> ListAsync(
        Guid scopeId,
        Guid projectId,
        string? status,
        DateTimeOffset? occurredBefore,
        Guid? idBefore,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the timeline of one project ordered <c>occurredAt asc, id asc</c>, starting after the
    /// cursor position (MILESTONE-TIMELINE-001).
    /// </summary>
    /// <remarks>
    /// The confirmed history is always included. Candidates join it only when
    /// <paramref name="includeCandidates"/> is set; dismissed records are never included, because
    /// the timeline narrates the history the owner stands behind rather than everything stored.
    /// </remarks>
    Task<IReadOnlyList<Milestone>> ListChronologicalAsync(
        Guid scopeId,
        Guid projectId,
        bool includeCandidates,
        DateTimeOffset? occurredAfter,
        Guid? idAfter,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a milestone, or reports that the project already holds one for the same upstream key.
    /// The decision is the database's, made atomically by the partial unique index, so two imports of
    /// the same key that race each other still produce exactly one row and both callers are told the
    /// same id. A milestone without a source key can never collide, so a manual record is always
    /// inserted.
    /// </summary>
    /// <returns><c>true</c> when this call created the row; <c>false</c> when the row already existed.</returns>
    Task<bool> InsertAsync(Milestone milestone, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a field edit to a milestone, and to no other, when the stored revision is exactly
    /// <paramref name="expectedRevision"/> and the owning project is still live. The revision is not a
    /// parameter: it is incremented by the database, so two edits that present the same revision
    /// cannot both land.
    /// </summary>
    Task<int> UpdateAsync(
        Guid scopeId,
        Guid milestoneId,
        long expectedRevision,
        string title,
        string? version,
        DateTimeOffset occurredAt,
        string type,
        string notes,
        IReadOnlyList<string> knownLimitations,
        IReadOnlyList<string> nextSteps,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a milestone to <paramref name="status"/> at exactly
    /// <paramref name="expectedRevision"/>, and only while the owning project is live. This is the
    /// only write in the repository that can change a status, and the only caller is the owner's
    /// explicit confirm or dismiss action.
    /// </summary>
    Task<int> SetStatusAsync(
        Guid scopeId,
        Guid milestoneId,
        long expectedRevision,
        string status,
        CancellationToken cancellationToken = default);
}
