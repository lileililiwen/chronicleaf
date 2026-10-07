namespace Chronicleaf.Core;

/// <summary>
/// One evidence-based weekly progress report for one agency client
/// (AGENCY-WEEKLY-REPORTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// A report is a deterministic aggregation, never a generation: the sections name confirmed
/// milestones whose <c>occurredAt</c> falls inside the report's UTC window, the complete
/// screenshot items of those milestones' evidence heads, and the owner's own explicit
/// in-progress and next-week notes. There is no provider behind a report, so there is no
/// consent to check, no key to lease and no byte that could reach a model - a project from
/// another client is refused before the render rather than redacted after one.
/// </para>
/// <para>
/// Rows are append-only: a regeneration inserts a new row at the next revision for the same
/// <c>(scope, client, week)</c> and never rewrites one. The approval is a stamp on one row
/// binding that row's revision, body hash, attribution hash and source refs; anything that
/// moves afterwards - a project regrouped, an evidence head advanced - makes the stamp
/// describe a state that no longer holds, which is exactly what the export re-checks.
/// </para>
/// </remarks>
/// <param name="Id">This report row's identity.</param>
/// <param name="ScopeId">The owning scope. Every read of this row is scoped by it.</param>
/// <param name="ClientId">The agency client this report is about.</param>
/// <param name="WeekStart">The first local calendar date of the seven-day window (YYYY-MM-DD).</param>
/// <param name="TimeZone">The IANA time zone those calendar dates are read in.</param>
/// <param name="WindowStartUtc">The inclusive UTC instant the window opens.</param>
/// <param name="WindowEndUtc">The exclusive UTC instant the window closes.</param>
/// <param name="Revision">The per-client-week revision; the first generation is 1.</param>
/// <param name="ProjectIds">The client projects the report covers, in id order.</param>
/// <param name="AttributionHash">Lowercase hex SHA-256 over the client, its membership generation and the project set.</param>
/// <param name="SourceRefsJson">The canonical per-project milestone evidence bindings the body was rendered from.</param>
/// <param name="InputsHash">Lowercase hex SHA-256 over the canonical generation inputs, for idempotent replay.</param>
/// <param name="IdempotencyKey">The caller's key, or null when the call carried none.</param>
/// <param name="BodyMarkdown">The rendered report, exactly as approved and exported.</param>
/// <param name="BodyHash">Lowercase hex SHA-256 over the UTF-8 bytes of <paramref name="BodyMarkdown"/>.</param>
/// <param name="InProgressNotes">The owner's explicit in-progress notes, or empty.</param>
/// <param name="NextWeekNotes">The owner's explicit next-week notes, or empty.</param>
/// <param name="ApprovedRevision">The revision the live approval names, or null when never approved.</param>
/// <param name="ApprovedBodyHash">The body hash the live approval names, or null.</param>
/// <param name="ApprovedAttributionHash">The attribution hash the live approval names, or null.</param>
/// <param name="ApprovedSourceRefsJson">The source refs the live approval names, or null.</param>
/// <param name="ApprovedAt">When the live approval was recorded, or null.</param>
/// <param name="CreatedAt">When this row was inserted; UTC.</param>
public sealed class WeeklyReport(
    Guid id,
    Guid scopeId,
    Guid clientId,
    string weekStart,
    string timeZone,
    DateTimeOffset windowStartUtc,
    DateTimeOffset windowEndUtc,
    long revision,
    IReadOnlyList<Guid> projectIds,
    string attributionHash,
    string sourceRefsJson,
    string inputsHash,
    string? idempotencyKey,
    string bodyMarkdown,
    string bodyHash,
    string inProgressNotes,
    string nextWeekNotes,
    long? approvedRevision,
    string? approvedBodyHash,
    string? approvedAttributionHash,
    string? approvedSourceRefsJson,
    DateTimeOffset? approvedAt,
    DateTimeOffset createdAt)
{
    /// <summary>At most this many projects in one report; a wider selection is two reports.</summary>
    public const int MaxProjects = 50;

    /// <summary>At most this many characters in one owner note field.</summary>
    public const int MaxNoteChars = 5000;

    /// <summary>At most this many characters in a caller idempotency key.</summary>
    public const int IdempotencyKeyMaxLength = 200;

    public Guid Id { get; } = id;

    public Guid ScopeId { get; } = scopeId;

    public Guid ClientId { get; } = clientId;

    public string WeekStart { get; } = weekStart;

    public string TimeZone { get; } = timeZone;

    public DateTimeOffset WindowStartUtc { get; } = windowStartUtc;

    public DateTimeOffset WindowEndUtc { get; } = windowEndUtc;

    public long Revision { get; } = revision;

    public IReadOnlyList<Guid> ProjectIds { get; } = projectIds;

    public string AttributionHash { get; } = attributionHash;

    public string SourceRefsJson { get; } = sourceRefsJson;

    public string InputsHash { get; } = inputsHash;

    public string? IdempotencyKey { get; } = idempotencyKey;

    public string BodyMarkdown { get; } = bodyMarkdown;

    public string BodyHash { get; } = bodyHash;

    public string InProgressNotes { get; } = inProgressNotes;

    public string NextWeekNotes { get; } = nextWeekNotes;

    public long? ApprovedRevision { get; } = approvedRevision;

    public string? ApprovedBodyHash { get; } = approvedBodyHash;

    public string? ApprovedAttributionHash { get; } = approvedAttributionHash;

    public string? ApprovedSourceRefsJson { get; } = approvedSourceRefsJson;

    public DateTimeOffset? ApprovedAt { get; } = approvedAt;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    /// <summary>True once an approval stamp names this row's own revision.</summary>
    public bool IsApproved =>
        ApprovedRevision == Revision
        && string.Equals(ApprovedBodyHash, BodyHash, StringComparison.Ordinal);
}
