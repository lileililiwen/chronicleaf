using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Narrative;

/// <summary>
/// The codes this package adds to the delivered error envelope, and the codes it writes to a job's
/// operation row (NARRATIVE-MEMORY-001/002).
/// </summary>
/// <remarks>
/// The envelope is the delivered <c>{code,message,traceId,fieldErrors?}</c> and its status mapping
/// is unchanged; this file is vocabulary, not a second envelope. Every message is authored here in
/// fixed words, because the middleware writes <c>Exception.Message</c> verbatim into the envelope
/// and into the log line beside it. A message that interpolated an entry's text, a milestone's
/// notes or a summary's body would publish all three - and a summary's whole purpose is to describe
/// history the owner trusts, not to leak it into logs.
/// <para>
/// <b>The two vocabularies are kept apart on purpose.</b> The envelope codes answer a request; the
/// job codes answer a refresh run that has already been accepted. A refresh whose build failed is a
/// job that ended with the prior summary kept and marked stale, not a request that was refused.
/// </para>
/// </remarks>
public static class NarrativeError
{
    /// <summary>Semantic validation failed (422), with per-field detail. The delivered code, reused.</summary>
    public const string ValidationFailed = ApiError.ValidationFailed;

    /// <summary>The resource is absent, or belongs to another scope (404). The delivered code, reused.</summary>
    public const string NotFound = ApiError.NotFound;

    /// <summary>The supplied revision no longer matches the stored revision (409). Delivered, reused.</summary>
    public const string RevisionConflict = ApiError.RevisionConflict;

    /// <summary>A mutation that requires <c>If-Match</c> arrived without it (428). Delivered, reused.</summary>
    public const string IfMatchRequired = ApiError.IfMatchRequired;

    /// <summary>
    /// The attributed context does not fit the byte budget and a pinned fact would be lost by
    /// shrinking it (422).
    /// </summary>
    /// <remarks>
    /// A requirement rather than a capacity limit: the context builder prefers origin and audience
    /// entries, the latest confirmed milestones and pinned decisions, and refuses - rather than
    /// silently dropping a pinned decision - when even that selection overflows. The remedy is to
    /// narrow the cutoff or shorten entries, never to let a draft proceed on a context that lost a
    /// fact without saying so.
    /// </remarks>
    public const string ContextTooLarge = "narrative_context_too_large";

    /// <summary>
    /// An entry cites an evidence item that is absent or belongs to another project (404).
    /// </summary>
    /// <remarks>
    /// Deliberately the delivered <c>not_found</c> rather than a new code: a caller learns that
    /// there is nothing citable here and nothing about what might be elsewhere, which is the same
    /// answer every other scoped lookup gives. The exception carries which position failed; the id
    /// itself is the owner's own reference and is not echoed.
    /// </remarks>
    public const string ForeignEvidence = ApiError.NotFound;

    // ---------------------------------------------------------------------------------------------
    // Job error codes. Written to the operation row, never to a response body, and deliberately
    // without a status in ApiErrorStatus: no route answers a refresh that already failed.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The summary build threw, so the prior summary was kept and marked stale.</summary>
    public const string BuildFailed = "narrative_summary_build_failed";

    /// <summary>The project the refresh was asked for is gone, so there is nothing to summarise.</summary>
    public const string ProjectGone = "narrative_project_gone";
}

/// <summary>The attributed context does not fit the byte budget without losing a pinned fact (422).</summary>
public sealed class NarrativeContextTooLargeException(long contextBytes, long budgetBytes)
    : ApiException(
        NarrativeError.ContextTooLarge,
        "The attributed narrative context does not fit the byte budget. Narrow the cutoff or shorten entries and ask again.")
{
    /// <summary>How many bytes the selection needed. The prose itself is never carried here.</summary>
    public long ContextBytes { get; } = contextBytes;

    /// <summary>The budget the selection was measured against.</summary>
    public long BudgetBytes { get; } = budgetBytes;
}

/// <summary>An entry cites an evidence item that is absent or belongs to another project (404).</summary>
public sealed class NarrativeForeignEvidenceException(int position)
    : ApiException(
        NarrativeError.ForeignEvidence,
        "A cited evidence item does not belong to this project.")
{
    /// <summary>The 0-based position in the citation list that failed. The id is not echoed.</summary>
    public int Position { get; } = position;
}
