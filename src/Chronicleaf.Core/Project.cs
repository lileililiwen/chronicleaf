namespace Chronicleaf.Core;

/// <summary>
/// One project the owner builds a milestone history for (PROJECTS-AND-MILESTONES-002).
/// </summary>
/// <remarks>
/// A project is scoped: <see cref="ScopeId"/> is the installation's own owner scope, so a lookup by
/// id alone can never reach a record that belongs to somebody else. <see cref="Revision"/> is the
/// optimistic concurrency token every edit must present through <c>If-Match</c>; it is incremented by
/// each accepted edit, which is what makes a stale edit a 409 rather than a silent overwrite.
/// <para>
/// Deleting a project is a soft archive: <see cref="ArchivedAt"/> is stamped and the row and its
/// milestones stay exactly as they are, so a historical record is never erased. An archived project
/// is read-only; only the owner can bring it back, and nothing else in this package does.
/// </para>
/// <para>
/// <see cref="RepoConnectionId"/> is a reference to a stored connection, never a credential, and
/// this package neither creates connections nor fetches anything. <see cref="ProjectUrl"/> is stored
/// as written by the owner and is deliberately never dereferenced: the runtime has no network call
/// in this package at all.
/// </para>
/// <para>
/// <see cref="ClientId"/> is the one optional agency client this project is grouped under
/// (AGENCY-CLIENT-PROJECTS-001), or <c>null</c> while it is a personal, unassigned project. At most
/// one client is ever named: the column is a single nullable foreign key, not a membership list.
/// </para>
/// </remarks>
public sealed class Project
{
    /// <summary>At most 120 characters; see the <c>projects.name</c> column.</summary>
    public const int NameMaxLength = 120;

    /// <summary>At most 2000 characters; see the <c>projects.description</c> column.</summary>
    public const int DescriptionMaxLength = 2000;

    /// <summary>At most 2000 characters; see the <c>projects.project_url</c> column.</summary>
    public const int ProjectUrlMaxLength = 2000;

    public Project(
        Guid id,
        Guid scopeId,
        string name,
        string description,
        Guid? repoConnectionId,
        string? projectUrl,
        long revision,
        DateTimeOffset? archivedAt = null,
        Guid? clientId = null)
    {
        Id = id;
        ScopeId = scopeId;
        Name = name;
        Description = description;
        RepoConnectionId = repoConnectionId;
        ProjectUrl = projectUrl;
        Revision = revision;
        ArchivedAt = archivedAt;
        ClientId = clientId;
    }

    public Guid Id { get; }

    /// <summary>The installation owner scope this record belongs to; never supplied by a caller.</summary>
    public Guid ScopeId { get; }

    /// <summary>1 to <see cref="NameMaxLength"/> characters after trimming.</summary>
    public string Name { get; }

    /// <summary>Free text, at most <see cref="DescriptionMaxLength"/> characters; never null.</summary>
    public string Description { get; }

    /// <summary>A stored connection reference, or <c>null</c>. Never a credential.</summary>
    public Guid? RepoConnectionId { get; }

    /// <summary>An http or https URL, stored as written. This package never fetches it.</summary>
    public string? ProjectUrl { get; }

    /// <summary>The optimistic revision; the first committed version is 1.</summary>
    public long Revision { get; }

    /// <summary>When the owner archived the project, or <c>null</c> while it is live.</summary>
    public DateTimeOffset? ArchivedAt { get; }

    /// <summary>
    /// The agency client this project is grouped under, or <c>null</c> while it is a personal,
    /// unassigned project. Never a login identity: clients never widen what the owner may read.
    /// </summary>
    public Guid? ClientId { get; }

    /// <summary>True when the project is archived, and therefore read-only.</summary>
    public bool IsArchived => ArchivedAt.HasValue;
}
