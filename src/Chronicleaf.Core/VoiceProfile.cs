using System.Globalization;

namespace Chronicleaf.Core;

/// <summary>
/// The closed vocabularies one voice profile is written in (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// The sets are closed on purpose. A profile is a rule set a checker runs against text, and a rule a
/// reader cannot spell out is a rule nobody can audit, so a value outside these sets is refused at
/// the edge rather than stored and discovered later.
/// </remarks>
public static class VoiceVocabularies
{
    /// <summary>The languages a profile may declare. <c>zh</c> and <c>en</c> only, by design.</summary>
    public static readonly IReadOnlyList<string> Languages = ["zh", "en"];

    /// <summary>The tones a profile may declare, from most to least formal.</summary>
    public static readonly IReadOnlyList<string> Tones = ["restrained", "neutral", "casual"];

    /// <summary>The verbosity of technical content a profile may declare.</summary>
    public static readonly IReadOnlyList<string> TechnicalDetails = ["low", "medium", "high"];

    /// <summary>How far a profile may lean on product language.</summary>
    public static readonly IReadOnlyList<string> MarketingLevels = ["low", "medium", "high"];

    public static bool IsLanguage(string? value) => value is not null && Languages.Contains(value, StringComparer.Ordinal);

    public static bool IsTone(string? value) => value is not null && Tones.Contains(value, StringComparer.Ordinal);

    public static bool IsTechnicalDetail(string? value) =>
        value is not null && TechnicalDetails.Contains(value, StringComparer.Ordinal);

    public static bool IsMarketing(string? value) =>
        value is not null && MarketingLevels.Contains(value, StringComparer.Ordinal);
}

/// <summary>
/// The bounds every voice profile and its terms obey (VOICE-PROFILES-001/002).
/// </summary>
/// <remarks>
/// The design fixes the title budget's own range at 10..160 and caps the term list at 100 entries
/// of at most 80 characters each. Both caps are enforced here rather than only in the request
/// handler, so the domain itself cannot hold a profile the API would have to reject.
/// </remarks>
public static class VoiceLimits
{
    /// <summary>The narrowest title budget a profile may declare.</summary>
    public const int MinTitleMaxChars = 10;

    /// <summary>The widest title budget a profile may declare.</summary>
    public const int MaxTitleMaxChars = 160;

    /// <summary>At most this many forbidden terms on one profile.</summary>
    public const int MaxForbiddenTerms = 100;

    /// <summary>At most this many characters in one forbidden term.</summary>
    public const int ForbiddenTermMaxLength = 80;

    /// <summary>At most this many characters in a stored draft's title.</summary>
    public const int MaxTitleTextLength = 200;

    /// <summary>At most this many characters in a stored draft's body.</summary>
    public const int MaxBodyTextLength = 64 * 1024;

    /// <summary>At most this many characters in a preset identifier.</summary>
    public const int PresetIdMaxLength = 32;

    public static bool IsTitleMaxChars(int value) => value is >= MinTitleMaxChars and <= MaxTitleMaxChars;
}

/// <summary>
/// The closed set of the four profile shapes a preset defines (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// These are the identifiers stored in <c>voice_profiles.preset_id</c>. The set is closed so a
/// profile that names a shape nobody can resolve is refused with 422 rather than quietly stored
/// against a default the owner never chose.
/// </remarks>
public static class VoicePresetIds
{
    public const string TechnicalBlog = "technical-blog";
    public const string Xiaohongshu = "xiaohongshu";
    public const string Linkedin = "linkedin";
    public const string Devlog = "devlog";
    public const string ClientWeekly = "client-weekly";

    public static readonly IReadOnlyList<string> All =
    [
        TechnicalBlog,
        Xiaohongshu,
        Linkedin,
        Devlog,
        ClientWeekly,
    ];

    /// <summary>
    /// The identifier as stored, or <c>null</c> when it is not one of <see cref="All"/>. Compared
    /// ordinally: a preset name is a wire value, so <c>Devlog</c> is a different string and is
    /// refused rather than folded into a preset the owner did not name.
    /// </summary>
    public static string? Normalize(string? presetId) =>
        presetId is not null && All.Contains(presetId, StringComparer.Ordinal) ? presetId : null;
}

