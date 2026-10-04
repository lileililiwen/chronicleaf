using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Capture;

/// <summary>
/// What an owner asks a milestone to photograph, as the service receives it (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// The exact design shape. Three bounds are enforced before a job exists rather than by the job: the
/// URL count, the viewport count and the wait, because a request that could never run is better
/// answered at validation than as a failed job an owner has to go and read.
/// </remarks>
/// <param name="Urls">
/// The URLs to capture, one to <see cref="CaptureBounds.MaximumPagesPerJob"/>. These are absolute
/// http or https URLs rather than paths, because the design's own uniqueness tuple is
/// <c>(operation_id, url, viewport)</c> - a full URL is the thing that has to be unique, so it is the
/// thing that is stored and the thing that is compared.
/// </param>
/// <param name="Viewports">
/// The viewports to capture, one to <see cref="CaptureBounds.MaximumViewportsPerJob"/>.
/// </param>
/// <param name="WaitSelector">An optional selector to wait for before each picture.</param>
/// <param name="WaitMilliseconds">How long to settle after each page loads.</param>
/// <param name="FullPage">Whether to capture the whole scrollable page.</param>
/// <param name="ExpectedBundleRevision">
/// The evidence revision the owner was looking at. Recorded on the request row and re-checked before
/// a candidate is committed, so a capture that lands after somebody else appended is reported as
/// superseded rather than quietly attached to a bundle nobody chose it for.
/// </param>
/// <param name="IdempotencyKey">The caller's key, unique within the scope and kind.</param>
public sealed record ScreenshotCaptureRequest(
    IReadOnlyList<Uri> Urls,
    IReadOnlyList<CaptureViewport> Viewports,
    string? WaitSelector,
    int WaitMilliseconds,
    bool FullPage,
    long ExpectedBundleRevision,
    string IdempotencyKey);

/// <summary>
/// The 202 a capture request answers with (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <param name="Operation">The operation that will do the capture, or the original of a replay.</param>
/// <param name="Created">False when this idempotency key had already created this capture.</param>
public sealed record ScreenshotCaptureAccepted(Operation Operation, bool Created)
{
    /// <summary>Where an owner reads this job's progress. Published rather than left to be built.</summary>
    public string OperationUrl => $"/api/operations/{Operation.Id}";
}

/// <summary>
/// One page in a capture job, and how it went (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// A per-page result exists because a job captures up to six pictures - three paths, two viewports -
/// and the design permits a partial outcome. <b>A job that captured two of three and refused the
/// third is not a success and not a failure:</b> it is two candidates that exist and one explicit
/// failure that says why, and the distinction is carried by the presence or absence of a
/// <see cref="CapturePageFailure"/> rather than by a flag on the run.
/// </remarks>
/// <param name="Path">The URL that was asked for, as the string the uniqueness tuple compares.</param>
/// <param name="Viewport">The viewport it was asked at.</param>
/// <param name="CandidateId">The candidate that was written, when one was.</param>
/// <param name="Failure">Why this page produced nothing, when it did.</param>
public sealed record CapturePageOutcome(
    string Path,
    string Viewport,
    Guid? CandidateId,
    CapturePageFailure? Failure)
{
    /// <summary>True when this page produced a stored candidate.</summary>
    public bool Succeeded => CandidateId is not null && Failure is null;
}

/// <summary>
/// Why one page of a capture produced nothing (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// A closed set of words rather than a provider's message, and never a host, an address or a page's
/// own error text. "This page was blocked" and "this page was slow" are different problems for an
/// owner and a panel that renders both as "could not be captured" is hiding the only information
/// there is.
/// </remarks>
/// <param name="Code">One of the <c>page_*</c> codes on <see cref="CapturePageError"/>.</param>
/// <param name="Path">The URL that failed, as the same string the candidate row would have stored.</param>
/// <param name="Viewport">The viewport it was asked at.</param>
public sealed record CapturePageFailure(string Code, string Path, string Viewport);

/// <summary>
/// The per-page failure codes a capture job records (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// Deliberately not envelope codes and deliberately not operation codes. A page failure is a result of
/// a job that may otherwise have succeeded, so it lives in the job's own vocabulary: there is no HTTP
/// response to put it in and no terminal operation status it corresponds to.
/// </remarks>
public static class CapturePageError
{
    /// <summary>The page, or something it pulled in, pointed somewhere the policy refuses.</summary>
    public const string TargetBlocked = "page_target_blocked";

    /// <summary>This page did not load inside the job limit.</summary>
    public const string TimedOut = "page_timeout";

    /// <summary>The page produced no picture at all - an error page, a download, an empty document.</summary>
    public const string NotRendered = "page_not_rendered";

    /// <summary>The picture was over the byte or pixel bound.</summary>
    public const string TooLarge = "page_too_large";

    /// <summary>The sandbox was not usable when this page came up, so it was not visited.</summary>
    public const string SandboxUnavailable = "page_sandbox_unavailable";

