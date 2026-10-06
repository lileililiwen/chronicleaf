using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Application.Journey;

/// <summary>
/// The codes this package adds to the delivered error envelope (PUBLIC-PROJECT-JOURNEY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The envelope is the delivered <c>{code,message,traceId,fieldErrors?}</c> and its status mapping
/// is unchanged; this file is vocabulary, not a second envelope. Every message is authored here in
/// fixed words, because the middleware writes <c>Exception.Message</c> verbatim into the envelope.
/// </para>
/// <para>
/// The approval refusals deliberately reuse the review package's own vocabulary -
/// <c>approval_required</c> and <c>approval_stale</c> - because the condition and the remedy are
/// the same and a second spelling would pretend otherwise. Only the slug conflict and the stale
/// preview hash have their own codes: both are this projection's own decisions, not an approval's.
/// </para>
/// </remarks>
public static class JourneyError
{
    /// <summary>
    /// The slug is already taken by another project's journey (409). The remedy is another slug.
    /// </summary>
    public const string SlugConflict = "journey_slug_conflict";

    /// <summary>
    /// The preview hash no longer describes the stored selection (409). The remedy is a fresh
    /// preview, not a retry: something was approved, edited or selected after the hash was read.
    /// </summary>
    public const string SelectionStale = "journey_selection_stale";
}

/// <summary>
/// The slug is already taken by another project's journey (409).
/// </summary>
public sealed class JourneySlugConflictException()
    : ApiException(
        JourneyError.SlugConflict,
        "That journey address is already taken by another project. Choose another slug.");

/// <summary>
/// The preview hash no longer describes the stored selection (409).
/// </summary>
public sealed class JourneySelectionStaleException()
    : ApiException(
        JourneyError.SelectionStale,
        "The selection changed since it was previewed. Preview the selection again and publish with the fresh hash.");

// ==============================================================================================
// Requests and views.
// ==============================================================================================

/// <summary>
/// What an owner sends when they ask what a selection would publish (PUBLIC-PROJECT-JOURNEY-001).
/// </summary>
/// <param name="ApprovedAssetIds">The approved assets to depict, each named once.</param>
public sealed record JourneyPreviewRequest(IReadOnlyList<Guid> ApprovedAssetIds);

/// <summary>
/// One previewed entry: exactly what publication would serve for it, before it is published.
/// </summary>
/// <param name="AssetId">The approved asset.</param>
/// <param name="MilestoneId">The confirmed milestone the asset belongs to.</param>
/// <param name="MilestoneTitle">The confirmed milestone's title.</param>
/// <param name="OccurredAt">When the milestone occurred; UTC ISO-8601.</param>
/// <param name="ContentRevision">The approved head revision.</param>
/// <param name="BodyHash">The digest of exactly those approved bytes.</param>
/// <param name="EvidenceRevision">The evidence head revision the approval is bound to.</param>
/// <param name="Pictures">The approval-covered pictures with their captions.</param>
public sealed record JourneyPreviewEntry(
    Guid AssetId,
    Guid MilestoneId,
    string MilestoneTitle,
    DateTimeOffset OccurredAt,
    long ContentRevision,
    string BodyHash,
    long EvidenceRevision,
    IReadOnlyList<JourneyPicture> Pictures);

/// <summary>One approval-covered picture with the owner's caption.</summary>
/// <param name="MediaId">The stored picture.</param>
/// <param name="Caption">The owner's caption.</param>
public sealed record JourneyPicture(Guid MediaId, string Caption);

/// <summary>
/// What a preview answers: the hash publication must carry, and the entries it would serve.
/// </summary>
/// <param name="SelectionHash">Lowercase SHA-256 hex over the canonical selection.</param>
/// <param name="Entries">The entries in oldest-first milestone order.</param>
public sealed record JourneyPreview(string SelectionHash, IReadOnlyList<JourneyPreviewEntry> Entries);

