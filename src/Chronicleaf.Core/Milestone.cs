namespace Chronicleaf.Core;

/// <summary>
/// One milestone in a project's history (PROJECTS-AND-MILESTONES-001).
/// </summary>
/// <remarks>
/// A milestone is only ever the owner's. A record the owner wrote by hand becomes
/// <see cref="MilestoneStatus.Confirmed"/> because the owner pressed save, and a record that arrived
/// from anywhere else begins as <see cref="MilestoneStatus.Candidate"/> and stays that way until
/// the owner confirms it: no automated process in this package can confirm, dismiss or change a
/// field, and there is no route that would let one.
/// <para>
/// <see cref="Source"/> names where the record came from and <see cref="SourceKey"/> is the key it
/// carries in that source. The pair is unique per project while a key is present, which is what
/// makes a repeated import of the same upstream record idempotent: the second import returns the
/// candidate that already exists instead of creating a twin. A manually written milestone has no
/// source key, so the uniqueness never constrains the owner.
/// </para>
/// <para>
/// <see cref="Revision"/> is the optimistic concurrency token, exactly as for a
/// <see cref="Project"/>. <see cref="OccurredAt"/> is always UTC; the offset a caller submitted is
/// normalized before it is stored, so two spellings of the same instant cannot be ordered
/// differently by a list.
/// </para>
/// </remarks>
public sealed class Milestone
{
    /// <summary>1 to <see cref="TitleMaxLength"/> characters after trimming.</summary>
    public const int TitleMaxLength = 160;

    /// <summary>At most <see cref="NotesMaxLength"/> characters; see the <c>milestones.notes</c> column.</summary>
    public const int NotesMaxLength = 20_000;

    /// <summary>A free-text version label, at most <see cref="VersionMaxLength"/> characters.</summary>
    public const int VersionMaxLength = 80;

    /// <summary>At most <see cref="ListItemMaxLength"/> characters per known limitation or next step.</summary>
    public const int ListItemMaxLength = 500;

    /// <summary>At most <see cref="ListItemMax"/> known limitations and at most that many next steps.</summary>
    public const int ListItemMax = 50;

    public Milestone(
        Guid id,
        Guid projectId,
        string title,
        string? version,
        DateTimeOffset occurredAt,
        string type,
        string source,
        string? sourceKey,
        string status,
        long revision,
        string notes,
        IReadOnlyList<string> knownLimitations,
        IReadOnlyList<string> nextSteps)
    {
        Id = id;
        ProjectId = projectId;
        Title = title;
        Version = version;
        OccurredAt = occurredAt;
        Type = type;
        Source = source;
        SourceKey = sourceKey;
        Status = status;
        Revision = revision;
        Notes = notes;
        KnownLimitations = knownLimitations;
        NextSteps = nextSteps;
    }

    public Guid Id { get; }

    /// <summary>The project this milestone belongs to; the project decides the scope.</summary>
    public Guid ProjectId { get; }

    /// <summary>1 to <see cref="TitleMaxLength"/> characters after trimming.</summary>
    public string Title { get; }

    /// <summary>A free-text version label such as <c>v1.4.0</c>, or <c>null</c> when there is none.</summary>
    public string? Version { get; }

    /// <summary>When the milestone happened, always in UTC.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>One of the fixed <see cref="MilestoneTypes"/> values.</summary>
    public string Type { get; }

    /// <summary>One of <see cref="MilestoneSource.Manual"/> or a later package's import source.</summary>
    public string Source { get; }

    /// <summary>The key this record carries in <see cref="Source"/>, or <c>null</c> when it has none.</summary>
    public string? SourceKey { get; }

    /// <summary>One of the fixed <see cref="MilestoneStatus"/> values.</summary>
    public string Status { get; }

    /// <summary>The optimistic revision; the first committed version is 1.</summary>
    public long Revision { get; }

    /// <summary>Free text, at most <see cref="NotesMaxLength"/> characters; never null.</summary>
    public string Notes { get; }

    /// <summary>What the owner knows does not work yet; ordered as written.</summary>
    public IReadOnlyList<string> KnownLimitations { get; }

    /// <summary>What the owner intends to do next; ordered as written.</summary>
    public IReadOnlyList<string> NextSteps { get; }

    /// <summary>True while the record still waits for the owner's confirmation.</summary>
    public bool IsCandidate => string.Equals(Status, MilestoneStatus.Candidate, StringComparison.Ordinal);

    /// <summary>True once the owner confirmed it, and only then.</summary>
    public bool IsConfirmed => string.Equals(Status, MilestoneStatus.Confirmed, StringComparison.Ordinal);

    /// <summary>True when the owner dismissed it; returning it needs an explicit owner action.</summary>
    public bool IsDismissed => string.Equals(Status, MilestoneStatus.Dismissed, StringComparison.Ordinal);
}
