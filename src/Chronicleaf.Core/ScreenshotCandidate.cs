namespace Chronicleaf.Core;

/// <summary>
/// One captured page, offered to an owner and nothing more (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// <para>
/// A candidate is a picture and the facts needed to be honest about where it came from: the URL that
/// was asked for, the URL the page actually ended at after its redirects, the viewport it was taken at
/// and the instant the bytes were produced. Nothing here says the picture is good.
/// </para>
/// <para>
/// <b>Every curation field is a constant, and that is the boundary.</b> <see cref="Status"/> is
/// <c>candidate</c>, <see cref="IsPrimary"/> is false and <see cref="Caption"/> is empty for every row
/// this package will ever insert. SCREENSHOT-CAPTURE-001 explicitly does not select, reorder, promote
/// or attach a candidate to approved evidence, and screenshot-curation is the package that adds those
/// behaviours and the one-primary constraint. Storing them here as columns with constant initial
/// values is what lets that package own them without redefining capture storage, and is what makes
/// "somebody selected this" a state an owner can see rather than an inference from a flag that was
/// never meant to move.
/// </para>
/// <para>
/// <b><see cref="SourceUrl"/> is a URL an owner chose, which is why it is stored and not
/// published.</b> It is provenance - the page these bytes depict - and a candidate row is
/// owner-private, so it is never handed to a browser to fetch, never resolved by a read route and
/// never rendered as a link. The only component that acts on it is the capture job, and the only
/// component that acts on it is subject to the same destination rules as everything else the page
/// asks for.
/// </para>
/// </remarks>
/// <param name="id">This candidate's identifier.</param>
/// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
/// <param name="projectId">The project the capture was made for.</param>
/// <param name="milestoneId">The milestone that was being evidenced.</param>
/// <param name="mediaId">
/// The stored image. A candidate without media is not a candidate: the row is only inserted once the
/// bytes are verified and registered, so this is never a dangling reference.
/// </param>
/// <param name="sourceUrl">The URL that was asked for, after the policy accepted it.</param>
/// <param name="finalUrl">The URL the page actually ended at, which may differ after redirects.</param>
/// <param name="viewport">The viewport this picture was taken at, as the two stored dimensions.</param>
/// <param name="capturedAt">When the bytes were produced, from the injected clock, always UTC.</param>
/// <param name="createdAt">When the row was inserted.</param>
public sealed class ScreenshotCandidate
{
    /// <param name="id">This candidate's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="projectId">The project the capture was made for.</param>
    /// <param name="milestoneId">The milestone that was being evidenced.</param>
    /// <param name="mediaId">The stored image this candidate is.</param>
    /// <param name="operationId">
    /// The capture job that produced it. Together with the URL and the viewport this is the tuple the
    /// database makes unique, which is what stops a retried or resumed job writing the same picture
    /// twice.
    /// </param>
    /// <param name="sourceUrl">The URL that was asked for.</param>
    /// <param name="finalUrl">The URL the page actually ended at.</param>
    /// <param name="viewport">The viewport this picture was taken at.</param>
    /// <param name="capturedAt">When the bytes were produced.</param>
    /// <param name="createdAt">When the row was inserted.</param>
    public ScreenshotCandidate(
        Guid id,
        Guid scopeId,
        Guid projectId,
        Guid milestoneId,
        Guid mediaId,
        Guid operationId,
        string sourceUrl,
        string finalUrl,
        CaptureViewport viewport,
        DateTimeOffset capturedAt,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalUrl);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A candidate needs an id.", nameof(id));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A candidate needs a project.", nameof(projectId));
        }

        if (milestoneId == Guid.Empty)
        {
            throw new ArgumentException("A candidate needs a milestone.", nameof(milestoneId));
        }

        if (mediaId == Guid.Empty)
        {
            // The whole meaning of this row is that there are bytes behind it. A record that could be
            // constructed without a media id is a record that could be inserted as a promise.
            throw new ArgumentException("A candidate needs the media it captured.", nameof(mediaId));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A candidate needs the job that captured it.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(viewport);

        if (sourceUrl.Length > ScreenshotCandidateLimits.MaximumUrlLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceUrl),
                sourceUrl.Length,
                $"A capture URL is at most {ScreenshotCandidateLimits.MaximumUrlLength} characters.");
        }

        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        MilestoneId = milestoneId;
        MediaId = mediaId;
        OperationId = operationId;
        SourceUrl = sourceUrl;
        FinalUrl = finalUrl;
        Viewport = viewport;
        CapturedAt = capturedAt;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ProjectId { get; }

    public Guid MilestoneId { get; }

    /// <summary>The stored image. Always present: the row is only written after the bytes are ready.</summary>
    public Guid MediaId { get; }

    /// <summary>The capture job that produced this row, and half of the uniqueness tuple.</summary>
    public Guid OperationId { get; }

    /// <summary>The URL the owner asked to be captured, and the other half of the tuple.</summary>
    public string SourceUrl { get; }

    /// <summary>
    /// Where the page actually ended up.
    ///
    /// Stored separately from <see cref="SourceUrl"/> because a redirect is the single most useful
    /// thing a reader can learn from a capture: two requests for different URLs that produced the same
    /// picture are the same page, and a picture whose final URL is somewhere unexpected is the
    /// clearest possible report that the page redirected somewhere the owner did not name.
    /// </summary>
    public string FinalUrl { get; }

    /// <summary>The viewport this picture was taken at, and the last third of the tuple.</summary>
    public CaptureViewport Viewport { get; }

    /// <summary>When the bytes were produced.</summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>When the row was inserted.</summary>
    public DateTimeOffset CreatedAt { get; }

    // The four curation fields, below. Each is a readonly field the mapper can bind and no caller can
    // write, so "this package never selects, promotes or captions a candidate" is a property of the
    // type rather than a rule a future edit could forget to follow.

    /// <summary>Always <c>candidate</c>. See the type remarks: selection belongs to another package.</summary>
    public string Status => _status;

    /// <summary>Always empty. This package does not write captions.</summary>
    public string Caption => _caption;

    /// <summary>Always false. Promotion belongs to screenshot-curation.</summary>
    public bool IsPrimary => _isPrimary;

    /// <summary>Always one. A candidate is immutable, so there is no second version of it to hold.</summary>
    public long Revision => _revision;

    private readonly string _status = ScreenshotCandidateStatus.Candidate;
    private readonly string _caption = string.Empty;
    private readonly bool _isPrimary = false;
    private readonly long _revision = 1;
}

