using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Application.VideoStory;

/// <summary>
/// What an owner sends when they script an approved story into a video storyboard
/// (VIDEO-STORY-SCRIPTS-001).
/// </summary>
/// <param name="DurationSeconds">The target runtime: exactly 15, 30 or 60.</param>
/// <param name="SourceAssetId">The approved Markdown story this script is derived from.</param>
/// <param name="EvidenceRevision">The evidence head the owner scripted against; must still be current.</param>
/// <param name="VoiceRevision">The voice profile revision the owner scripted against; must still be current.</param>
/// <param name="Shots">The shot list, in play order, covering the runtime exactly.</param>
public sealed record GenerateVideoStoryRequest(
    int DurationSeconds,
    Guid SourceAssetId,
    long EvidenceRevision,
    long VoiceRevision,
    IReadOnlyList<VideoStoryShotRequest> Shots);

/// <summary>
/// One shot the owner scripted, as the request carries it.
/// </summary>
/// <param name="ScreenshotMediaId">A selected screenshot's media in the frozen evidence bundle.</param>
/// <param name="Caption">The shot's display caption, 1 to 1000 characters.</param>
/// <param name="Narration">The single spoken line timed to this shot, 1 to 2000 characters.</param>
/// <param name="Transition">How this shot hands off: <c>cut</c> or <c>fade</c>.</param>
/// <param name="StartMs">When the shot starts, from zero, contiguous with its neighbours.</param>
/// <param name="EndMs">When the shot ends: at least 500ms after it starts.</param>
/// <param name="SourceEvidenceIds">The evidence this shot cites: at least one id the frozen bundle holds.</param>
public sealed record VideoStoryShotRequest(
    Guid ScreenshotMediaId,
    string Caption,
    string Narration,
    string Transition,
    long StartMs,
    long EndMs,
    IReadOnlyList<Guid> SourceEvidenceIds);

/// <summary>One scripted video story, as the generate route answers it.</summary>
/// <param name="AssetId">The story asset, new or already holding earlier revisions.</param>
/// <param name="Revision">The revision this generation appended.</param>
/// <param name="BodyHash">Lowercase SHA-256 hex over the stored storyboard bytes.</param>
/// <param name="DurationSeconds">The runtime the shots cover: 15, 30 or 60.</param>
/// <param name="ShotCount">How many shots the stored script holds.</param>
/// <param name="TotalMs">The covered runtime in milliseconds: exactly the duration in seconds times 1000.</param>
/// <param name="SourceAssetId">The approved story this script was derived from.</param>
/// <param name="SourceRevision">That story's head revision at generate time.</param>
/// <param name="EvidenceRevision">The evidence head the script is grounded in.</param>
/// <param name="VoiceRevision">The voice profile revision the script carries a snapshot of.</param>
/// <param name="ImageOrder">The pictures the shots depict, in first-appearance order.</param>
public sealed record GeneratedVideoStory(
    Guid AssetId,
    long Revision,
    string BodyHash,
    int DurationSeconds,
    int ShotCount,
    long TotalMs,
    Guid SourceAssetId,
    long SourceRevision,
    long EvidenceRevision,
    long VoiceRevision,
    IReadOnlyList<Guid> ImageOrder);

/// <summary>One approved video story as a hand-off package (VIDEO-STORY-SCRIPTS-002).</summary>
/// <param name="FileName">The attachment name, carrying the asset id and revision.</param>
/// <param name="ContentType">Always the ZIP media type: scripts plus the depicted pictures.</param>
/// <param name="Bytes">The package bytes.</param>
/// <param name="Sha256">Lowercase SHA-256 hex over <see cref="Bytes"/>.</param>
/// <param name="AssetId">The exported asset.</param>
/// <param name="Revision">The exported revision, exactly the approved one.</param>
public sealed record VideoStoryExport(
    string FileName,
    string ContentType,
    byte[] Bytes,
    string Sha256,
    Guid AssetId,
    long Revision);

