namespace Chronicleaf.Core;

/// <summary>
/// The kinds of explicit project history an owner may record (NARRATIVE-MEMORY-001).
/// </summary>
/// <remarks>
/// A kind is a fixed vocabulary, not free text, so a reader of the narrative context can tell what a
/// line is without guessing from its prose: an <c>origin</c> says where the project came from, an
/// <c>audience</c> says who it is for, a <c>technical-route</c> says how it is built, a
/// <c>decision</c> records a choice the owner stands behind, a <c>problem</c> records what hurts,
/// and <c>feedback</c> records what users said. A <c>decision</c> is pinned: the context builder
/// never silently drops one to fit the byte budget, because a choice the owner recorded is exactly
/// the fact a later draft must not lose.
/// </remarks>
public static class NarrativeKinds
{
    public const string Origin = "origin";
    public const string Audience = "audience";
    public const string TechnicalRoute = "technical-route";
    public const string Decision = "decision";
    public const string Problem = "problem";
    public const string Feedback = "feedback";

    public static IReadOnlyList<string> All { get; } =
        [Origin, Audience, TechnicalRoute, Decision, Problem, Feedback];

    public static bool IsKnown(string? kind) =>
        All.Contains(kind, StringComparer.Ordinal);

    /// <summary>True for the kinds the context builder prefers when the budget is tight.</summary>
    public static bool IsPreferred(string kind) =>
        string.Equals(kind, Origin, StringComparison.Ordinal)
        || string.Equals(kind, Audience, StringComparison.Ordinal);

    /// <summary>True for the kinds the context builder never silently drops.</summary>
    public static bool IsPinned(string kind) =>
        string.Equals(kind, Decision, StringComparison.Ordinal);
}

/// <summary>
/// Who may read a narrative entry (NARRATIVE-MEMORY-002).
/// </summary>
/// <remarks>
/// <c>private</c> entries stay inside the owner's own context and reach a model or a public
/// projection only when explicitly selected for that purpose. <c>exportable</c> entries may be
/// included in consented model input and in the public Journey without a per-entry selection.
/// There is no inheritance: a private entry is never carried into a public output because an
/// exportable neighbour mentioned it.
/// </remarks>
public static class NarrativeVisibility
{
    public const string Private = "private";
    public const string Exportable = "exportable";

    public static IReadOnlyList<string> All { get; } = [Private, Exportable];

    public static bool IsKnown(string? visibility) =>
        All.Contains(visibility, StringComparer.Ordinal);
}

/// <summary>
/// One explicit, owner-written line of project history (NARRATIVE-MEMORY-001).
/// </summary>
/// <remarks>
/// The entry is only ever the owner's: it is written by hand through the narrative routes, and no
/// automated process creates, edits or deletes one. <see cref="SourceEvidenceIds"/> names the
/// evidence items the line is drawn from; every id must belong to the same project, so a line can
/// never cite material the owner never collected. The scope is the owning project's scope, checked
/// through the project on every read and write, exactly as for a milestone.
/// <para>
/// <see cref="Revision"/> is the optimistic concurrency token, exactly as for a project: an edit
/// presents the revision it read and the database advances it, so two edits that present the same
/// revision cannot both land. A stale writer loses rather than merging, because a merge of two
/// histories would be a history nobody wrote.
/// </para>
/// </remarks>
public sealed class NarrativeEntry
{
    /// <summary>At most <see cref="TextMaxLength"/> characters after trimming.</summary>
    public const int TextMaxLength = 8000;

    public NarrativeEntry(
        Guid id,
        Guid projectId,
        string kind,
        DateTimeOffset occurredAt,
        string text,
        IReadOnlyList<Guid> sourceEvidenceIds,
        long revision,
        string visibility)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A narrative entry needs a non-empty id.", nameof(id));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A narrative entry needs a non-empty project.", nameof(projectId));
        }

        if (!NarrativeKinds.IsKnown(kind))
        {
            throw new ArgumentException("The kind is not one this product stores.", nameof(kind));
        }

        if (!NarrativeVisibility.IsKnown(visibility))
        {
            throw new ArgumentException("The visibility is not one this product stores.", nameof(visibility));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var trimmed = text.Trim();
        if (trimmed.Length > TextMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                $"A narrative entry is at most {TextMaxLength} characters.");
        }

        ArgumentNullException.ThrowIfNull(sourceEvidenceIds);
        if (sourceEvidenceIds.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("An evidence reference needs a non-empty id.", nameof(sourceEvidenceIds));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "The first stored revision is 1.");
        }

        Id = id;
        ProjectId = projectId;
        Kind = kind;
        OccurredAt = occurredAt;
        Text = trimmed;
        SourceEvidenceIds = sourceEvidenceIds.Distinct().OrderBy(id => id).ToArray();
        Revision = revision;
        Visibility = visibility;
    }

    public Guid Id { get; }

    /// <summary>The project this entry belongs to; the project decides the scope.</summary>
    public Guid ProjectId { get; }

    /// <summary>One of <see cref="NarrativeKinds"/>.</summary>
    public string Kind { get; }

    /// <summary>When the recorded history happened; always UTC.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>The owner's own words, trimmed, at most <see cref="TextMaxLength"/> characters.</summary>
    public string Text { get; }

    /// <summary>The evidence item ids this line is drawn from, distinct and ordered.</summary>
    public IReadOnlyList<Guid> SourceEvidenceIds { get; }

    /// <summary>The optimistic concurrency token: 1 at creation, advanced by the database on edit.</summary>
    public long Revision { get; }

    /// <summary>One of <see cref="NarrativeVisibility"/>.</summary>
    public string Visibility { get; }
}

