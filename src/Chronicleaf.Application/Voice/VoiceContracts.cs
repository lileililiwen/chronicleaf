using Chronicleaf.Core;

namespace Chronicleaf.Application.Voice;

/// <summary>
/// The error codes this package's own refusals add to the delivered envelope
/// (VOICE-PROFILES-001/002).
/// </summary>
/// <remarks>
/// The envelope itself is the delivered <c>{code,message,traceId,fieldErrors?}</c> and the status
/// mapping is unchanged; only the vocabulary grows. A preset that cannot be resolved and a profile
/// that another scope owns are both <see cref="ValidationFailed"/> and <see cref="NotFound"/>
/// respectively, because they are the delivered refusals and reusing them is what keeps a caller from
/// learning this package's internals by comparing codes.
/// </remarks>
public static class VoiceError
{
    /// <summary>Semantic validation failed. 422, with per-field detail.</summary>
    public const string ValidationFailed = "validation_failed";

    /// <summary>The resource is absent, or belongs to another scope. 404.</summary>
    public const string NotFound = "not_found";

    /// <summary>The <c>If-Match</c> a mutation required was not presented. 428.</summary>
    public const string IfMatchRequired = "if_match_required";

    /// <summary>The presented <c>If-Match</c> is not the stored revision. 409.</summary>
    public const string RevisionConflict = "revision_conflict";
}

/// <summary>
/// One preset as the catalogue publishes it (VOICE-PROFILES-001).
/// </summary>
/// <param name="Id">One of <see cref="VoicePresetIds"/>.</param>
/// <param name="Label">A human-readable name for a settings surface; it is never a rule input.</param>
/// <param name="Description">What this preset is for, in one sentence.</param>
/// <param name="Values">The values this preset resolves to before any override.</param>
public sealed record VoicePreset(string Id, string Label, string Description, VoiceValues Values);

/// <summary>
/// One field of a resolved profile, with the layer that decided it (VOICE-PROFILES-001).
/// </summary>
/// <param name="Field">The field name, matching the wire key the settings surface edits.</param>
/// <param name="PresetValue">The value the preset alone would produce.</param>
/// <param name="EffectiveValue">The value after the owner's explicit overrides.</param>
/// <param name="Overridden">True when an explicit override, not the preset, produced this value.</param>
public sealed record VoiceFieldResolution(
    string Field,
    string PresetValue,
    string EffectiveValue,
    bool Overridden);

/// <summary>
/// Everything a settings surface needs to show how a profile resolved, and why
/// (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// <see cref="Effective"/> is what the checker runs. <see cref="Fields"/> is what makes the
/// precedence observable field by field: an owner who disabled emoji over a preset that allowed it
/// can see the preset said <c>true</c>, the override said <c>false</c>, and the effective value is
/// <c>false</c> - rather than having to trust that the override won.
/// </remarks>
public sealed record ResolvedVoiceProfile(
    string PresetId,
    VoiceValues PresetValues,
    VoiceOverrides Overrides,
    VoiceValues Effective,
    IReadOnlyList<VoiceFieldResolution> Fields);

/// <summary>
/// A stored profile as the API returns it (VOICE-PROFILES-001).
/// </summary>
/// <param name="Id">The profile's identity.</param>
/// <param name="ProjectId">The project it states the voice for.</param>
/// <param name="PresetId">One of <see cref="VoicePresetIds"/>.</param>
/// <param name="Overrides">The owner's explicit deviations; empty means "exactly the preset".</param>
/// <param name="Revision">The optimistic revision an <c>If-Match</c> must carry.</param>
/// <param name="Stored">
/// False when the project has never had a voice profile stored. The defaults below are then
/// computed, not read, and <see cref="Id"/> and <see cref="Revision"/> are zero - so a caller can
/// tell "resolved from the defaults" from "stored and it happens to equal the defaults", and a
/// first save knows it has no revision to present.
/// </param>
/// <param name="CreatedAt">When the profile was first stored; UTC.</param>
/// <param name="UpdatedAt">When the profile was last replaced; UTC.</param>
/// <param name="Resolved">Preset, overrides and effective values side by side.</param>
public sealed record VoiceProfileView(
    Guid Id,
    Guid ProjectId,
    string PresetId,
    VoiceOverrides Overrides,
    long Revision,
    bool Stored,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ResolvedVoiceProfile Resolved);

/// <summary>What the owner wants stored as a project's voice (VOICE-PROFILES-001).</summary>
/// <param name="PresetId">The preset to resolve against. Unknown values are refused with 422.</param>
/// <param name="Overrides">Explicit deviations; only the fields the owner set are applied.</param>
/// <param name="ExpectedRevision">
/// The revision this replacement is based on, presented through <c>If-Match</c>. A replacement that
/// does not present it is refused with 428; one that presents a stale value is refused with 409 and
/// changes nothing.
/// </param>
public sealed record VoiceProfileWrite(string PresetId, VoiceOverrides Overrides, long ExpectedRevision);

/// <summary>One draft in a list page (VOICE-PROFILES-001/002).</summary>
public sealed record VoiceDraftSummary(
    Guid Id,
    Guid ProjectId,
    string Status,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string SnapshotFingerprint,
    int FindingCount);

