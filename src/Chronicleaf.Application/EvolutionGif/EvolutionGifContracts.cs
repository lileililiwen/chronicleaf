using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.EvolutionGifs;

/// <summary>
/// What an owner asks to animate (EVOLUTION-GIF-EXPORT-001).
/// </summary>
/// <remarks>
/// Milestones are named, never inferred: the GIF covers exactly the confirmed milestones listed
/// here, in chronological order unless <see cref="ManualOrder"/> fixes the sequence.
/// <see cref="SelectedScreenshotIds"/> optionally pins which selected picture depicts a
/// milestone that holds several; every named id must already be selected, because candidates
/// are never depicted and this request never promotes one.
/// <see cref="DurationsMs"/> fixes how long each canvas-order frame shows: null for the
/// one-second default, one value for every frame, or one value per frame. Durations and loop
/// are part of the review hash, so editing them after approval requires a new approval.
/// <see cref="ExpectedSelectionHash"/> is the manifest hash the owner reviewed: rendering is
/// conditional on the selection still being what was reviewed, so a caption edited mid-flight
/// supersedes the request instead of silently entering the artifact.
/// </remarks>
public sealed class RenderEvolutionGifRequest
{
    public RenderEvolutionGifRequest(
        IReadOnlyList<Guid> milestoneIds,
        IReadOnlyList<Guid>? selectedScreenshotIds,
        IReadOnlyList<Guid>? manualOrder,
        IReadOnlyList<int>? durationsMs,
        bool loop,
        string? expectedSelectionHash,
        string? idempotencyKey)
    {
        MilestoneIds = milestoneIds;
        SelectedScreenshotIds = selectedScreenshotIds;
        ManualOrder = manualOrder;
        DurationsMs = durationsMs;
        Loop = loop;
        ExpectedSelectionHash = expectedSelectionHash;
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>The confirmed milestones to animate, 2 to 12.</summary>
    public IReadOnlyList<Guid> MilestoneIds { get; }

    /// <summary>
    /// The selected pictures to depict, or null to depict each milestone's primary (else its
    /// first selected picture). Every id must be selected; a candidate is never depicted.
    /// </summary>
    public IReadOnlyList<Guid>? SelectedScreenshotIds { get; }

    /// <summary>
    /// The owner's explicit frame sequence, or null for chronological order. Must name exactly
    /// the requested milestones once each when present.
    /// </summary>
    public IReadOnlyList<Guid>? ManualOrder { get; }

    /// <summary>
    /// Per-frame durations in milliseconds: null, one value for all frames, or one per frame.
    /// </summary>
    public IReadOnlyList<int>? DurationsMs { get; }

    /// <summary>Whether the GIF loops indefinitely. Default false: it plays once.</summary>
    public bool Loop { get; }

    /// <summary>The manifest selection hash the owner reviewed, or null for a first render.</summary>
    public string? ExpectedSelectionHash { get; }

    /// <summary>The caller's idempotency key, or null.</summary>
    public string? IdempotencyKey { get; }
}

/// <summary>One rendered artifact and whether the call created it.</summary>
public sealed record RenderEvolutionGifOutcome(EvolutionGif Artifact, bool Created);

/// <summary>One approval of a GIF artifact and the hashes it authorized.</summary>
public sealed record ApproveEvolutionGifOutcome(EvolutionGif Artifact, bool AlreadyApproved);

/// <summary>The approved bytes and whether a live approval still describes them.</summary>
public sealed record EvolutionGifExport(byte[] Gif, string Sha256, bool ApprovalLive);

/// <summary>
/// The evolution GIF use cases over the delivered curation, evidence and media contracts
/// (EVOLUTION-GIF-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Selected only, always.</b> Every depicted picture is read through the curation store with
/// its selection state, and anything that is not selected is never decoded, never placed and
/// never named as a frame: a candidate that was never chosen stays outside the animation the
/// same way it stays outside evidence, and naming one is a 422 rather than a promotion.
/// </para>
/// <para>
/// <b>Derived, then bound.</b> Rendering encodes the deterministic GIF before this package's
/// row exists, and the row - bytes, manifest, selection hash and artifact hash together - is
/// written in one INSERT, so a failed render stores nothing and a partial GIF is never
/// completable. Approval binds the selection hash and the artifact hash on the same row; an
/// export serves the exact approved bytes only while both still describe the stored state.
/// </para>
/// </remarks>
public interface IEvolutionGifService
{
    /// <summary>Renders the animation, or answers the idempotent replay.</summary>
    /// <exception cref="ResourceNotFoundException">The project or a milestone is absent or foreign (404).</exception>
    /// <exception cref="ProjectArchivedException">The project is archived (409).</exception>
    /// <exception cref="ValidationFailedException">The shape is not one this package animates (422).</exception>
    /// <exception cref="Evolution.InsufficientSelectedException">A milestone depicts no selected screenshot (422).</exception>
    /// <exception cref="EvolutionGifMediaUnavailableException">A selected picture's bytes are gone (409).</exception>
    /// <exception cref="EvolutionGifTooLargeException">The canvas exceeds the ceiling (413).</exception>
    /// <exception cref="EvolutionGifSelectionSupersededException">The selection moved since the review (409).</exception>
    Task<RenderEvolutionGifOutcome> RenderAsync(
        OwnerScope owner,
        Guid projectId,
        RenderEvolutionGifRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one artifact's manifest with its live staleness.</summary>
    /// <exception cref="ResourceNotFoundException">The artifact is absent or foreign (404).</exception>
    Task<(EvolutionGif Artifact, bool SelectionCurrent)> ReadAsync(
        OwnerScope owner,
        Guid gifId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists one project's artifacts, newest first.</summary>
    Task<IReadOnlyList<EvolutionGif>> ListAsync(
        OwnerScope owner,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Approves the exact selection and artifact hashes on the row.</summary>
    /// <exception cref="ResourceNotFoundException">The artifact is absent or foreign (404).</exception>
    /// <exception cref="EvolutionGifSelectionSupersededException">The selection moved since the review (409).</exception>
    Task<ApproveEvolutionGifOutcome> ApproveAsync(
        OwnerScope owner,
        Guid gifId,
        string expectedSelectionHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serves the draft bytes for review, or the static first frame when asked. A draft is
    /// viewable; only an export is gated. The static frame is derived deterministically from
    /// the stored GIF's first frame, so it shows exactly what playback starts with.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The artifact is absent or foreign (404).</exception>
    Task<(byte[] Png, int Width, int Height)> PreviewStaticAsync(
        OwnerScope owner,
        Guid gifId,
        CancellationToken cancellationToken = default);

    /// <summary>Serves the draft bytes for review. A draft is viewable; only an export is gated.</summary>
    /// <exception cref="ResourceNotFoundException">The artifact is absent or foreign (404).</exception>
    Task<EvolutionGifExport> PreviewAsync(
        OwnerScope owner,
        Guid gifId,
        CancellationToken cancellationToken = default);

    /// <summary>Serves the exact approved bytes, while the approval still describes the state.</summary>
    /// <exception cref="ResourceNotFoundException">The artifact is absent or foreign (404).</exception>
    /// <exception cref="Content.ApprovalRequiredException">Nothing approved authorizes this export (409).</exception>
    /// <exception cref="Content.ApprovalStaleException">The approval no longer describes the state (409).</exception>
    Task<EvolutionGifExport> ExportAsync(
        OwnerScope owner,
        Guid gifId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Chronicleaf-owned side of the evolution GIF table (EVOLUTION-GIF-EXPORT-001/002).
/// </summary>
/// <remarks>
/// Every method is scoped, and the scope is in the WHERE clause rather than in a filter applied
/// afterwards, so a lookup by id can only ever return a row inside the caller's own installation.
/// </remarks>
public interface IEvolutionGifStore
{
    /// <summary>Inserts the rendered row with its bytes. Returns false when the id is taken.</summary>
    Task<bool> InsertAsync(EvolutionGif artifact, CancellationToken cancellationToken = default);

    /// <summary>Reads one artifact in a scope, or null when it is absent or foreign.</summary>
    Task<EvolutionGif?> FindAsync(
        Guid scopeId,
        Guid gifId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one project's artifacts, newest first, bounded.</summary>
    Task<IReadOnlyList<EvolutionGif>> ListAsync(
        Guid scopeId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the row an idempotency key already bound, or null. A key binds one selection: the
    /// same key with other inputs is a conflict the service reports, not a second row.
    /// </summary>
    Task<EvolutionGif?> FindByIdempotencyKeyAsync(
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
        Guid gifId,
        Guid actorId,
        DateTimeOffset approvedAt,
        CancellationToken cancellationToken = default);
}
