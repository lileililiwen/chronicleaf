using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.ContentVariants;

/// <summary>
/// What an owner asks for when they derive a reusable format from an approved story
/// (CONTENT-ASSET-VARIANTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The source is named, not inferred: <see cref="SourceAssetId"/> is the approved
/// <c>markdown-story</c> asset, or null to use the milestone's own story. The two revisions are the
/// heads the owner was looking at, and both must still be current: deriving from a moved evidence
/// head or a moved voice profile is refused rather than rendered against stale provenance, because a
/// variant of stale content is stale.
/// </para>
/// </remarks>
/// <param name="Type">One of <see cref="ContentVariantTypes"/>.</param>
/// <param name="SourceAssetId">The approved story to derive from, or null for the milestone's story.</param>
/// <param name="EvidenceRevision">The evidence head revision the owner was looking at.</param>
/// <param name="VoiceRevision">The project voice profile revision the owner was looking at.</param>
public sealed record DeriveVariantRequest(
    string? Type,
    Guid? SourceAssetId,
    long EvidenceRevision,
    long VoiceRevision);

/// <summary>
/// One derived revision, as the derive route answers it (CONTENT-ASSET-VARIANTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The answer names the new asset, never the source's approval: the derivative is a draft with no
/// approval row, and the review package's own read is what reports that. The body hash is the digest
/// of exactly the bytes stored, so a later approval binds what the owner actually saw.
/// </para>
/// </remarks>
/// <param name="AssetId">The new variant asset.</param>
/// <param name="Type">The derived format.</param>
/// <param name="Revision">The variant's head revision (1 for a first derivation).</param>
/// <param name="BodyHash">The digest of exactly the bytes stored.</param>
/// <param name="SourceAssetId">The approved story this was derived from.</param>
/// <param name="SourceRevision">The source revision the derivation read.</param>
/// <param name="EvidenceRevision">The evidence head the derivation is bound to.</param>
/// <param name="VoiceRevision">The voice profile revision the derivation is bound to.</param>
public sealed record DerivedVariant(
    Guid AssetId,
    string Type,
    long Revision,
    string BodyHash,
    Guid SourceAssetId,
    long SourceRevision,
    long EvidenceRevision,
    long VoiceRevision);

/// <summary>
/// One variant asset in a milestone's list, as the list route returns it
/// (CONTENT-ASSET-VARIANTS-001).
/// </summary>
/// <param name="AssetId">The variant asset.</param>
/// <param name="Type">The derived format.</param>
/// <param name="CurrentRevision">The variant's head revision.</param>
/// <param name="BodyHash">The digest of the head revision's bytes.</param>
/// <param name="EvidenceRevision">The evidence head the head revision is bound to.</param>
/// <param name="VoiceRevision">The voice profile revision the head revision is bound to.</param>
/// <param name="UpdatedAt">When the head revision last advanced; UTC.</param>
public sealed record VariantSummary(
    Guid AssetId,
    string Type,
    long CurrentRevision,
    string BodyHash,
    long EvidenceRevision,
    long VoiceRevision,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The variant use cases the API layer calls (CONTENT-ASSET-VARIANTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here generates, approves or publishes.</b> Deriving re-renders an approved story's
/// accepted summary through a deterministic template: no provider, no credential and no socket, so
/// a variant stays derivable when every provider is down. Approval is the review package's table
/// bound to the variant's own revision and body hash; publication is a later package's.
/// </para>
/// </remarks>
public interface IContentVariantService
{
    /// <summary>
    /// Renders one variant format from an approved story and stores it as a draft asset.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The milestone or the source asset is absent from this scope (404).</exception>
    /// <exception cref="ProjectArchivedException">The milestone's project is archived (409).</exception>
    /// <exception cref="ApprovalRequiredException">The source story was never approved (409).</exception>
    /// <exception cref="ApprovalStaleException">The source approval no longer describes the stored state (409).</exception>
    /// <exception cref="RevisionConflictException">A presented revision is not the stored head (409).</exception>
    /// <exception cref="ValidationFailedException">The type or the source is not one this package derives from (422).</exception>
    Task<DerivedVariant> DeriveAsync(
        OwnerScope owner,
        Guid milestoneId,
        DeriveVariantRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the derived assets a milestone holds, in picker order: every variant
    /// format, plus the LinkedIn copy and Xiaohongshu pack kinds
    /// (CONTENT-PACK-PANELS-004) only when <paramref name="includePacks"/> is set,
    /// so a panel can select a previously derived pack over this delivered route
    /// while the default read stays exactly what older callers rely on.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The milestone is absent or in another scope (404).</exception>
    Task<IReadOnlyList<VariantSummary>> ListAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken = default,
        bool includePacks = false);
}