/// <summary>
/// The eight resolved voice values a checker reads (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// This is the preset layer's shape: every field is a real value, none of them nullable, and there
/// is no override on this type at all. That is what makes it safe to use as the base of a
/// resolution - see <see cref="VoiceOverrides"/> for the other half of the picture.
/// </remarks>
public sealed record VoiceValues(
    string Language,
    string Tone,
    bool Emoji,
    int TitleMaxChars,
    string TechnicalDetail,
    string Marketing,
    bool FirstPerson,
    IReadOnlyList<string> ForbiddenTerms)
{
    /// <summary>
    /// The design's stated defaults, used wherever a profile has no preset of its own yet. They are
    /// the same numbers the proposal fixes, kept in the domain so the API, the UI and the checker
    /// cannot each hold their own copy.
    /// </summary>
    public static VoiceValues Defaults { get; } = new(
        Language: VoiceVocabularies.Languages[0],
        Tone: VoiceVocabularies.Tones[0],
        Emoji: false,
        TitleMaxChars: 60,
        TechnicalDetail: VoiceVocabularies.TechnicalDetails[2],
        Marketing: VoiceVocabularies.MarketingLevels[0],
        FirstPerson: true,
        ForbiddenTerms: VoiceDefaults.ForbiddenTerms);

    /// <summary>True when every value of this instance is inside its declared vocabulary.</summary>
    public bool IsWellFormed() =>
        VoiceVocabularies.IsLanguage(Language) &&
        VoiceVocabularies.IsTone(Tone) &&
        VoiceLimits.IsTitleMaxChars(TitleMaxChars) &&
        VoiceVocabularies.IsTechnicalDetail(TechnicalDetail) &&
        VoiceVocabularies.IsMarketing(Marketing) &&
        VoiceLimits.MaxForbiddenTerms >= ForbiddenTerms.Count;
}

/// <summary>
/// The explicitly stored project-level deviations from a preset (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// <para>
/// Every field is nullable and a null field means "not an override". That is the whole reason this
/// type exists in this shape: an override record cannot tell a stored <c>false</c> from an absent
/// value, because both would be the same non-nullable <c>bool</c> defaulting to <c>false</c>. Here
/// <see cref="Emoji"/> is <see cref="bool"/>?, so a profile that says <c>emoji: false</c> over a
/// preset that says <c>emoji: true</c> is representable, and it resolves to <c>false</c> - which is
/// the requirement's success scenario, not a special case in the resolver.
/// </para>
/// <para>
/// A profile with no overrides at all is <see cref="Empty"/>, and it resolves to exactly its
/// preset. That is a testable equality, not an approximation.
/// </para>
/// </remarks>
public sealed record VoiceOverrides(
    string? Language = null,
    string? Tone = null,
    bool? Emoji = null,
    int? TitleMaxChars = null,
    string? TechnicalDetail = null,
    string? Marketing = null,
    bool? FirstPerson = null,
    IReadOnlyList<string>? ForbiddenTerms = null)
{
    /// <summary>No field is an override. Resolves to exactly the preset.</summary>
    public static VoiceOverrides Empty { get; } = new();

    /// <summary>True when no field overrides anything, so resolution is the preset untouched.</summary>
    public bool IsEmpty =>
        Language is null &&
        Tone is null &&
        Emoji is null &&
        TitleMaxChars is null &&
        TechnicalDetail is null &&
        Marketing is null &&
        FirstPerson is null &&
        ForbiddenTerms is null;

    /// <summary>
    /// The names of the fields this record actually overrides, in the declared order. The settings
    /// surface uses it to label each effective value with where it came from, which is what makes
    /// "the override won" visible rather than inferred.
    /// </summary>
    public IReadOnlyList<string> OverriddenFields =>
    [
        .. new[]
        {
            (Key: "language", Present: Language is not null),
            (Key: "tone", Present: Tone is not null),
            (Key: "emoji", Present: Emoji is not null),
            (Key: "titleMaxChars", Present: TitleMaxChars is not null),
            (Key: "technicalDetail", Present: TechnicalDetail is not null),
            (Key: "marketing", Present: Marketing is not null),
            (Key: "firstPerson", Present: FirstPerson is not null),
            (Key: "forbiddenTerms", Present: ForbiddenTerms is not null),
        }.Where(entry => entry.Present).Select(entry => entry.Key),
    ];
}