/// <summary>
/// What an owner sends when they publish, republish or revoke a journey
/// (PUBLIC-PROJECT-JOURNEY-001/002).
/// </summary>
/// <param name="Enabled">True to publish, false to revoke.</param>
/// <param name="Slug">The public address, required when enabling without a stored one.</param>
/// <param name="ExpectedRevision">The revision the owner read, required when a journey exists.</param>
/// <param name="SelectionHash">The preview hash, required when enabling.</param>
/// <param name="ApprovedAssetIds">The selection the hash pins, required when enabling.</param>
public sealed record JourneyPublishRequest(
    bool Enabled,
    string? Slug,
    long? ExpectedRevision,
    string? SelectionHash,
    IReadOnlyList<Guid>? ApprovedAssetIds);

/// <summary>
/// One journey as the private routes answer it (PUBLIC-PROJECT-JOURNEY-001/002).
/// </summary>
/// <param name="ProjectId">The projected project.</param>
/// <param name="Slug">The global public slug.</param>
/// <param name="Enabled">Whether the public routes serve the snapshot right now.</param>
/// <param name="Revision">The optimistic revision the next mutation must carry.</param>
/// <param name="SelectionHash">Lowercase SHA-256 hex over the canonical published selection.</param>
/// <param name="SnapshotRef">Lowercase SHA-256 hex over the canonical published snapshot.</param>
/// <param name="RevokedAt">When the owner last revoked this journey, if ever; UTC ISO-8601.</param>
/// <param name="EntryCount">How many entries the published snapshot depicts.</param>
public sealed record JourneyView(
    Guid ProjectId,
    string Slug,
    bool Enabled,
    long Revision,
    string SelectionHash,
    string SnapshotRef,
    DateTimeOffset? RevokedAt,
    int EntryCount);

/// <summary>
/// One journey as the anonymous public route serves it (PUBLIC-PROJECT-JOURNEY-001/002).
/// </summary>
/// <param name="Slug">The global public slug.</param>
/// <param name="ProjectName">The project's name.</param>
/// <param name="Revision">The published revision.</param>
/// <param name="Entries">The published entries in oldest-first milestone order.</param>
public sealed record JourneyPublicView(
    string Slug,
    string ProjectName,
    long Revision,
    IReadOnlyList<JourneyPublicEntry> Entries);

/// <summary>
/// One published entry: a confirmed milestone's title and date, its approved article text and its
/// approval-covered pictures. No private note, no candidate and no unselected picture appears here.
/// </summary>
/// <param name="MilestoneTitle">The confirmed milestone's title.</param>
/// <param name="OccurredAt">When the milestone occurred; UTC ISO-8601.</param>
/// <param name="BodyMarkdown">The approved article text, exactly as approved.</param>
/// <param name="Pictures">The approval-covered pictures with their captions and public addresses.</param>
public sealed record JourneyPublicEntry(
    string MilestoneTitle,
    DateTimeOffset OccurredAt,
    string BodyMarkdown,
    IReadOnlyList<JourneyPublicPicture> Pictures);

/// <summary>One published picture: its caption and the anonymous address that serves its bytes.</summary>
/// <param name="Caption">The owner's caption.</param>
/// <param name="MediaUrl">The anonymous media address, relative to the application root.</param>
public sealed record JourneyPublicPicture(string Caption, string MediaUrl);

// ==============================================================================================
// Storage and service contracts.
// ==============================================================================================

