namespace Chronicleaf.Core;

/// <summary>
/// The request a capture job reads its own work back from (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// <para>
/// A job is handed an operation id and nothing else, so the words an owner typed cannot live on the
/// operation row - that is a fingerprint and a result reference, not a payload, and putting a list of
/// URLs in a column the delivered queue owns would be using that table as a message bus. This row is
/// where the request lives, exactly as the delivered github-evidence package keeps its own refs, and a
/// job whose row is missing reports that rather than guessing at a target.
/// </para>
/// <para>
/// <b>One row per operation, enforced by the database.</b> A job that is retried, resumed or delivered
/// twice must find one request, not several, or a re-run would be able to photograph something the
/// owner did not ask for.
/// </para>
/// <para>
/// <b>Nothing here is ever read back to a client.</b> These URLs exist to be navigated once, by a job,
/// under a policy. The read route publishes candidates and their provenance; this table is not a
/// history of what an owner once asked for, and a reader of it learns only that a capture exists.
/// </para>
/// </remarks>
/// <param name="id">This request's identifier.</param>
/// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
/// <param name="operationId">The capture job, one to one.</param>
/// <param name="projectId">The project the candidates belong to.</param>
/// <param name="milestoneId">The milestone the candidates belong to.</param>
/// <param name="urls">The absolute URLs to capture, as the strings the candidates' tuple compares.</param>
/// <param name="viewports">The viewport names to capture.</param>
/// <param name="waitSelector">The optional selector the owner asked to wait for, or <c>null</c>.</param>
/// <param name="waitMilliseconds">How long to settle after each page loads.</param>
/// <param name="fullPage">Whether the whole scrollable page is captured.</param>
/// <param name="expectedBundleRevision">The evidence revision this capture was asked against.</param>
/// <param name="createdAt">When the request was made.</param>
public sealed class CaptureRequestRow
{
    /// <param name="id">This request's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="operationId">The capture job, one to one.</param>
    /// <param name="projectId">The project the candidates belong to.</param>
    /// <param name="milestoneId">The milestone the candidates belong to.</param>
    /// <param name="urls">The absolute URLs to capture.</param>
    /// <param name="viewports">The viewport names to capture.</param>
    /// <param name="waitSelector">The optional selector, or <c>null</c>.</param>
    /// <param name="waitMilliseconds">How long to settle after each page loads.</param>
    /// <param name="fullPage">Whether the whole scrollable page is captured.</param>
    /// <param name="expectedBundleRevision">The evidence revision this capture was asked against.</param>
    /// <param name="createdAt">When the request was made.</param>
    public CaptureRequestRow(
        Guid id,
        Guid scopeId,
        Guid operationId,
        Guid projectId,
        Guid milestoneId,
        IReadOnlyList<string> urls,
        IReadOnlyList<string> viewports,
        string? waitSelector,
        int waitMilliseconds,
        bool fullPage,
        long expectedBundleRevision,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(viewports);

        if (urls.Count == 0)
        {
            throw new ArgumentException("A capture request needs at least one URL.", nameof(urls));
        }

        if (viewports.Count == 0)
        {
            throw new ArgumentException("A capture request needs at least one viewport.", nameof(viewports));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A capture request needs an id.", nameof(id));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A capture request needs the job that runs it.", nameof(operationId));
        }

        if (milestoneId == Guid.Empty)
        {
            throw new ArgumentException("A capture request needs a milestone.", nameof(milestoneId));
        }

        if (expectedBundleRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedBundleRevision),
                expectedBundleRevision,
                "A bundle revision is zero or greater; zero is a milestone with no evidence yet.");
        }

        if (waitMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(waitMilliseconds), waitMilliseconds, "Wait is not negative.");
        }

        Id = id;
        ScopeId = scopeId;
        OperationId = operationId;
        ProjectId = projectId;
        MilestoneId = milestoneId;
        Urls = [.. urls];
        Viewports = [.. viewports];
        WaitSelector = waitSelector;
        WaitMilliseconds = waitMilliseconds;
        FullPage = fullPage;
        ExpectedBundleRevision = expectedBundleRevision;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid OperationId { get; }

    public Guid ProjectId { get; }

    public Guid MilestoneId { get; }

    /// <summary>The absolute URLs to capture, in the order the owner listed them.</summary>
    public IReadOnlyList<string> Urls { get; }

    /// <summary>The viewport names to capture, in the order the owner listed them.</summary>
    public IReadOnlyList<string> Viewports { get; }

    public string? WaitSelector { get; }

    public int WaitMilliseconds { get; }

    public bool FullPage { get; }

    /// <summary>The evidence revision this capture was asked against, and re-checked before a commit.</summary>
    public long ExpectedBundleRevision { get; }

    public DateTimeOffset CreatedAt { get; }
}
