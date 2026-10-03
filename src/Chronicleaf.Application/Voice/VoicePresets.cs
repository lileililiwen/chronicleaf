using Chronicleaf.Core;

namespace Chronicleaf.Application.Voice;

/// <summary>
/// The five presets this build ships, and the design's defaults (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// <para>
/// The presets are code, not rows. They are a fixed vocabulary that a profile names, so they are
/// versioned with the build that resolves them and cannot drift out of step with the resolver; a
/// stored <c>preset_id</c> that this build does not know is refused rather than defaulted.
/// </para>
/// <para>
/// Every preset's <see cref="VoiceValues.ForbiddenTerms"/> is the design's banned list, and every
/// preset's emoji value is <c>true</c> or <c>false</c> as its shape implies. The success scenario -
/// a preset that requests emoji, an owner who disables it - needs a preset that genuinely does
/// request emoji, which is why the three publishing-shaped presets set it rather than leaving it at
/// the default for all five.
/// </para>
/// </remarks>
public static class VoicePresets
{
    private static readonly VoiceValues Blog = new(
        Language: "zh",
        Tone: "restrained",
        Emoji: true,
        TitleMaxChars: 60,
        TechnicalDetail: "high",
        Marketing: "low",
        FirstPerson: true,
        ForbiddenTerms: VoiceDefaults.ForbiddenTerms);

    private static readonly IReadOnlyList<VoicePreset> Catalogue =
    [
        new(
            VoicePresetIds.TechnicalBlog,
            "Technical blog",
            "Long-form technical writing that leads with how something works.",
            Blog),
        new(
            VoicePresetIds.Xiaohongshu,
            "Xiaohongshu note",
            "A short, warm first-person note with room for a little personality.",
            new VoiceValues(
                Language: "zh",
                Tone: "casual",
                Emoji: true,
                TitleMaxChars: 40,
                TechnicalDetail: "low",
                Marketing: "medium",
                FirstPerson: true,
                ForbiddenTerms: VoiceDefaults.ForbiddenTerms)),
        new(
            VoicePresetIds.Linkedin,
            "LinkedIn post",
            "A professional post that states the work and what it changed.",
            new VoiceValues(
                Language: "en",
                Tone: "neutral",
                Emoji: false,
                TitleMaxChars: 120,
                TechnicalDetail: "medium",
                Marketing: "medium",
                FirstPerson: false,
                ForbiddenTerms: VoiceDefaults.ForbiddenTerms)),
        new(
            VoicePresetIds.Devlog,
            "Devlog entry",
            "A close record of one working session, in the order it happened.",
            new VoiceValues(
                Language: "en",
                Tone: "casual",
                Emoji: false,
                TitleMaxChars: 80,
                TechnicalDetail: "high",
                Marketing: "low",
                FirstPerson: true,
                ForbiddenTerms: VoiceDefaults.ForbiddenTerms)),
        new(
            VoicePresetIds.ClientWeekly,
            "Client weekly",
            "A short, plain update for somebody who was not in the room.",
            new VoiceValues(
                Language: "zh",
                Tone: "neutral",
                Emoji: false,
                TitleMaxChars: 50,
                TechnicalDetail: "low",
                Marketing: "low",
                FirstPerson: false,
                ForbiddenTerms: VoiceDefaults.ForbiddenTerms)),
    ];

    /// <summary>Every preset, in the order a settings surface lists them.</summary>
    public static IReadOnlyList<VoicePreset> All => Catalogue;

    /// <summary>
    /// The preset an id names, or <c>null</c> when it names none. The one place a preset id is
    /// turned into values; a caller that got a non-null preset back can resolve without re-checking.
    /// </summary>
    public static VoicePreset? Find(string? presetId)
    {
        if (presetId is null)
        {
            return null;
        }

        foreach (var preset in Catalogue)
        {
            if (string.Equals(preset.Id, presetId, StringComparison.Ordinal))
            {
                return preset;
            }
        }

        return null;
    }
}

