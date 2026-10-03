using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Voice;

/// <summary>
/// The canonical serialization of a <see cref="VoiceSnapshot"/> (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// <para>
/// The snapshot is a value, and this is what makes it one. It is written into the draft row as
/// self-contained JSON - the resolved values, the preset, the profile revision and the instant - and
/// it is read back out of those bytes. There is no id in there that anything resolves, so a profile
/// edit has no path to reach a snapshot that was already taken; the draft simply keeps what it was
/// given.
/// </para>
/// <para>
/// The bytes are canonical: properties are written in a fixed order with no whitespace and no
/// dictionary anywhere, so the same snapshot always serializes to the same string in any process on
/// any runtime. That is what makes "the stored snapshot did not change" a byte comparison rather
/// than an assertion about object equality, and it is why the requirement's boundary scenario can
/// be proven against the database column itself.
/// </para>
/// </remarks>
public static class VoiceSnapshotCodec
{
    private static readonly JsonWriterOptions Writer = new() { Indented = false, SkipValidation = false };

    /// <summary>Serializes a snapshot to its canonical JSON text.</summary>
    public static string Serialize(VoiceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var values = snapshot.Values;
        var buffer = new ArrayBufferWriter<byte>(512);
        var bufferWriter = new Utf8JsonWriter(buffer, Writer);

        bufferWriter.WriteStartObject();
        bufferWriter.WriteString("presetId", snapshot.PresetId);
        bufferWriter.WriteNumber("profileRevision", snapshot.ProfileRevision);
        bufferWriter.WriteString("capturedAt", snapshot.CapturedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        bufferWriter.WriteString("language", values.Language);
        bufferWriter.WriteString("tone", values.Tone);
        bufferWriter.WriteBoolean("emoji", values.Emoji);
        bufferWriter.WriteNumber("titleMaxChars", values.TitleMaxChars);
        bufferWriter.WriteString("technicalDetail", values.TechnicalDetail);
        bufferWriter.WriteString("marketing", values.Marketing);
        bufferWriter.WriteBoolean("firstPerson", values.FirstPerson);
        bufferWriter.WriteStartArray("forbiddenTerms");

        foreach (var term in values.ForbiddenTerms)
        {
            bufferWriter.WriteStringValue(term);
        }

        bufferWriter.WriteEndArray();
        bufferWriter.WriteString("fingerprint", snapshot.Fingerprint);
        bufferWriter.WriteEndObject();
        bufferWriter.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Reads a snapshot back out of its stored bytes, or answers <c>null</c> when the text is not a
    /// snapshot. A row this codec cannot read is a row this build will not pretend to understand.
    /// </summary>
    public static VoiceSnapshot? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("presetId", out var presetId) ||
                !root.TryGetProperty("profileRevision", out var profileRevision) ||
                !root.TryGetProperty("capturedAt", out var capturedAt) ||
                !root.TryGetProperty("language", out var language) ||
                !root.TryGetProperty("tone", out var tone) ||
                !root.TryGetProperty("emoji", out var emoji) ||
                !root.TryGetProperty("titleMaxChars", out var titleMaxChars) ||
                !root.TryGetProperty("technicalDetail", out var technicalDetail) ||
                !root.TryGetProperty("marketing", out var marketing) ||
                !root.TryGetProperty("firstPerson", out var firstPerson) ||
                !root.TryGetProperty("forbiddenTerms", out var forbiddenTerms) ||
                forbiddenTerms.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var terms = new List<string>(forbiddenTerms.GetArrayLength());
            foreach (var term in forbiddenTerms.EnumerateArray())
            {
                if (term.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                terms.Add(term.GetString() ?? string.Empty);
            }

            if (!DateTimeOffset.TryParse(
                    capturedAt.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var captured) ||
                !voiceValuesWellFormed(
                    language.GetString(),
                    tone.GetString(),
                    titleMaxChars.GetInt32(),
                    technicalDetail.GetString(),
                    marketing.GetString(),
                    terms))
            {
                return null;
            }

            var snapshot = new VoiceSnapshot(
                presetId.GetString() ?? string.Empty,
                profileRevision.GetInt64(),
                captured,
                new VoiceValues(
                    language.GetString()!,
                    tone.GetString()!,
                    emoji.GetBoolean(),
                    titleMaxChars.GetInt32(),
                    technicalDetail.GetString()!,
                    marketing.GetString()!,
                    firstPerson.GetBoolean(),
                    terms));

            // The stored fingerprint is recomputed rather than trusted: a snapshot that does not
            // describe itself consistently is not a snapshot this build will hand to a checker.
            return root.TryGetProperty("fingerprint", out var stored) &&
                   string.Equals(stored.GetString(), snapshot.Fingerprint, StringComparison.Ordinal)
                ? snapshot
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a decoded snapshot's values are inside their vocabularies. The two booleans are
    /// deliberately not constrained: either value is a legitimate thing for an owner to have
    /// chosen, and a snapshot records a choice rather than proposing one.
    /// </summary>
    private static bool voiceValuesWellFormed(
        string? language,
        string? tone,
        int titleMaxChars,
        string? technicalDetail,
        string? marketing,
        List<string> terms) =>
        VoiceVocabularies.IsLanguage(language) &&
        VoiceVocabularies.IsTone(tone) &&
        VoiceLimits.IsTitleMaxChars(titleMaxChars) &&
        VoiceVocabularies.IsTechnicalDetail(technicalDetail) &&
        VoiceVocabularies.IsMarketing(marketing) &&
        terms.Count <= VoiceLimits.MaxForbiddenTerms;
}

/// <summary>
/// The canonical serialization of a <see cref="VoiceOverrides"/> record (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// The shape matters more than the format. A field the owner never set is written as JSON
/// <c>null</c>, which reads back as "no override"; a field the owner set to <c>false</c> is written
/// as <c>false</c>, which reads back as an override. Those are different bytes on purpose, and
/// collapsing them would make the requirement's success scenario unrepresentable at the storage
/// layer even though the domain type can express it.
/// </remarks>
public static class VoiceOverridesCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes overrides to JSON, absent fields written as <c>null</c>.</summary>
    public static string Serialize(VoiceOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        return JsonSerializer.Serialize(new OverridePayload(
            overrides.Language,
            overrides.Tone,
            overrides.Emoji,
            overrides.TitleMaxChars,
            overrides.TechnicalDetail,
            overrides.Marketing,
            overrides.FirstPerson,
            overrides.ForbiddenTerms), Options);
    }

    /// <summary>
    /// Reads overrides back, or answers <c>null</c> when the text is not an override record. A
    /// malformed record is never read as "no overrides", because that would silently discard what
    /// the owner stored.
    /// </summary>
    public static VoiceOverrides? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OverridePayload>(json, Options) is { } payload
                ? new VoiceOverrides(
                    payload.Language,
                    payload.Tone,
                    payload.Emoji,
                    payload.TitleMaxChars,
                    payload.TechnicalDetail,
                    payload.Marketing,
                    payload.FirstPerson,
                    payload.ForbiddenTerms)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record OverridePayload(
        string? Language,
        string? Tone,
        bool? Emoji,
        int? TitleMaxChars,
        string? TechnicalDetail,
        string? Marketing,
        bool? FirstPerson,
        IReadOnlyList<string>? ForbiddenTerms);
}
