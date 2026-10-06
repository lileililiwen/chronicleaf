namespace Chronicleaf.Core;

/// <summary>
/// One agency client the owner groups projects under (AGENCY-CLIENT-PROJECTS-001).
/// </summary>
/// <remarks>
/// A client is organizational context, not a login identity: <see cref="ScopeId"/> is the
/// installation's own owner scope, so a lookup by id alone can never reach a record that belongs
/// to somebody else. <see cref="Revision"/> is the optimistic concurrency token every client edit
/// must present through <c>If-Match</c>; it is incremented by each accepted client edit.
/// <para>
/// <see cref="ContextRevision"/> is the second counter, and it is the one project membership
/// moves: every assignment or removal of a project touches the old client's and the new client's
/// context revision, while leaving both client revisions alone. A report context therefore pins a
/// context revision, and a membership change invalidates exactly the reports that pinned the old
/// one - without making a client edit the owner holds stale.
/// </para>
/// <para>
/// Deleting a client is a soft archive: <see cref="ArchivedAt"/> is stamped and the row and its
/// projects stay exactly as they are, so a historical tree is never erased. An archived client
/// refuses new assignments but keeps its existing ones readable; only moving a project away (or
/// clearing it) is still allowed, so no project can be stranded.
/// </para>
/// </remarks>
public sealed class Client
{
    /// <summary>At most 160 characters; see the <c>clients.name</c> column.</summary>
    public const int NameMaxLength = 160;

    /// <summary>At most 20000 plain-text characters; see the <c>clients.notes</c> column.</summary>
    public const int NotesMaxLength = 20000;

    public Client(
        Guid id,
        Guid scopeId,
        string name,
        string notes,
        long revision,
        long contextRevision,
        DateTimeOffset? archivedAt = null)
    {
        Id = id;
        ScopeId = scopeId;
        Name = name;
        Notes = notes;
        Revision = revision;
        ContextRevision = contextRevision;
        ArchivedAt = archivedAt;
    }

    public Guid Id { get; }

    /// <summary>The installation owner scope this record belongs to; never supplied by a caller.</summary>
    public Guid ScopeId { get; }

    /// <summary>1 to <see cref="NameMaxLength"/> characters after trimming.</summary>
    public string Name { get; }

    /// <summary>Free text, at most <see cref="NotesMaxLength"/> characters; never null.</summary>
    public string Notes { get; }

    /// <summary>The optimistic revision; the first committed version is 1.</summary>
    public long Revision { get; }

    /// <summary>
    /// The membership generation; the first committed version is 1 and every project assignment or
    /// removal advances it. A report context pins this value, not <see cref="Revision"/>.
    /// </summary>
    public long ContextRevision { get; }

    /// <summary>When the owner archived the client, or <c>null</c> while it is live.</summary>
    public DateTimeOffset? ArchivedAt { get; }

    /// <summary>True when the client is archived, and therefore refuses new assignments.</summary>
    public bool IsArchived => ArchivedAt.HasValue;
}
