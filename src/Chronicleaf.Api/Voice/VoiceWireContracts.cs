using System.Text.Json.Serialization;
using Chronicleaf.Application.Voice;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Voice;

/// <summary>The wire shape of the preset catalogue (VOICE-PROFILES-001).</summary>
public sealed record VoicePresetResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("values")] VoiceValuesResponse Values)
{
    public static VoicePresetResponse From(VoicePreset preset) =>
        new(preset.Id, preset.Label, preset.Description, VoiceValuesResponse.From(preset.Values));
}

/// <summary>
/// The wire shape of the eight resolved voice values, with every name pinned so the contract cannot
/// drift with serializer policy (VOICE-PROFILES-001).
/// </summary>
public sealed record VoiceValuesResponse(
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("tone")] string Tone,
    [property: JsonPropertyName("emoji")] bool Emoji,
    [property: JsonPropertyName("titleMaxChars")] int TitleMaxChars,
    [property: JsonPropertyName("technicalDetail")] string TechnicalDetail,
    [property: JsonPropertyName("marketing")] string Marketing,
    [property: JsonPropertyName("firstPerson")] bool FirstPerson,
    [property: JsonPropertyName("forbiddenTerms")] IReadOnlyList<string> ForbiddenTerms)
{
    public static VoiceValuesResponse From(VoiceValues values) =>
        new(
            values.Language,
            values.Tone,
            values.Emoji,
            values.TitleMaxChars,
            values.TechnicalDetail,
            values.Marketing,
            values.FirstPerson,
            values.ForbiddenTerms);
}

/// <summary>
/// The wire shape of one field's resolution: what the preset said, what won, and which one it was
/// (VOICE-PROFILES-001).
/// </summary>
public sealed record VoiceFieldResponse(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("presetValue")] string PresetValue,
    [property: JsonPropertyName("effectiveValue")] string EffectiveValue,
    [property: JsonPropertyName("overridden")] bool Overridden)
{
    public static VoiceFieldResponse From(VoiceFieldResolution resolution) =>
        new(resolution.Field, resolution.PresetValue, resolution.EffectiveValue, resolution.Overridden);
}

/// <summary>The wire shape of a stored profile, with preset, overrides and effective values (VOICE-PROFILES-001).</summary>
public sealed record VoiceProfileResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("projectId")] Guid ProjectId,
    [property: JsonPropertyName("presetId")] string PresetId,
    [property: JsonPropertyName("stored")] bool Stored,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("overrides")] VoiceOverridesRequest Overrides,
    [property: JsonPropertyName("presetValues")] VoiceValuesResponse PresetValues,
    [property: JsonPropertyName("effectiveValues")] VoiceValuesResponse EffectiveValues,
    [property: JsonPropertyName("fields")] IReadOnlyList<VoiceFieldResponse> Fields,
    [property: JsonPropertyName("overriddenFields")] IReadOnlyList<string> OverriddenFields)
{
    public static VoiceProfileResponse From(VoiceProfileView view) =>
        new(
            view.Id,
            view.ProjectId,
            view.PresetId,
            view.Stored,
            view.Revision,
            view.CreatedAt,
            view.UpdatedAt,
            VoiceOverridesRequest.From(view.Overrides),
            VoiceValuesResponse.From(view.Resolved.PresetValues),
            VoiceValuesResponse.From(view.Resolved.Effective),
            [.. view.Resolved.Fields.Select(VoiceFieldResponse.From)],
            view.Overrides.OverriddenFields);
}