/// <summary>
/// Turns a preset plus explicit overrides into the one effective voice everything else reads
/// (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// <para>
/// Precedence is decided here, once, and it is decided per field rather than per record. Each
/// effective value is <c>override ?? preset</c> on its own, so an override wins for <em>every</em>
/// field it names and the resolver has no branch that could let one field fall back to the preset
/// while another does not. There is no whole-record "is this an override" gate to get wrong.
/// </para>
/// <para>
/// A boolean override is <see cref="bool"/>? precisely so that <c>false</c> is a value and not an
/// absence; the null-coalescing above is what makes the success scenario work, and it works the
/// same way for <c>firstPerson</c> and for the title budget.
/// </para>
/// <para>
/// The resolver also reports, for each field, what the preset alone would have said. That is not
/// decoration: it is what lets a test assert the override won by comparing two values rather than
/// by asserting one value and hoping.
/// </para>
/// </remarks>
public static class VoiceProfileResolver
{
    /// <summary>
    /// Resolves a preset and its overrides. An unknown preset resolves to nothing rather than to a
    /// default, because a silent fallback is exactly the failure the requirement forbids.
    /// </summary>
    /// <exception cref="Application.Common.ValidationFailedException">The preset is not one of <see cref="VoicePresetIds"/>.</exception>
    public static ResolvedVoiceProfile Resolve(string? presetId, VoiceOverrides? overrides)
    {
        var preset = VoicePresets.Find(presetId)
            ?? throw new Application.Common.ValidationFailedException(
                "presetId",
                "The preset is not one this build offers.");

        return Resolve(preset, overrides ?? VoiceOverrides.Empty);
    }

    /// <summary>Resolves against a preset the caller already holds.</summary>
    public static ResolvedVoiceProfile Resolve(VoicePreset preset, VoiceOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(overrides);

        var values = preset.Values;
        var effective = new VoiceValues(
            Language: overrides.Language ?? values.Language,
            Tone: overrides.Tone ?? values.Tone,
            Emoji: overrides.Emoji ?? values.Emoji,
            TitleMaxChars: overrides.TitleMaxChars ?? values.TitleMaxChars,
            TechnicalDetail: overrides.TechnicalDetail ?? values.TechnicalDetail,
            Marketing: overrides.Marketing ?? values.Marketing,
            FirstPerson: overrides.FirstPerson ?? values.FirstPerson,
            ForbiddenTerms: overrides.ForbiddenTerms ?? values.ForbiddenTerms);

        var fields = new List<VoiceFieldResolution>(8)
        {
            Field("language", values.Language, effective.Language, overrides.Language is not null),
            Field("tone", values.Tone, effective.Tone, overrides.Tone is not null),
            Field("emoji", Bool(values.Emoji), Bool(effective.Emoji), overrides.Emoji is not null),
            Field(
                "titleMaxChars",
                values.TitleMaxChars.ToString(System.Globalization.CultureInfo.InvariantCulture),
                effective.TitleMaxChars.ToString(System.Globalization.CultureInfo.InvariantCulture),
                overrides.TitleMaxChars is not null),
            Field("technicalDetail", values.TechnicalDetail, effective.TechnicalDetail, overrides.TechnicalDetail is not null),
            Field("marketing", values.Marketing, effective.Marketing, overrides.Marketing is not null),
            Field("firstPerson", Bool(values.FirstPerson), Bool(effective.FirstPerson), overrides.FirstPerson is not null),
            Field(
                "forbiddenTerms",
                string.Join("|", values.ForbiddenTerms),
                string.Join("|", effective.ForbiddenTerms),
                overrides.ForbiddenTerms is not null),
        };

        return new ResolvedVoiceProfile(preset.Id, values, overrides, effective, fields);
    }

    private static VoiceFieldResolution Field(string name, string preset, string effective, bool overridden) =>
        new(name, preset, effective, overridden);

    private static string Bool(bool value) => value ? "true" : "false";
}