/// <summary>
/// The journey-row side of the public project journey storage (PUBLIC-PROJECT-JOURNEY-001/002).
/// </summary>
public interface IProjectJourneyStore
{
    /// <summary>Reads one project's journey inside a scope, or <c>null</c>.</summary>
    Task<Core.ProjectJourneyRow?> FindByProjectAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the journey behind a public slug across scopes, or <c>null</c>. The slug is global,
    /// so this is the one lookup in this package that is not scoped - and the reason the public
    /// routes serve a snapshot that was explicitly published rather than a live database read.
    /// </summary>
    Task<Core.ProjectJourneyRow?> FindBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a journey row. Returns false when the project or the slug is already bound.</summary>
    Task<bool> TryInsertAsync(Core.ProjectJourneyRow row, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a journey row when its stored revision still matches. Returns false when another
    /// writer moved it first, or when the slug it now carries is bound elsewhere.
    /// </summary>
    Task<bool> TryUpdateAsync(
        Core.ProjectJourneyRow row,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The public project journey use cases the API layer calls (PUBLIC-PROJECT-JOURNEY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A preview computes; a publication freezes; a public read re-checks.</b> The preview
/// resolves a selection against the live approvals and reports the hash publication must carry.
/// Publication re-resolves the same selection and refuses anything that moved - never approved
/// is <c>approval_required</c>, once approved but moved is <c>approval_stale</c>, a preview hash
/// that no longer matches is <c>journey_selection_stale</c> - so only explicitly published
/// milestones and entries ever appear. The anonymous read serves the frozen snapshot only while
/// every binding still describes the stored state; a revocation or a lapsed approval suspends it
/// as a 404 rather than serving text nobody authorized.
/// </para>
/// <para>
/// <b>Nothing here generates, approves or exports anything.</b> Approval stays the review
/// package's table, generation stays the drafts package's, and no byte here reaches any host:
/// the projection is served by this application's own anonymous routes.
/// </para>
/// </remarks>
public interface IProjectJourneyService
{
    /// <summary>
    /// Computes what a selection would publish, without publishing it.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The project or an asset is absent from this scope (404).</exception>
    /// <exception cref="ApprovalRequiredException">A selected asset carries no live approval (409).</exception>
    /// <exception cref="ApprovalStaleException">A selected approval no longer describes the stored state (409).</exception>
    /// <exception cref="MediaUnavailableException">An approved picture's bytes are gone (409).</exception>
    /// <exception cref="MilestoneNotConfirmedException">A selected milestone is not confirmed (422).</exception>
    /// <exception cref="ValidationFailedException">The selection is not one this package accepts (422).</exception>
    Task<JourneyPreview> PreviewAsync(
        OwnerScope owner,
        Guid projectId,
        JourneyPreviewRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes, republishes or revokes one project's journey.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The project is absent from this scope (404).</exception>
    /// <exception cref="JourneySlugConflictException">The slug belongs to another project (409).</exception>
    /// <exception cref="JourneySelectionStaleException">The preview hash no longer matches (409).</exception>
    /// <exception cref="RevisionConflictException">The revision moved under the writer (409).</exception>
    /// <exception cref="IfMatchRequiredException">A journey exists but no revision was supplied (428).</exception>
    Task<JourneyView> PublishAsync(
        OwnerScope owner,
        Guid projectId,
        JourneyPublishRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one project's journey inside a scope.</summary>
    /// <exception cref="ResourceNotFoundException">The project has no journey in this scope (404).</exception>
    Task<JourneyView> ReadAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serves one published snapshot to an anonymous reader. A slug that was never enabled, was
    /// revoked, or whose bindings moved answers the same 404 as a missing one.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">There is nothing published here (404).</exception>
    Task<JourneyPublicView> ReadPublicAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serves one published picture's bytes to an anonymous reader. Anything that is not an
    /// approval-covered picture of a live published snapshot answers the same 404.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">There is no such published picture (404).</exception>
    Task<PublicJourneyMedia> ReadPublicMediaAsync(
        string slug,
        Guid mediaId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One published picture's bytes as the anonymous media route serves them.
/// </summary>
/// <param name="MediaId">The stored picture.</param>
/// <param name="MediaType">The stored media type, always PNG.</param>
/// <param name="Bytes">The stored bytes.</param>
public sealed record PublicJourneyMedia(Guid MediaId, string MediaType, byte[] Bytes);