/// <summary>
/// The wire shape of the explicit project overrides (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// Every field is a three-state wire value, and that is the whole contract. <c>emoji: false</c> is
/// an owner turning emoji off over a preset that had it on; omitting <c>emoji</c> leaves the preset
/// alone. A two-state boolean could not tell those apart, and the requirement is about exactly that
/// distinction.
/// </remarks>
public sealed record VoiceOverridesRequest
{
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("tone")]
    public string? Tone { get; init; }

    [JsonPropertyName("emoji")]
    public bool? Emoji { get; init; }

    [JsonPropertyName("titleMaxChars")]
    public int? TitleMaxChars { get; init; }

    [JsonPropertyName("technicalDetail")]
    public string? TechnicalDetail { get; init; }

    [JsonPropertyName("marketing")]
    public string? Marketing { get; init; }

    [JsonPropertyName("firstPerson")]
    public bool? FirstPerson { get; init; }

    [JsonPropertyName("forbiddenTerms")]
    public IReadOnlyList<string>? ForbiddenTerms { get; init; }

    public VoiceOverrides ToOverrides() =>
        new(Language, Tone, Emoji, TitleMaxChars, TechnicalDetail, Marketing, FirstPerson, ForbiddenTerms);

    public static VoiceOverridesRequest From(VoiceOverrides overrides) =>
        new()
        {
            Language = overrides.Language,
            Tone = overrides.Tone,
            Emoji = overrides.Emoji,
            TitleMaxChars = overrides.TitleMaxChars,
            TechnicalDetail = overrides.TechnicalDetail,
            Marketing = overrides.Marketing,
            FirstPerson = overrides.FirstPerson,
            ForbiddenTerms = overrides.ForbiddenTerms,
        };
}

/// <summary>The wire shape of a voice profile save (VOICE-PROFILES-001).</summary>
public sealed record VoiceProfileWriteRequest
{
    [JsonPropertyName("presetId")]
    public string? PresetId { get; init; }

    [JsonPropertyName("overrides")]
    public VoiceOverridesRequest? Overrides { get; init; }
}

/// <summary>The wire shape of the stateless text check (VOICE-PROFILES-002).</summary>
public sealed record VoiceCheckRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }
}

/// <summary>The wire shape of one rule violation, with the span it points at (VOICE-PROFILES-002).</summary>
public sealed record VoiceFindingResponse(
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("excerpt")] string Excerpt,
    [property: JsonPropertyName("term")] string Term,
    [property: JsonPropertyName("normalizedTerm")] string NormalizedTerm,
    [property: JsonPropertyName("message")] string Message)
{
    public static VoiceFindingResponse From(VoiceFinding finding) =>
        new(
            finding.Rule,
            finding.Field,
            finding.Start,
            finding.Length,
            finding.Excerpt,
            finding.Term,
            finding.NormalizedTerm,
            finding.Message);
}

/// <summary>The wire shape of a voice snapshot, named so a finding is never reported without the voice it was measured against (VOICE-PROFILES-001).</summary>
public sealed record VoiceSnapshotResponse(
    [property: JsonPropertyName("presetId")] string PresetId,
    [property: JsonPropertyName("profileRevision")] long ProfileRevision,
    [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("values")] VoiceValuesResponse Values)
{
    public static VoiceSnapshotResponse From(VoiceSnapshot snapshot) =>
        new(
            snapshot.PresetId,
            snapshot.ProfileRevision,
            snapshot.CapturedAt,
            snapshot.Fingerprint,
            VoiceValuesResponse.From(snapshot.Values));
}

/// <summary>The wire shape of a check result (VOICE-PROFILES-002).</summary>
public sealed record VoiceCheckResponse(
    [property: JsonPropertyName("snapshot")] VoiceSnapshotResponse Snapshot,
    [property: JsonPropertyName("clean")] bool Clean,
    [property: JsonPropertyName("findings")] IReadOnlyList<VoiceFindingResponse> Findings)
{
    public static VoiceCheckResponse From(VoiceCheckResult result) =>
        new(
            VoiceSnapshotResponse.From(result.Snapshot),
            result.IsClean,
            [.. result.Findings.Select(VoiceFindingResponse.From)]);
}

/// <summary>The wire shape of a draft, with its own copied snapshot and the findings for its text (VOICE-PROFILES-001/002).</summary>
public sealed record VoiceDraftResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("projectId")] Guid ProjectId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("snapshot")] VoiceSnapshotResponse Snapshot,
    [property: JsonPropertyName("clean")] bool Clean,
    [property: JsonPropertyName("findings")] IReadOnlyList<VoiceFindingResponse> Findings)
{
    public static VoiceDraftResponse From(VoiceDraftView view) =>
        new(
            view.Draft.Id,
            view.Draft.ProjectId,
            view.Draft.Title,
            view.Draft.Body,
            view.Draft.Status,
            view.Draft.Revision,
            view.Draft.CreatedAt,
            view.Draft.UpdatedAt,
            VoiceSnapshotResponse.From(view.Snapshot),
            view.Findings.Count == 0,
            [.. view.Findings.Select(VoiceFindingResponse.From)]);
}