/// <summary>One milestone's video-story asset, as the discovery read answers it (VIDEO-STORY-PANEL-003).</summary>
/// <param name="AssetId">The story asset, or <c>null</c> when the milestone holds no script yet.</param>
/// <param name="Revision">The head revision, or 0 when there is no script.</param>
/// <param name="BodyHash">Lowercase SHA-256 hex over the stored storyboard bytes, or empty when absent.</param>
/// <param name="DurationSeconds">The runtime the stored shots cover, or 0 when absent.</param>
/// <param name="ShotCount">How many shots the stored script holds, or 0 when absent.</param>
/// <param name="EvidenceRevision">The evidence head the stored revision is grounded in, or 0 when absent.</param>
/// <param name="VoiceRevision">The voice profile revision the stored revision carries, or 0 when absent.</param>
/// <param name="UpdatedAt">When the asset last changed.</param>
public sealed record VideoStorySummary(
    Guid? AssetId,
    long Revision,
    string BodyHash,
    int DurationSeconds,
    int ShotCount,
    long EvidenceRevision,
    long VoiceRevision,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The video-story use cases (VIDEO-STORY-SCRIPTS-001/002).
/// </summary>
/// <remarks>
/// Every method is scope-bound: an id belonging to another scope is answered as absent. Nothing
/// here synthesizes video, renders footage, records a screen or reaches any publishing endpoint:
/// generation validates and stores a reviewed shot list, export serves the approved scripts to
/// the owner that asked for them, and publication is refused unconditionally. The finished
/// scripts are text a human acts on; the labels in them are a plan, never a promise of footage.
/// </remarks>
public interface IVideoStoryService
{
    /// <summary>
    /// Validates an owner's shot list against the approved story, the frozen evidence head and
    /// the current voice, and stores it as a draft revision of the milestone's story asset, with
    /// no approval row of its own.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The milestone or source is absent or in another scope (404).</exception>
    /// <exception cref="Common.ValidationFailedException">The request names no duration, source, revision or shot the package stores (422).</exception>
    /// <exception cref="InvalidStorySchemaException">The shot list does not cover the runtime exactly, or cites material the frozen bundle does not hold (422 <c>invalid_story_schema</c>).</exception>
    /// <exception cref="Content.ApprovalRequiredException">The source was never approved (409).</exception>
    /// <exception cref="Content.ApprovalStaleException">The source approval no longer describes the stored state (409).</exception>
    /// <exception cref="Common.RevisionConflictException">The named evidence or voice head has moved (409).</exception>
    Task<GeneratedVideoStory> GenerateAsync(
        OwnerScope owner,
        Guid milestoneId,
        GenerateVideoStoryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the milestone's video-story asset for discovery (VIDEO-STORY-PANEL-003).
    /// </summary>
    /// <remarks>
    /// A milestone with no script is a normal empty state, not a missing resource: the answer
    /// carries a null asset rather than a 404. The milestone itself is read and scoped first,
    /// so an absent or foreign milestone is still the delivered 404.
    /// </remarks>
    /// <exception cref="Common.ResourceNotFoundException">The milestone is absent or in another scope (404).</exception>
    Task<VideoStorySummary> ReadForMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serves the approved revision as a ZIP holding <c>storyboard.json</c>,
    /// <c>narration.md</c>, <c>caption.srt</c>, the depicted pictures in first-appearance order
    /// and their manifest. Never approved is <c>approval_required</c>; once approved but moved -
    /// a newer unapproved revision, a moved evidence head, a changed caption set - is
    /// <c>approval_stale</c>. A picture whose bytes are gone is <c>media_unavailable</c>: the
    /// scripts stay previewable through the review routes, but no picture package is served
    /// without its pictures.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="Common.ValidationFailedException">The asset is not a video story (422).</exception>
    /// <exception cref="Content.ApprovalRequiredException">Nothing live authorizes this export (409).</exception>
    /// <exception cref="Content.ApprovalStaleException">The approval no longer describes the stored state (409).</exception>
    /// <exception cref="Content.MediaUnavailableException">A depicted picture's bytes are gone (409).</exception>
    Task<VideoStoryExport> ExportAsync(
        OwnerScope owner,
        Guid assetId,
        long? revision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refuses native video publication unconditionally (422 <c>publisher_unsupported</c>).
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    Task PublishAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default);
}
