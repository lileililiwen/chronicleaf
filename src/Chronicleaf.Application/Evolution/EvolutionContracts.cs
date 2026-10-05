using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Evolution;

/// <summary>
/// What an owner asks to compare (SCREENSHOT-EVOLUTION-IMAGES-001).
/// </summary>
/// <remarks>
/// Milestones are named, never inferred: the comparison covers exactly the confirmed milestones
/// listed here, in chronological order unless <see cref="ManualOrder"/> fixes the sequence.
/// <see cref="SelectedScreenshotIds"/> optionally pins which selected picture depicts a
/// milestone that holds several; every named id must already be selected, because candidates
/// are never compared and this request never promotes one.
/// <see cref="ExpectedSelectionHash"/> is the manifest hash the owner reviewed: rendering is
/// conditional on the selection still being what was reviewed, so a caption edited mid-flight
/// supersedes the request instead of silently entering the artifact.
/// </remarks>
public sealed class RenderEvolutionRequest
{
    public RenderEvolutionRequest(
        IReadOnlyList<Guid> milestoneIds,
        string layout,
        IReadOnlyList<Guid>? selectedScreenshotIds,
        IReadOnlyList<Guid>? manualOrder,
        string? expectedSelectionHash,
        string? idempotencyKey)
    {
        MilestoneIds = milestoneIds;
        Layout = layout;
        SelectedScreenshotIds = selectedScreenshotIds;
        ManualOrder = manualOrder;
        ExpectedSelectionHash = expectedSelectionHash;
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>The confirmed milestones to compare, 2 to 12.</summary>
    public IReadOnlyList<Guid> MilestoneIds { get; }

    /// <summary>One of <see cref="EvolutionLayouts"/>.</summary>
    public string Layout { get; }

    /// <summary>
    /// The selected pictures to depict, or null to depict each milestone's primary (else its
    /// first selected picture). Every id must be selected; a candidate is never depicted.
    /// </summary>
    public IReadOnlyList<Guid>? SelectedScreenshotIds { get; }

    /// <summary>
    /// The owner's explicit milestone sequence, or null for chronological order. Must name
    /// exactly the requested milestones once each when present.
    /// </summary>
    public IReadOnlyList<Guid>? ManualOrder { get; }

    /// <summary>The manifest selection hash the owner reviewed, or null for a first render.</summary>
    public string? ExpectedSelectionHash { get; }

    /// <summary>The caller's idempotency key, or null.</summary>
    public string? IdempotencyKey { get; }
}

/// <summary>One rendered artifact and whether the call created it.</summary>
public sealed record RenderEvolutionOutcome(EvolutionImage Artifact, bool Created);

/// <summary>One approval of an evolution artifact and the hashes it authorized.</summary>
public sealed record ApproveEvolutionOutcome(EvolutionImage Artifact, bool AlreadyApproved);

/// <summary>The approved bytes and the hash they were approved under.</summary>
public sealed record EvolutionExport(byte[] Png, string Sha256, bool ApprovalLive);

/// <summary>
/// The evolution use cases over the delivered curation, evidence and media contracts
/// (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Selected only, always.</b> Every depicted picture is read through the curation store with
/// its selection state, and anything that is not selected is never decoded, never placed and
/// never named as a source: a candidate that was never chosen stays outside the comparison the
/// same way it stays outside evidence, and naming one is a 422 rather than a promotion.
/// </para>
/// <para>
/// <b>Derived, then bound.</b> Rendering stores the PNG through the delivered media store and
/// the manifest beside it, citing the media and caption revisions every cell was built from.
/// Approval binds the selection hash and the artifact hash on the same row; an export serves
/// the exact approved bytes only while both still describe the stored state.
/// </para>
/// </remarks>
public interface IEvolutionImageService
{
    /// <summary>Renders the comparison, or answers the idempotent replay.</summary>
    /// <exception cref="ResourceNotFoundException">The project or a milestone is absent or foreign (404).</exception>
    /// <exception cref="ProjectArchivedException">The project is archived (409).</exception>
    /// <exception cref="ValidationFailedException">The shape is not one this package renders (422).</exception>
    /// <exception cref="ComparisonViewportMismatchException">Mixed viewports outside the gallery (422).</exception>
    /// <exception cref="InsufficientSelectedException">Fewer than two milestones depict a picture (422).</exception>
    /// <exception cref="EvolutionMediaUnavailableException">A selected picture's bytes are gone (409).</exception>
    /// <exception cref="EvolutionTooLargeException">The canvas exceeds the ceiling (413).</exception>
    /// <exception cref="SelectionSupersededException">The selection moved since the review (409).</exception>
    Task<RenderEvolutionOutcome> RenderAsync(
        OwnerScope owner,
        Guid projectId,
        RenderEvolutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one artifact's manifest with its live staleness.</summary>
    /// <exception cref="ResourceNotFoundException">The artifact is absent or foreign (404).</exception>
    Task<(EvolutionImage Artifact, bool SelectionCurrent)> ReadAsync(
        OwnerScope owner,
        Guid evolutionId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists one project's artifacts, newest first.</summary>
    Task<IReadOnlyList<EvolutionImage>> ListAsync(
        OwnerScope owner,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Approves the exact selection and artifact hashes on the row.</summary>
    /// <exception cref="ResourceNotFoundException">The artifact is absent or foreign (404).</exception>
    /// <exception cref="SelectionSupersededException">The selection moved since the review (409).</exception>
    Task<ApproveEvolutionOutcome> ApproveAsync(
        OwnerScope owner,
        Guid evolutionId,
        string expectedSelectionHash,
        CancellationToken cancellationToken = default);

    /// <summary>Serves the draft bytes for review. A draft is viewable; only an export is gated.</summary>
    /// <exception cref="ResourceNotFoundException">The artifact is absent or foreign (404).</exception>
    /// <exception cref="EvolutionMediaUnavailableException">The artifact's bytes are gone (409).</exception>
    Task<EvolutionExport> PreviewAsync(
        OwnerScope owner,
        Guid evolutionId,
        CancellationToken cancellationToken = default);

    /// <summary>Serves the exact approved bytes, while the approval still describes the state.</summary>
    /// <exception cref="ResourceNotFoundException">The artifact is absent or foreign (404).</exception>
    /// <exception cref="Content.ApprovalRequiredException">Nothing approved authorizes this export (409).</exception>
    /// <exception cref="Content.ApprovalStaleException">The approval no longer describes the state (409).</exception>
    /// <exception cref="EvolutionMediaUnavailableException">The artifact's bytes are gone (409).</exception>
    Task<EvolutionExport> ExportAsync(
        OwnerScope owner,
        Guid evolutionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Chronicleaf-owned side of the evolution table (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
/// <remarks>
/// Every method is scoped, and the scope is in the WHERE clause rather than in a filter applied
/// afterwards, so a lookup by id can only ever return a row inside the caller's own installation.
/// </remarks>
public interface IEvolutionImageStore
{
    /// <summary>Inserts the rendered row. Returns false when the id is already taken.</summary>
    Task<bool> InsertAsync(EvolutionImage artifact, CancellationToken cancellationToken = default);

    /// <summary>Reads one artifact in a scope, or null when it is absent or foreign.</summary>
    Task<EvolutionImage?> FindAsync(
        Guid scopeId,
        Guid evolutionId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one project's artifacts, newest first, bounded.</summary>
    Task<IReadOnlyList<EvolutionImage>> ListAsync(
        Guid scopeId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the row an idempotency key already bound, or null. A key binds one selection: the
    /// same key with other inputs is a conflict the service reports, not a second row.
    /// </summary>
    Task<EvolutionImage?> FindByIdempotencyKeyAsync(
        Guid scopeId,
        Guid projectId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a draft to approved with its actor and instant, conditional on it still being a
    /// draft. Returns false when the row moved under the caller.
    /// </summary>
    Task<bool> TryApproveAsync(
        Guid scopeId,
        Guid evolutionId,
        Guid actorId,
        DateTimeOffset approvedAt,
        CancellationToken cancellationToken = default);
}