    /// <summary>
    /// A candidate for this exact page at this viewport already exists from this job, so nothing was
    /// written twice.
    /// </summary>
    public const string Duplicate = "page_duplicate";
}

/// <summary>
/// What one capture job produced, as its result reference decodes (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// The result reference is a bounded string on the operation row, and this is what it names: how many
/// pages were asked for, how many produced a candidate, how many were explicitly refused, and the
/// per-page lines. The ids are stored, so a reader can fetch the candidates through the delivered read
/// route without this string carrying a URL or a media id.
/// </remarks>
/// <param name="Requested">How many page-captures the job asked for.</param>
/// <param name="Captured">How many produced a stored candidate.</param>
/// <param name="Failed">The pages that were explicitly refused, each with its own reason.</param>
/// <param name="Pages">The per-page outcomes, in the order they were attempted.</param>
public sealed record CaptureJobOutcome(
    int Requested,
    int Captured,
    IReadOnlyList<CapturePageFailure> Failed,
    IReadOnlyList<CapturePageOutcome> Pages)
{
    /// <summary>Builds the outcome from the pages, so the three counts cannot disagree.</summary>
    public static CaptureJobOutcome FromPages(IReadOnlyList<CapturePageOutcome> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        var captured = 0;
        List<CapturePageFailure>? failed = null;

        foreach (var page in pages)
        {
            if (page.Succeeded)
            {
                captured++;
            }
            else if (page.Failure is { } failure)
            {
                (failed ??= []).Add(failure);
            }
        }

        return new CaptureJobOutcome(pages.Count, captured, failed ?? [], pages);
    }
}

/// <summary>
/// The capture use cases the API layer and the job call (SCREENSHOT-CAPTURE-001).
/// </summary>
public interface IScreenshotCaptureService
{
    /// <summary>
    /// Validates a capture request and creates the job that will run it.
    /// </summary>
    /// <remarks>
    /// Every page-capture is checked against the URL policy before the job exists, so a request for a
    /// destination that cannot be photographed is refused here rather than becoming a job that can only
    /// fail. What the job then re-checks is a different thing: the same policy applied to the address
    /// the name resolves to at that later moment.
    /// </remarks>
    /// <exception cref="ResourceNotFoundException">The milestone is absent or in another scope (404).</exception>
    /// <exception cref="ProjectArchivedException">The milestone's project is archived (409).</exception>
    /// <exception cref="ValidationFailedException">A field is not one this package accepts (422).</exception>
    /// <exception cref="CaptureTargetBlockedException">A destination is refused by the policy (403).</exception>
    /// <exception cref="PayloadTooLargeException">The request is over a bound (413).</exception>
    Task<ScreenshotCaptureAccepted> RequestCaptureAsync(
        OwnerScope owner,
        Guid milestoneId,
        ScreenshotCaptureRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one milestone's capture candidates, newest first.
    /// </summary>
    /// <remarks>
    /// Owner-private and read-only. This package creates candidates and never selects, reorders,
    /// promotes or deletes them; that is screenshot-curation's work, and a read route that could
    /// change a candidate would mean the boundary was crossed on the way in.
    /// </remarks>
    Task<IReadOnlyList<ScreenshotCandidate>> ListCandidatesAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    /// <summary>Performs the work of one capture job, and returns what it produced.</summary>
    Task<CaptureJobOutcome> RunAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Chronicleaf-owned side of the candidate table (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// Separate from the media store because these rows are Chronicleaf's own and never resolve a
/// storage key. Every method is scoped, and the scope is in the WHERE clause rather than in a filter
/// applied afterwards.
/// </remarks>
public interface IScreenshotCandidateStore
{
    /// <summary>Reads a milestone's candidates, newest first, bounded by the caller.</summary>
    Task<IReadOnlyList<ScreenshotCandidate>> ListAsync(
        Guid scopeId,
        Guid milestoneId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts one candidate, or reports that this job already captured this page at this viewport.
    /// </summary>
    /// <remarks>
    /// The decision is the database's, made atomically by the unique tuple
    /// <c>(operation_id, source_url, viewport)</c>, so a job that is retried or resumed after a
    /// partial failure cannot write the same picture twice - and a second writer is told so rather
    /// than being told it succeeded.
    /// </remarks>
    /// <returns>True when this call created the row; false when the tuple already existed.</returns>
    Task<bool> TryInsertAsync(ScreenshotCandidate candidate, CancellationToken cancellationToken = default);
}

/// <summary>
/// The request-row side of the capture storage (SCREENSHOT-CAPTURE-001).
/// </summary>
public interface ICaptureRequestStore
{
    /// <summary>Reads the request a job was created from, or <c>null</c> when there is none in this scope.</summary>
    Task<CaptureRequestRow?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a request row. Returns false when this job already has one.</summary>
    Task<bool> TryInsertAsync(CaptureRequestRow row, CancellationToken cancellationToken = default);
}