/// <summary>
/// A draft as the API returns it, with the voice it was created under
/// (VOICE-PROFILES-001/002).
/// </summary>
/// <param name="Draft">The stored draft, with its snapshot.</param>
/// <param name="Snapshot">The copied voice, and the profile revision it was taken at.</param>
/// <param name="Findings">What the checker reports for this text under that snapshot.</param>
public sealed record VoiceDraftView(
    VoiceDraft Draft,
    VoiceSnapshot Snapshot,
    IReadOnlyList<VoiceFinding> Findings);

/// <summary>What the owner wants written as a new draft (VOICE-PROFILES-001).</summary>
public sealed record VoiceDraftCreate(string Title, string Body);

/// <summary>What the owner wants stored over a draft's existing text (VOICE-PROFILES-002).</summary>
/// <param name="ExpectedRevision">Presented through <c>If-Match</c>; 428 when absent, 409 when stale.</param>
public sealed record VoiceDraftEdit(string Title, string Body, long ExpectedRevision);

/// <summary>
/// The result of judging text against a voice, with the voice named so the answer is
/// self-describing (VOICE-PROFILES-002).
/// </summary>
/// <remarks>
/// The snapshot is returned alongside the findings rather than being implied by them: a caller
/// needs to know <em>which</em> voice these findings were measured against, and for a draft that is
/// the voice it was created with rather than the profile's current one.
/// </remarks>
public sealed record VoiceCheckResult(
    VoiceSnapshot Snapshot,
    IReadOnlyList<VoiceFinding> Findings)
{
    /// <summary>True when nothing was flagged. The only state in which approval may proceed.</summary>
    public bool IsClean => Findings.Count == 0;
}

/// <summary>
/// The owner-facing voice use cases (VOICE-PROFILES-001/002).
/// </summary>
/// <remarks>
/// Every method is scope-bound: an id belonging to another scope is answered as absent rather than
/// found, and a caller cannot name a scope of its own. The preset catalogue is the one exception,
/// and it is scope-free by nature - it is this repository's fixed vocabulary, not stored state.
/// </remarks>
public interface IVoiceProfileService
{
    /// <summary>Every preset this build understands, in the order a settings surface lists them.</summary>
    IReadOnlyList<VoicePreset> Presets();

    /// <summary>
    /// Reads a project's stored voice. A project with no stored profile yet resolves to the
    /// design's defaults with no overrides and reports <c>profileStored: false</c>, so a caller can
    /// render an editor without a special case for "never configured".
    /// </summary>
    Task<VoiceProfileView> GetAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a project's voice, replacing the version at <see cref="VoiceProfileWrite.ExpectedRevision"/>.
    /// An unknown preset, an out-of-vocabulary override or an out-of-range title budget is refused
    /// with 422 and nothing is written.
    /// </summary>
    Task<VoiceProfileView> SaveAsync(
        Guid scopeId,
        Guid projectId,
        VoiceProfileWrite write,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Judges caller-supplied text against the project's <em>current</em> effective voice, and
    /// writes nothing. This is the stateless check a compose surface runs before anything is saved.
    /// </summary>
    Task<VoiceCheckResult> CheckAsync(
        Guid scopeId,
        Guid projectId,
        string title,
        string body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a draft, copying the project's effective voice at this instant into the draft's own
    /// snapshot. Later profile edits cannot reach it.
    /// </summary>
    Task<VoiceDraftView> CreateDraftAsync(
        Guid scopeId,
        Guid projectId,
        VoiceDraftCreate create,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one draft with its snapshot and the findings for its stored text.</summary>
    Task<VoiceDraftView> GetDraftAsync(
        Guid scopeId,
        Guid draftId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-judges a stored draft against <em>its own</em> snapshot. This is a read: the stored title
    /// and body are not touched by it, before or after.
    /// </summary>
    Task<VoiceCheckResult> CheckDraftAsync(
        Guid scopeId,
        Guid draftId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores new text over a draft, byte for byte as sent, at the presented revision. The draft's
    /// snapshot is not re-taken: a draft is judged under the voice it was written with, and moving
    /// it to a new voice is an explicit regeneration rather than a side effect of an edit.
    /// </summary>
    Task<VoiceDraftView> EditDraftAsync(
        Guid scopeId,
        Guid draftId,
        VoiceDraftEdit edit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a draft. Refused with 422 while any finding remains, and the stored text is not
    /// altered either way - approval is a judgement gate, never an edit.
    /// </summary>
    Task<VoiceDraftView> ApproveDraftAsync(
        Guid scopeId,
        Guid draftId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a project's drafts newest first, after the cursor position. Default 25, maximum 100.
    /// </summary>
    Task<VoiceDraftPage> ListDraftsAsync(
        Guid scopeId,
        Guid projectId,
        VoiceDraftPageRequest page,
        CancellationToken cancellationToken = default);
}

/// <summary>The list input for a project's drafts (VOICE-PROFILES-001/002).</summary>
/// <param name="PageSize">Requested size; clamped by the shared list policy.</param>
/// <param name="Cursor">An opaque, scope-bound position from a previous page, or null to start.</param>
public sealed record VoiceDraftPageRequest(int? PageSize, string? Cursor);

/// <summary>One page of drafts, with the cursor that continues it (VOICE-PROFILES-001/002).</summary>
public sealed record VoiceDraftPage(
    IReadOnlyList<VoiceDraftSummary> Items,
    string? NextCursor,
    int PageSize);
