using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Curation;

/// <summary>
/// What an owner asks to change about one captured picture (SCREENSHOT-CURATION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Every field is optional except the two revisions: a PATCH names the change it wants - a new
/// status, a new caption, a new primary flag - and the two revisions it was read against. The
/// screenshot revision guards the picture's own row; the bundle revision guards the evidence the
/// selection is published into. Both are required because curation writes to both stores in one
/// transaction, and a write conditional on only one of them could publish a selection against
/// evidence its owner never saw.
/// </para>
/// <para>
/// Leaving <see cref="IsPrimary"/> true while moving the status away from selected does not fail:
/// the primary flag is cleared in the same statement, because a primary picture that is no longer
/// selected would violate the invariant that only a selected picture may be primary.
/// </para>
/// </remarks>
public sealed class ScreenshotCurateRequest
{
    public ScreenshotCurateRequest(
        string? status,
        string? caption,
        bool? isPrimary,
        long expectedRevision,
        long expectedBundleRevision)
    {
        Status = status;
        Caption = caption;
        IsPrimary = isPrimary;
        ExpectedRevision = expectedRevision;
        ExpectedBundleRevision = expectedBundleRevision;
    }

    /// <summary>The desired status, or null to keep the current one.</summary>
    public string? Status { get; }

    /// <summary>The desired plain-text caption, or null to keep the current one.</summary>
    public string? Caption { get; }

    /// <summary>The desired primary flag, or null to keep the current one.</summary>
    public bool? IsPrimary { get; }

    /// <summary>The screenshot revision the owner read, from If-Match.</summary>
    public long ExpectedRevision { get; }

    /// <summary>The evidence revision the owner read.</summary>
    public long ExpectedBundleRevision { get; }
}

/// <summary>
/// The new explicit sequence of a milestone's selected pictures (SCREENSHOT-CURATION-002).
/// </summary>
/// <remarks>
/// <see cref="Ids"/> must name exactly the milestone's currently selected pictures, once each:
/// no duplicates, no omissions, no unselected pictures. An order that drops a selected picture
/// would silently unselect it, and an order that adds an unselected one would silently select it -
/// so both are refused rather than resolved in favour of the stored set.
/// </remarks>
public sealed class ScreenshotOrderRequest
{
    public ScreenshotOrderRequest(IReadOnlyList<Guid> ids, long expectedBundleRevision)
    {
        Ids = ids;
        ExpectedBundleRevision = expectedBundleRevision;
    }

    /// <summary>The selected pictures in the owner's desired order, first to last.</summary>
    public IReadOnlyList<Guid> Ids { get; }

    /// <summary>The evidence revision the owner read.</summary>
    public long ExpectedBundleRevision { get; }
}

/// <summary>
/// One curated picture and the evidence revision it was curated against
/// (SCREENSHOT-CURATION-001/002).
/// </summary>
public sealed record CuratedScreenshotOutcome(CuratedScreenshot Screenshot, long BundleRevision);

/// <summary>
/// The milestone's selected pictures in their stored order, and the evidence revision they were
/// ordered against (SCREENSHOT-CURATION-002).
/// </summary>
public sealed record ScreenshotOrderOutcome(IReadOnlyList<CuratedScreenshot> Ordered, long BundleRevision);

/// <summary>
/// The human curation use cases over real PostgreSQL (SCREENSHOT-CURATION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Selection is publication: moving a candidate into selected appends a screenshot evidence item
/// that references its media and caption, and moving it back out appends a new bundle revision
/// that no longer carries that item. Earlier bundles keep their own rows - the database refuses
/// rewrites outright - so approved historical content still reads what it was approved against.
/// </para>
/// <para>
/// Capture re-runs never move these columns. The capture job inserts candidate rows and nothing
/// else; a new candidate arrives as a candidate however the owner curated the earlier ones.
/// </para>
/// </remarks>
public interface IScreenshotCurationService
{
    /// <summary>Reads one milestone's captured pictures with their curation state, newest first.</summary>
    /// <exception cref="ResourceNotFoundException">The milestone is absent or in another scope (404).</exception>
    Task<IReadOnlyList<CuratedScreenshot>> ListAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one owner's change to one captured picture, and publishes the selection into the
    /// milestone's evidence when the change moves it.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The picture is absent or in another scope (404).</exception>
    /// <exception cref="ProjectArchivedException">The picture's project is archived (409).</exception>
    /// <exception cref="ValidationFailedException">A field is not one this package accepts (422).</exception>
    /// <exception cref="RevisionConflictException">The screenshot or the bundle revision is stale (409).</exception>
    Task<CuratedScreenshotOutcome> CurateAsync(
        OwnerScope owner,
        Guid screenshotId,
        ScreenshotCurateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Stores the owner's explicit sequence of the selected pictures.</summary>
    /// <exception cref="ResourceNotFoundException">The milestone is absent or in another scope (404).</exception>
    /// <exception cref="ProjectArchivedException">The milestone's project is archived (409).</exception>
    /// <exception cref="ValidationFailedException">The ids are not exactly the selected set (422).</exception>
    /// <exception cref="RevisionConflictException">The bundle revision is stale (409).</exception>
    Task<ScreenshotOrderOutcome> OrderAsync(
        OwnerScope owner,
        Guid milestoneId,
        ScreenshotOrderRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Chronicleaf-owned side of the candidate curation columns (SCREENSHOT-CURATION-001/002).
/// </summary>
/// <remarks>
/// The table is capture's; these are the columns curation moves. Every method is scoped, and the
/// scope is in the WHERE clause rather than in a filter applied afterwards. The evidence bundle
/// writes that selection implies go through the delivered evidence store, never through here.
/// </remarks>
public interface IScreenshotCurationStore
{
    /// <summary>
    /// Locks the milestone's row for the transaction. Returns false when the milestone is absent
    /// or in another scope, so concurrent curations of one milestone serialize instead of racing
    /// on the one-primary index.
    /// </summary>
    Task<bool> LockMilestoneAsync(
        Guid scopeId,
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one picture with its curation state, or null when it is absent or foreign.</summary>
    Task<CuratedScreenshot?> FindAsync(
        Guid scopeId,
        Guid screenshotId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a milestone's pictures with their curation state, newest first.</summary>
    Task<IReadOnlyList<CuratedScreenshot>> ListAsync(
        Guid scopeId,
        Guid milestoneId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one picture's curation state conditional on its revision. Returns false when the row
    /// moved under the caller, which is the 409 rather than a lost update.
    /// </summary>
    Task<bool> TryUpdateAsync(
        Guid scopeId,
        Guid screenshotId,
        long expectedRevision,
        string status,
        string caption,
        bool isPrimary,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears every other primary of the milestone in the same transaction that sets the new one,
    /// so two primaries never coexist.
    /// </summary>
    Task ClearOtherPrimariesAsync(
        Guid scopeId,
        Guid milestoneId,
        Guid exceptId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores one picture's position conditional on its revision. Returns false when the row moved
    /// under the caller.
    /// </summary>
    Task<bool> TryOrderAsync(
        Guid scopeId,
        Guid screenshotId,
        long expectedRevision,
        int displayOrder,
        CancellationToken cancellationToken = default);
}