/// <summary>
/// The terms and shape limits every preset starts from (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// The design fixes the default banned list as 革命性, 震撼, <c>game-changing</c> and
/// <c>exciting news</c>. They are held once, here, so the defaults, the presets and the API
/// describe cannot drift apart.
/// </remarks>
public static class VoiceDefaults
{
    /// <summary>The design's default forbidden terms, in the order the design lists them.</summary>
    public static readonly IReadOnlyList<string> ForbiddenTerms =
    [
        "革命性",
        "震撼",
        "game-changing",
        "exciting news",
    ];
}

/// <summary>
/// One stored voice profile: a preset plus the deviations the owner explicitly set
/// (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// <para>
/// The row holds <see cref="PresetId"/> and <see cref="Overrides"/>; it deliberately does not hold a
/// resolved copy. A resolved copy would be a second source of truth that can disagree with its own
/// inputs, and the whole point of the requirement is that the effective value is <em>derived</em> at
/// read time from the preset and the overrides, never remembered.
/// </para>
/// <para>
/// <see cref="ProjectId"/> is the project's own id and is part of this record's identity through the
/// unique <c>(scope_id, project_id)</c> index, so a project can hold one profile and one profile can
/// never answer for two projects.
/// </para>
/// </remarks>
public sealed class VoiceProfile
{
    public VoiceProfile(
        Guid id,
        Guid scopeId,
        Guid projectId,
        string presetId,
        VoiceOverrides overrides,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(presetId);
        ArgumentNullException.ThrowIfNull(overrides);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A voice profile needs a non-empty id.", nameof(id));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A voice profile needs a non-empty project.", nameof(projectId));
        }

        if (VoicePresetIds.Normalize(presetId) is null)
        {
            throw new ArgumentException("The preset is not one of the supported preset identifiers.", nameof(presetId));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "The first stored revision is 1.");
        }

        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        PresetId = presetId;
        Overrides = overrides;
        Revision = revision;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>This profile's identity.</summary>
    public Guid Id { get; }

    /// <summary>The installation scope the profile belongs to and is readable from.</summary>
    public Guid ScopeId { get; }

    /// <summary>The project whose voice this profile states.</summary>
    public Guid ProjectId { get; }

    /// <summary>One of <see cref="VoicePresetIds"/>.</summary>
    public string PresetId { get; }

    /// <summary>The owner's explicit deviations from <see cref="PresetId"/>.</summary>
    public VoiceOverrides Overrides { get; }

    /// <summary>The optimistic revision an <c>If-Match</c> must carry to replace this profile.</summary>
    public long Revision { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }
}

/// <summary>
/// The voice a piece of work was written under, copied at the moment it was created
/// (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// <para>
/// This is the requirement's boundary scenario, and the type is what makes it true by
/// construction: it is a record of resolved values, not a reference to a profile row. A profile edit
/// produces a new profile revision for <em>future</em> work and cannot reach a snapshot that was
/// already taken, because there is no pointer from here back to the profile to follow.
/// </para>
/// <para>
/// <see cref="Fingerprint"/> is a lowercase SHA-256 over the canonical serialized form of this
/// snapshot's values. It exists so "the snapshot did not change" is observable from outside the
/// process - a test can compare the stored bytes and the fingerprint before and after a profile
/// edit, rather than trusting that nothing reached in.
/// </para>
/// </remarks>
public sealed record VoiceSnapshot(
    string PresetId,
    long ProfileRevision,
    DateTimeOffset CapturedAt,
    VoiceValues Values)
{
    /// <summary>
    /// The identity a snapshot is compared by. Two snapshots with the same fingerprint describe the
    /// same voice; a profile edit that changes any resolved value changes this.
    /// <para>
    /// It is computed from <see cref="Values"/> on every read rather than carried alongside them,
    /// so a fingerprint can never disagree with the values it is supposed to describe.
    /// </para>
    /// </summary>
    public string Fingerprint => VoiceSnapshotHasher.Fingerprint(Values);

    /// <summary>
    /// A stable diagnostic label for a draft row that carries this snapshot. It names the snapshot,
    /// never the profile row, because a draft outlives the profile revision it was written under.
    /// </summary>
    public static string Label(Guid draftId) =>
        string.Create(CultureInfo.InvariantCulture, $"voice-drafts/{draftId:N}#snapshot");
}
