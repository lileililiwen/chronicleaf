namespace Chronicleaf.Core;

/// <summary>
/// One captured page as curation sees it (SCREENSHOT-CURATION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This is a read of the same <c>screenshot_candidates</c> row the capture package inserts, not a
/// second candidate schema. The capture record's curation fields are constants - status
/// <c>candidate</c>, empty caption, not primary, revision one - because capture never selects; this
/// record carries the values those columns hold after an owner has acted, which is what makes
/// "somebody selected this" a state rather than an inference.
/// </para>
/// <para>
/// <see cref="DisplayOrder"/> is the owner's explicit sequence of the selected pictures, written
/// only by the order endpoint. A candidate that has never been ordered carries zero, and a reader
/// that needs a total order breaks ties by capture time. The order of unselected candidates is not
/// curated state: it is the capture order the candidate list already publishes.
/// </para>
/// </remarks>
public sealed class CuratedScreenshot
{
    public CuratedScreenshot(
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
        DateTimeOffset createdAt,
        string status,
        string caption,
        bool isPrimary,
        int displayOrder,
        long revision)
    {
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
        Status = status;
        Caption = caption;
        IsPrimary = isPrimary;
        DisplayOrder = displayOrder;
        Revision = revision;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ProjectId { get; }

    public Guid MilestoneId { get; }

    /// <summary>The stored image this candidate is.</summary>
    public Guid MediaId { get; }

    /// <summary>The capture job that produced it.</summary>
    public Guid OperationId { get; }

    /// <summary>The URL the owner asked to be captured.</summary>
    public string SourceUrl { get; }

    /// <summary>Where the page actually ended up.</summary>
    public string FinalUrl { get; }

    /// <summary>The viewport this picture was taken at.</summary>
    public CaptureViewport Viewport { get; }

    /// <summary>When the bytes were produced.</summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>When the row was inserted.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>One of the <see cref="ScreenshotCurationStatus"/> values.</summary>
    public string Status { get; }

    /// <summary>The owner's plain-text caption, or empty when none was written.</summary>
    public string Caption { get; }

    /// <summary>True for the milestone's single primary picture, if one is chosen.</summary>
    public bool IsPrimary { get; }

    /// <summary>The owner's explicit sequence among the selected pictures.</summary>
    public int DisplayOrder { get; }

    /// <summary>The optimistic revision every curation write is conditional on.</summary>
    public long Revision { get; }

    /// <summary>True when an owner has chosen this picture as selected evidence.</summary>
    public bool IsSelected => string.Equals(Status, ScreenshotCurationStatus.Selected, StringComparison.Ordinal);
}

/// <summary>
/// The states a captured page moves through once an owner acts (SCREENSHOT-CURATION-001).
/// </summary>
/// <remarks>
/// Capture inserts every row as <see cref="Candidate"/> and never moves it. Selection publishes
/// the row into the milestone's evidence; rejection marks a bad candidate without deleting its
/// row, so the bytes a rejected picture points at stay addressable until nothing references them.
/// A rejected row is a tombstone, not an erasure: it is never physically deleted by this package,
/// and media it no longer protects becomes reclaimable through the delivered retire path.
/// </remarks>
public static class ScreenshotCurationStatus
{
    /// <summary>Captured and offered. Not selected, not attached to evidence, not primary.</summary>
    public const string Candidate = ScreenshotCandidateStatus.Candidate;

    /// <summary>Chosen by the owner. A screenshot evidence item references its media and caption.</summary>
    public const string Selected = "selected";

    /// <summary>Refused by the owner. Outside selected evidence, kept as a row.</summary>
    public const string Rejected = "rejected";

    public static IReadOnlyList<string> All { get; } = [Candidate, Selected, Rejected];

    public static bool IsKnown(string status) =>
        All.Contains(status, StringComparer.Ordinal);
}

/// <summary>
/// The bounds curation imposes on what an owner may write (SCREENSHOT-CURATION-001/002).
/// </summary>
public static class ScreenshotCurationLimits
{
    /// <summary>
    /// At most 1000 characters for a caption. Stored and served as plain text: it is never
    /// interpreted as markup, so a caption cannot become a heading, a link or a script wherever it
    /// is rendered.
    /// </summary>
    public const int CaptionMaxLength = 1000;
}