/// <summary>
/// The one status a capture candidate is ever inserted with (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// A single value, deliberately. Screenshot-curation adds the selected states and the one-primary
/// constraint; this package has no opinion about a row's fate after it has been offered, which is
/// exactly what "does not select" means as a storage decision rather than only as a service one.
/// </remarks>
public static class ScreenshotCandidateStatus
{
    /// <summary>Captured and offered. Not selected, not attached to evidence, not primary.</summary>
    public const string Candidate = "candidate";
}

/// <summary>
/// The bounds this package's own storage imposes, kept beside the record that has to satisfy them.
/// </summary>
/// <remarks>
/// The capture bounds the design states - viewport sizes, page count, wait and job limits, PNG byte
/// and pixel ceilings - belong to the service and the job. What is left here is the width of a
/// column, and a column that is too narrow truncates a URL in the database, which would leave a row
/// describing a page with a URL that was never visited. A refused write is better than a plausible
/// lie, so the limit is enforced by the record's own constructor rather than only by the database.
/// </remarks>
public static class ScreenshotCandidateLimits
{
    /// <summary>
    /// At most 2048 characters for a stored URL. Comfortably above any real page, and short enough
    /// that the column and the log lines that quote it both stay bounded.
    /// </summary>
    public const int MaximumUrlLength = 2048;
}