/// <summary>
/// One narrative refresh request: the values an owner stated when they asked for a refresh, kept
/// where the job that runs can read them (NARRATIVE-MEMORY-002).
/// </summary>
/// <remarks>
/// This row exists for the same reason <c>content_draft_requests</c> does: a queued job carries an
/// operation id and nothing else, so the cutoff and the consented private-entry selection have to
/// be durable somewhere the worker can read back. A handler that held them in memory would be a
/// handler that could not survive the restart its whole queue is built around.
/// <para>
/// What is here is a bound, never prose: the cutoff instant and the ids of explicitly selected
/// private entries. The history itself is re-read from the sources inside the run, so a refresh
/// always summarises the current sources rather than the sources as they were at request time.
/// </para>
/// </remarks>
/// <param name="id">This request's identifier.</param>
/// <param name="scopeId">The owning scope; the worker's lookup is scoped by it.</param>
/// <param name="operationId">The job that will perform the refresh.</param>
/// <param name="projectId">The project the summary describes.</param>
/// <param name="cutoff">No milestone after this instant is covered; UTC.</param>
/// <param name="selectedEntryIds">Private entries explicitly consented for this build, ordered.</param>
/// <param name="createdAt">When the request was accepted; UTC.</param>
public sealed class NarrativeRefreshRequestRow
{
    public NarrativeRefreshRequestRow(
        Guid id,
        Guid scopeId,
        Guid operationId,
        Guid projectId,
        DateTimeOffset cutoff,
        IReadOnlyList<Guid> selectedEntryIds,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A refresh request needs a non-empty id.", nameof(id));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A refresh request needs the job that will run it.", nameof(operationId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A refresh request needs a non-empty project.", nameof(projectId));
        }

        ArgumentNullException.ThrowIfNull(selectedEntryIds);
        if (selectedEntryIds.Any(entryId => entryId == Guid.Empty))
        {
            throw new ArgumentException("A selected entry needs a non-empty id.", nameof(selectedEntryIds));
        }

        Id = id;
        ScopeId = scopeId;
        OperationId = operationId;
        ProjectId = projectId;
        Cutoff = cutoff;
        SelectedEntryIds = selectedEntryIds.Distinct().OrderBy(entryId => entryId).ToArray();
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid OperationId { get; }

    public Guid ProjectId { get; }

    public DateTimeOffset Cutoff { get; }

    public IReadOnlyList<Guid> SelectedEntryIds { get; }

    public DateTimeOffset CreatedAt { get; }
}

/// <summary>
/// The generated project summary: what the last refresh concluded from the attributed sources
/// (NARRATIVE-MEMORY-002).
/// <remarks>
/// One row per project: a refresh recomputes the input hash from the sources and either keeps the
/// stored body (same hash) or regenerates it (new hash, new revision). <see cref="Stale"/> is set
/// when a source the body was built from changed after the build - a corrected narrative entry, a
/// revised milestone - and cleared by the regeneration that reads the new sources. A refresh that
/// fails to build keeps the prior row with <see cref="Stale"/> set rather than deleting it or
/// writing a body it did not produce, so a failed regeneration is a labelled old answer rather
/// than a missing one.
/// <para>
/// <see cref="ThroughMilestoneId"/> names the newest confirmed milestone the body covers, or null
/// when the body covers no milestone (a retrospective cutoff that precedes every milestone, or a
/// project with no confirmed history yet). A cutoff forbids future milestones in a retrospective
/// build: a milestone after the cutoff is excluded, never summarised.
/// </para>
/// </remarks>
public sealed class ProjectSummary
{
    public ProjectSummary(
        Guid id,
        Guid projectId,
        Guid? throughMilestoneId,
        string inputHash,
        string bodyJson,
        long revision,
        bool stale,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A project summary needs a non-empty id.", nameof(id));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A project summary needs a non-empty project.", nameof(projectId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(inputHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyJson);

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "The first stored revision is 1.");
        }

        Id = id;
        ProjectId = projectId;
        ThroughMilestoneId = throughMilestoneId;
        InputHash = inputHash;
        BodyJson = bodyJson;
        Revision = revision;
        Stale = stale;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    /// <summary>The project this summary describes; the project decides the scope.</summary>
    public Guid ProjectId { get; }

    /// <summary>The newest confirmed milestone the body covers, or null when it covers none.</summary>
    public Guid? ThroughMilestoneId { get; }

    /// <summary>Lowercase SHA-256 hex over the canonical source fingerprint the body was built from.</summary>
    public string InputHash { get; }

    /// <summary>The generated summary document, canonical JSON with citations to owned sources.</summary>
    public string BodyJson { get; }

    /// <summary>The optimistic concurrency token: 1 at first build, advanced on regeneration.</summary>
    public long Revision { get; }

    /// <summary>True when a source changed after this body was built and no regeneration has run since.</summary>
    public bool Stale { get; }

    /// <summary>When this row was last written; always UTC.</summary>
    public DateTimeOffset UpdatedAt { get; }
}
