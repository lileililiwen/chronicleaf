using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Narrative;

/// <summary>
/// Everything the owner may write on a new narrative entry (NARRATIVE-MEMORY-001).
/// </summary>
/// <param name="Kind">One of <see cref="NarrativeKinds"/>.</param>
/// <param name="OccurredAt">When the recorded history happened; stored as UTC.</param>
/// <param name="Text">The owner's own words, 1 to <see cref="NarrativeEntry.TextMaxLength"/> characters.</param>
/// <param name="SourceEvidenceIds">Evidence item ids this line is drawn from; every one must belong to the project.</param>
/// <param name="Visibility">One of <see cref="NarrativeVisibility"/>.</param>
public sealed record CreateNarrativeEntryInput(
    string Kind,
    DateTimeOffset OccurredAt,
    string Text,
    IReadOnlyList<Guid> SourceEvidenceIds,
    string Visibility);

/// <summary>
/// A partial narrative entry edit. An absent field is left alone; citations replace wholesale when
/// present, because a citation list is a claim about sources and a merged list would be a claim
/// nobody stated (NARRATIVE-MEMORY-002).
/// </summary>
public sealed record NarrativeEntryPatch(
    string? Kind,
    DateTimeOffset? OccurredAt,
    string? Text,
    IReadOnlyList<Guid>? SourceEvidenceIds,
    bool ClearSourceEvidenceIds,
    string? Visibility);

/// <summary>One narrative entry as the read routes serve it.</summary>
public sealed record NarrativeEntryView(
    Guid Id,
    Guid ProjectId,
    string Kind,
    DateTimeOffset OccurredAt,
    string Text,
    IReadOnlyList<Guid> SourceEvidenceIds,
    long Revision,
    string Visibility)
{
    public static NarrativeEntryView From(NarrativeEntry entry) =>
        new(
            entry.Id,
            entry.ProjectId,
            entry.Kind,
            entry.OccurredAt,
            entry.Text,
            entry.SourceEvidenceIds,
            entry.Revision,
            entry.Visibility);
}

/// <summary>One page of narrative entries, ordered <c>occurredAt asc, id asc</c>.</summary>
public sealed record NarrativeEntryPage(IReadOnlyList<NarrativeEntryView> Items, string? NextCursor);

/// <summary>The generated project summary as the read routes serve it.</summary>
/// <param name="Stale">True when a source changed after this body was built.</param>
public sealed record ProjectSummaryView(
    Guid Id,
    Guid ProjectId,
    Guid? ThroughMilestoneId,
    string InputHash,
    string BodyJson,
    long Revision,
    bool Stale,
    DateTimeOffset UpdatedAt)
{
    public static ProjectSummaryView From(ProjectSummary summary) =>
        new(
            summary.Id,
            summary.ProjectId,
            summary.ThroughMilestoneId,
            summary.InputHash,
            summary.BodyJson,
            summary.Revision,
            summary.Stale,
            summary.UpdatedAt);
}

/// <summary>One attributed line inside a built narrative context.</summary>
/// <param name="Section">Which part of the context this line belongs to: history, decisions, assets or feedback.</param>
/// <param name="Ref">The stable reference a summary citation names: an entry, milestone, asset or evidence id.</param>
/// <param name="Text">The line itself, bounded with its siblings by the byte budget.</param>
public sealed record NarrativeContextLine(string Section, string Ref, string Text);

/// <summary>
/// The bounded, attributed context a draft may build on (NARRATIVE-MEMORY-001).
/// </summary>
/// <param name="ProjectId">The project this context describes.</param>
/// <param name="Cutoff">No milestone after this instant is included; a retrospective build.</param>
/// <param name="Lines">Chronological lines, every one carrying the id of the source it came from.</param>
/// <param name="Bytes">The UTF-8 size of the lines, always within <see cref="NarrativeMemoryPolicy.MaxContextBytes"/>.</param>
/// <param name="ThroughMilestoneId">The newest confirmed milestone covered, or null when none is.</param>
/// <param name="FeedbackUnknown">True when no feedback history exists: the context says unknown, never invented users.</param>
public sealed record NarrativeContextView(
    Guid ProjectId,
    DateTimeOffset Cutoff,
    IReadOnlyList<NarrativeContextLine> Lines,
    long Bytes,
    Guid? ThroughMilestoneId,
    bool FeedbackUnknown);