/// <summary>The wire shape of a draft in a list (VOICE-PROFILES-001).</summary>
public sealed record VoiceDraftSummaryResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("projectId")] Guid ProjectId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("snapshotFingerprint")] string SnapshotFingerprint,
    [property: JsonPropertyName("findingCount")] int FindingCount)
{
    public static VoiceDraftSummaryResponse From(VoiceDraftSummary summary) =>
        new(
            summary.Id,
            summary.ProjectId,
            summary.Status,
            summary.Revision,
            summary.CreatedAt,
            summary.UpdatedAt,
            summary.SnapshotFingerprint,
            summary.FindingCount);
}

/// <summary>The wire shape of a draft page, with the cursor that continues it (VOICE-PROFILES-001).</summary>
public sealed record VoiceDraftPageResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<VoiceDraftSummaryResponse> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor,
    [property: JsonPropertyName("pageSize")] int PageSize)
{
    public static VoiceDraftPageResponse From(VoiceDraftPage page) =>
        new(
            [.. page.Items.Select(VoiceDraftSummaryResponse.From)],
            page.NextCursor,
            page.PageSize);
}

/// <summary>The wire shape of a draft create (VOICE-PROFILES-001).</summary>
public sealed record VoiceDraftCreateRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }
}

/// <summary>The wire shape of a draft text edit (VOICE-PROFILES-002).</summary>
public sealed record VoiceDraftEditRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }
}

/// <summary>The preset catalogue as <c>GET /api/voice-presets</c> returns it (VOICE-PROFILES-001).</summary>
public sealed record VoicePresetListResponse(
    [property: JsonPropertyName("presets")] IReadOnlyList<VoicePresetResponse> Presets,
    [property: JsonPropertyName("defaults")] VoiceValuesResponse Defaults,
    [property: JsonPropertyName("languages")] IReadOnlyList<string> Languages,
    [property: JsonPropertyName("tones")] IReadOnlyList<string> Tones,
    [property: JsonPropertyName("technicalDetails")] IReadOnlyList<string> TechnicalDetails,
    [property: JsonPropertyName("marketingLevels")] IReadOnlyList<string> MarketingLevels,
    [property: JsonPropertyName("rules")] IReadOnlyList<string> Rules,
    [property: JsonPropertyName("limits")] VoiceLimitsResponse Limits);

/// <summary>The bounds a settings surface needs in order to validate before it posts (VOICE-PROFILES-001).</summary>
public sealed record VoiceLimitsResponse(
    [property: JsonPropertyName("minTitleMaxChars")] int MinTitleMaxChars,
    [property: JsonPropertyName("maxTitleMaxChars")] int MaxTitleMaxChars,
    [property: JsonPropertyName("maxForbiddenTerms")] int MaxForbiddenTerms,
    [property: JsonPropertyName("forbiddenTermMaxLength")] int ForbiddenTermMaxLength,
    [property: JsonPropertyName("maxTitleTextLength")] int MaxTitleTextLength,
    [property: JsonPropertyName("maxBodyTextLength")] int MaxBodyTextLength,
    [property: JsonPropertyName("defaultPageSize")] int DefaultPageSize,
    [property: JsonPropertyName("maxPageSize")] int MaxPageSize)
{
    public static VoiceLimitsResponse Instance { get; } = new(
        VoiceLimits.MinTitleMaxChars,
        VoiceLimits.MaxTitleMaxChars,
        VoiceLimits.MaxForbiddenTerms,
        VoiceLimits.ForbiddenTermMaxLength,
        VoiceLimits.MaxTitleTextLength,
        VoiceLimits.MaxBodyTextLength,
        Application.Common.ListQueryPolicy.DefaultPageSize,
        Application.Common.ListQueryPolicy.MaxPageSize);
}