/// <summary>
/// What a summary refresh was asked to do (NARRATIVE-MEMORY-002).
/// </summary>
/// <param name="Cutoff">No milestone after this instant is covered; defaults to now.</param>
/// <param name="SelectedEntryIds">Private entries explicitly consented for this build; exportable entries need no selection.</param>
/// <param name="IdempotencyKey">1 to 200 characters; a replay answers with the original operation.</param>
public sealed record NarrativeRefreshRequest(
    DateTimeOffset? Cutoff,
    IReadOnlyList<Guid> SelectedEntryIds,
    string IdempotencyKey);

/// <summary>The storage contract for narrative entries and project summaries.</summary>
/// <remarks>
/// Every method is scoped through the owning project: a lookup for an id this owner may not reach
/// answers <c>null</c> rather than finding somebody else's record, and every write carries the
/// same scope in its WHERE clause. Entry writes are compare-and-set on the revision: a stale
/// writer loses rather than merging, and the caller learns it from the row count.
/// </remarks>
public interface INarrativeStore
{
    /// <summary>Reads one entry inside a scope, or <c>null</c> when it is absent or out of scope.</summary>
    Task<NarrativeEntry?> FindEntryAsync(Guid scopeId, Guid entryId, CancellationToken cancellationToken = default);

    /// <summary>Reads one project's entries ordered <c>occurredAt asc, id asc</c>, bounded by the shared list policy.</summary>
    Task<IReadOnlyList<NarrativeEntry>> ListEntriesAsync(
        Guid scopeId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>True when every cited evidence item belongs to this project inside this scope.</summary>
    Task<bool> CitationsBelongToProjectAsync(
        Guid scopeId,
        Guid projectId,
        IReadOnlyList<Guid> evidenceIds,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts an entry at revision 1. Returns false when the id already exists.</summary>
    Task<bool> InsertEntryAsync(NarrativeEntry entry, Guid scopeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies an edit at exactly <paramref name="expectedRevision"/>, advancing the revision by
    /// one. Returns how many rows changed: 0 means the write lost a race or the entry is gone, and
    /// the caller re-reads to say which.
    /// </summary>
    Task<int> UpdateEntryAsync(
        Guid scopeId,
        Guid entryId,
        long expectedRevision,
        string kind,
        DateTimeOffset occurredAt,
        string text,
        IReadOnlyList<Guid> sourceEvidenceIds,
        string visibility,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one project's summary, or <c>null</c> when no refresh has ever committed one.</summary>
    Task<ProjectSummary?> FindSummaryAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the summary row at revision 1, or reports that one already exists. The decision is
    /// the database's, made atomically by the unique index, so two refreshes that race still leave
    /// one row.
    /// </summary>
    /// <returns>True when this call created the row.</returns>
    Task<bool> TryInsertSummaryAsync(ProjectSummary summary, Guid scopeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the summary body at exactly <paramref name="expectedRevision"/>, advancing the
    /// revision by one and clearing the stale mark. Returns how many rows changed.
    /// </summary>
    Task<int> UpdateSummaryAsync(
        Guid scopeId,
        Guid projectId,
        long expectedRevision,
        Guid? throughMilestoneId,
        string inputHash,
        string bodyJson,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the summary stale without touching its body. Returns how many rows changed.</summary>
    Task<int> MarkSummaryStaleAsync(
        Guid scopeId,
        Guid projectId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the refresh request the enqueued job will read back. Written after the job exists
    /// because it names the job; a replay under the same idempotency key gets the original job
    /// back and its own insert changes no row.
    /// </summary>
    Task InsertRefreshRequestAsync(
        NarrativeRefreshRequestRow row,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the refresh request for one job inside a scope, or <c>null</c> when it is gone. A job
    /// whose row is gone cannot be refreshed honestly and says so rather than guessing.
    /// </summary>
    Task<NarrativeRefreshRequestRow?> FindRefreshRequestByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the summary document from attributed sources. The delivered implementation is
/// deterministic: it arranges owned history, decisions and citations into a document and invents
/// nothing, so a summary can never name a user, a fact or a source its inputs did not contain.
/// </summary>
public interface INarrativeSummaryBuilder
{
    /// <summary>The instruction version recorded on every body this builder produces.</summary>
    string PromptVersion { get; }

    Task<string> BuildAsync(
        NarrativeContextView context,
        IReadOnlyList<MilestoneNarrativeSource> milestones,
        CancellationToken cancellationToken = default);
}

/// <summary>One confirmed milestone as the summary builder receives it.</summary>
/// <param name="ApprovedExcerpt">The live-approved asset excerpt, or null when no approval is live.</param>
public sealed record MilestoneNarrativeSource(
    Guid Id,
    string Title,
    DateTimeOffset OccurredAt,
    string Type,
    long Revision,
    string? ApprovedExcerpt);

/// <summary>The narrative use cases the API layer calls (NARRATIVE-MEMORY-001/002).</summary>
public interface INarrativeService
{
    Task<NarrativeEntryView> CreateEntryAsync(
        OwnerScope owner,
        Guid projectId,
        CreateNarrativeEntryInput input,
        CancellationToken cancellationToken = default);

    Task<NarrativeEntryView> PatchEntryAsync(
        OwnerScope owner,
        Guid entryId,
        long expectedRevision,
        NarrativeEntryPatch patch,
        CancellationToken cancellationToken = default);

    Task<NarrativeEntryPage> ListEntriesAsync(
        OwnerScope owner,
        Guid projectId,
        int? limit,
        CancellationToken cancellationToken = default);

    Task<(NarrativeEntryPage Entries, ProjectSummaryView? Summary)> ReadNarrativeAsync(
        OwnerScope owner,
        Guid projectId,
        int? limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues a durable summary refresh and answers 202 with the operation. A replay under the
    /// same idempotency key answers with the original operation.
    /// </summary>
    Task<EnqueueOperationResult> RefreshSummaryAsync(
        OwnerScope owner,
        Guid projectId,
        NarrativeRefreshRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one enqueued refresh to completion. Called by the refresh handler through the
    /// operation queue; idempotent with respect to being run more than once.
    /// </summary>
    Task<NarrativeRefreshOutcome> RunRefreshAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The bounded context builder other packages call before drafting (NARRATIVE-MEMORY-001).
/// </summary>
/// <remarks>
/// Core drafts call this optional context only after this package is installed: the builder reads
/// ordered confirmed milestones, live-approved asset excerpts and explicit narrative entries with
/// their source ids, all bounded by <see cref="NarrativeMemoryPolicy.MaxContextBytes"/>. Unknown
/// history is labelled unknown, never inferred; a retrospective cutoff excludes later milestones.
/// </remarks>
public interface IProjectNarrativeContext
{
    Task<NarrativeContextView> BuildAsync(
        OwnerScope owner,
        Guid projectId,
        DateTimeOffset cutoff,
        long maxBytes,
        IReadOnlyList<Guid> selectedEntryIds,
        CancellationToken cancellationToken = default);
}

/// <summary>The bounds one narrative context runs inside (NARRATIVE-MEMORY-001).</summary>
public static class NarrativeMemoryPolicy
{
    /// <summary>
    /// The byte budget a built context must fit. A constant rather than a setting: raising it
    /// would let one build carry unbounded history into a call the owner is billed for.
    /// </summary>
    public const long MaxContextBytes = 64 * 1024;

    /// <summary>How many of the latest confirmed milestones the preferred selection carries.</summary>
    public const int PreferredMilestoneCount = 10;

    /// <summary>The instruction version the refresh records on every summary it commits.</summary>
    public const string PromptVersion = "narrative-memory/1";

    /// <summary>How many entries one list page carries at most.</summary>
    public const int MaxListLimit = 100;
}
