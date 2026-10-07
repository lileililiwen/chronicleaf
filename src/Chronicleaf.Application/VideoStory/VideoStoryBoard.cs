using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Chronicleaf.Application.VideoStory;

/// <summary>
/// The storyboard document a video-story revision body holds (VIDEO-STORY-SCRIPTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure: no IO, no clock, no provider, no network.</b> The inputs are the target runtime, the
/// owner's shot list and the summary's known limitations. The outputs are the canonical JSON the
/// revision stores, the narration Markdown and the SRT captions the export serves. Every member
/// that touches owner prose treats it as opaque text to carry, never as markup to interpret.
/// </para>
/// <para>
/// <b>A storyboard is intervals, not intentions.</b> Validation proves the shots cover the runtime
/// exactly - contiguous from zero, each at least 500ms, at most 30 shots, no gaps, no overlaps -
/// because a script whose timings do not add up is not a plan for the runtime it names. What the
/// frozen evidence holds is checked by the service, which can read the bundle; this type checks
/// only what the document says about itself.
/// </para>
/// <para>
/// <b>Nothing here is footage.</b> The document names pictures and evidence, and the export serves
/// exactly those pictures beside the scripts. There is no video track, no audio track, no render
/// and no post: a storyboard that claimed any of those would be claiming work nobody did.
/// </para>
/// </remarks>
public static class VideoStoryBoard
{
    /// <summary>
    /// The template version recorded as the revision's prompt version, so a stored script names
    /// the schema that validated it.
    /// </summary>
    public const string TemplateVersion = "video-story/1";

    /// <summary>The runtimes this product scripts, in seconds.</summary>
    public static readonly IReadOnlyList<int> AllowedDurations = [15, 30, 60];

    /// <summary>The shortest shot this product stores, in milliseconds.</summary>
    public const int MinShotMs = 500;

    /// <summary>How many shots one script may hold. A bound, not a platform rule.</summary>
    public const int MaxShots = 30;

    /// <summary>How many characters of caption one shot may carry.</summary>
    public const int MaxCaptionChars = 1000;

    /// <summary>How many characters of narration one shot may carry.</summary>
    public const int MaxNarrationChars = 2000;

    /// <summary>The handoffs one shot may name.</summary>
    public static readonly IReadOnlyList<string> AllowedTransitions = ["cut", "fade"];

    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>One validated shot, as the stored document carries it.</summary>
    public sealed record Shot(
        Guid ScreenshotMediaId,
        string Caption,
        string Narration,
        string Transition,
        long StartMs,
        long EndMs,
        IReadOnlyList<Guid> SourceEvidenceIds);

    /// <summary>One validated storyboard: the runtime, its shots and the summary's limitations.</summary>
    public sealed record Document(
        int DurationSeconds,
        IReadOnlyList<Shot> Shots,
        IReadOnlyList<string> Limitations)
    {
        /// <summary>The covered runtime in milliseconds: exactly the duration in seconds times 1000.</summary>
        public long TotalMs => DurationSeconds * 1000L;

        /// <summary>The depicted pictures, in first-appearance order.</summary>
        public IReadOnlyList<Guid> ImageOrder =>
            [.. Shots.Select(shot => shot.ScreenshotMediaId).Distinct()];
    }

    /// <summary>
    /// Validates a shot list against the runtime and returns the document the revision stores.
    /// </summary>
    /// <exception cref="InvalidStorySchemaException">The list does not satisfy the schema.</exception>
    public static Document Validate(
        int durationSeconds,
        IReadOnlyList<VideoStoryShotRequest> shots,
        IReadOnlyList<string> limitations)
    {
        ArgumentNullException.ThrowIfNull(shots);
        ArgumentNullException.ThrowIfNull(limitations);

        if (!AllowedDurations.Contains(durationSeconds))
        {
            throw new InvalidStorySchemaException(
                "durationSeconds",
                "A video story covers 15, 30 or 60 seconds exactly. Name one of those runtimes.");
        }

        if (shots.Count == 0)
        {
            throw new InvalidStorySchemaException(
                "shots",
                "A video story holds at least one shot. Select material or omit the script.");
        }

        if (shots.Count > MaxShots)
        {
            throw new InvalidStorySchemaException(
                "shots",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A video story holds at most {MaxShots} shots."));
        }

        var accepted = new List<Shot>(shots.Count);

        for (var index = 0; index < shots.Count; index++)
        {
            accepted.Add(ValidateShot(shots[index], index));
        }

        ValidateCoverage(durationSeconds, accepted);

        return new Document(durationSeconds, accepted, [.. limitations]);
    }

    private static Shot ValidateShot(VideoStoryShotRequest shot, int index)
    {
        ArgumentNullException.ThrowIfNull(shot);

        string Field(string member) =>
            string.Create(CultureInfo.InvariantCulture, $"shots[{index}].{member}");

        if (shot.ScreenshotMediaId == Guid.Empty)
        {
            throw new InvalidStorySchemaException(
                Field("screenshotMediaId"),
                "A shot depicts a selected screenshot, so name its media id.");
        }

        if (string.IsNullOrWhiteSpace(shot.Caption) || shot.Caption.Length > MaxCaptionChars)
        {
            throw new InvalidStorySchemaException(
                Field("caption"),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A shot carries a caption of 1 to {MaxCaptionChars} characters."));
        }

        if (string.IsNullOrWhiteSpace(shot.Narration) || shot.Narration.Length > MaxNarrationChars)
        {
            throw new InvalidStorySchemaException(
                Field("narration"),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A shot carries one spoken line of 1 to {MaxNarrationChars} characters."));
        }

        if (shot.Narration.Contains('\r') || shot.Narration.Contains('\n'))
        {
            throw new InvalidStorySchemaException(
                Field("narration"),
                "A spoken line is one line: captions are timed per shot, not paragraphed.");
        }

        if (shot.Narration.Contains("-->", StringComparison.Ordinal))
        {
            throw new InvalidStorySchemaException(
                Field("narration"),
                "A spoken line must not carry a caption timestamp of its own.");
        }

        if (!AllowedTransitions.Contains(shot.Transition, StringComparer.Ordinal))
        {
            throw new InvalidStorySchemaException(
                Field("transition"),
                "A shot hands off with cut or fade. Name one of those.");
        }

        if (shot.StartMs < 0 || shot.EndMs < 0)
        {
            throw new InvalidStorySchemaException(
                Field("startMs"),
                "A shot's timings are milliseconds from the start, never negative.");
        }

        if (shot.EndMs - shot.StartMs < MinShotMs)
        {
            throw new InvalidStorySchemaException(
                Field("endMs"),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A shot holds the screen for at least {MinShotMs} milliseconds."));
        }

        if (shot.SourceEvidenceIds is null || shot.SourceEvidenceIds.Count == 0)
        {
            throw new InvalidStorySchemaException(
                Field("sourceEvidenceIds"),
                "A shot cites the evidence it shows: name at least one evidence id.");
        }

        if (shot.SourceEvidenceIds.Any(id => id == Guid.Empty)
            || shot.SourceEvidenceIds.Distinct().Count() != shot.SourceEvidenceIds.Count)
        {
            throw new InvalidStorySchemaException(
                Field("sourceEvidenceIds"),
                "A shot cites each evidence id once, and every citation names an id.");
        }

        return new Shot(
            shot.ScreenshotMediaId,
            shot.Caption,
            shot.Narration,
            shot.Transition,
            shot.StartMs,
            shot.EndMs,
            [.. shot.SourceEvidenceIds]);
    }

    private static void ValidateCoverage(int durationSeconds, IReadOnlyList<Shot> shots)
    {
        var totalMs = durationSeconds * 1000L;

        if (shots[0].StartMs != 0)
        {
            throw new InvalidStorySchemaException(
                "shots[0].startMs",
                "A storyboard starts at zero: the first shot opens the runtime.");
        }

        for (var index = 1; index < shots.Count; index++)
        {
            if (shots[index].StartMs != shots[index - 1].EndMs)
            {
                throw new InvalidStorySchemaException(
                    string.Create(CultureInfo.InvariantCulture, $"shots[{index}].startMs"),
                    "Shots play back to back with no gaps and no overlaps: "
                    + "each shot starts where the previous one ends.");
            }
        }

        if (shots[^1].EndMs != totalMs)
        {
            throw new InvalidStorySchemaException(
                string.Create(CultureInfo.InvariantCulture, $"shots[{shots.Count - 1}].endMs"),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The last shot ends the runtime: a {durationSeconds}-second story covers "
                    + $"exactly {totalMs} milliseconds."));
        }
    }

    /// <summary>
    /// The canonical JSON a revision stores. Fixed property order and no indentation, so the
    /// same script always hashes the same.
    /// </summary>
    public static string ToCanonicalJson(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return JsonSerializer.Serialize(
            new
            {
                format = "video-story",
                version = TemplateVersion,
                durationSeconds = document.DurationSeconds,
                shots = document.Shots.Select(shot => new
                {
                    startMs = shot.StartMs,
                    endMs = shot.EndMs,
                    screenshotMediaId = shot.ScreenshotMediaId.ToString("D"),
                    caption = shot.Caption,
                    narration = shot.Narration,
                    transition = shot.Transition,
                    sourceEvidenceIds = shot.SourceEvidenceIds.Select(id => id.ToString("D")).ToArray(),
                }).ToArray(),
                limitations = document.Limitations.ToArray(),
            },
            CanonicalJson);
    }

    /// <summary>
    /// Reads a stored storyboard back. Anything this build did not write is unreadable, not a story.
    /// </summary>
    /// <returns>The document, or <c>null</c> when the bytes are not one.</returns>
    public static Document? FromCanonicalJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (parsed)
        {
            var root = parsed.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format)
                || format.GetString() != "video-story"
                || !root.TryGetProperty("durationSeconds", out var duration)
                || duration.ValueKind != JsonValueKind.Number
                || !duration.TryGetInt32(out var durationSeconds)
                || !AllowedDurations.Contains(durationSeconds))
            {
                return null;
            }

            if (!root.TryGetProperty("shots", out var shots)
                || shots.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var accepted = new List<Shot>();

            foreach (var entry in shots.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("startMs", out var start)
                    || !start.TryGetInt64(out var startMs)
                    || !entry.TryGetProperty("endMs", out var end)
                    || !end.TryGetInt64(out var endMs)
                    || !entry.TryGetProperty("screenshotMediaId", out var media)
                    || !Guid.TryParse(media.GetString(), out var mediaId)
                    || mediaId == Guid.Empty
                    || !entry.TryGetProperty("caption", out var caption)
                    || caption.GetString() is not { } captionText
                    || !entry.TryGetProperty("narration", out var narration)
                    || narration.GetString() is not { } narrationText
                    || !entry.TryGetProperty("transition", out var transition)
                    || transition.GetString() is not { } transitionText
                    || !entry.TryGetProperty("sourceEvidenceIds", out var evidence)
                    || evidence.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var evidenceIds = new List<Guid>();

                foreach (var id in evidence.EnumerateArray())
                {
                    if (!Guid.TryParse(id.GetString(), out var evidenceId) || evidenceId == Guid.Empty)
                    {
                        return null;
                    }

                    evidenceIds.Add(evidenceId);
                }

                accepted.Add(new Shot(
                    mediaId, captionText, narrationText, transitionText, startMs, endMs, evidenceIds));
            }

            var limitations = new List<string>();

            if (root.TryGetProperty("limitations", out var stored)
                && stored.ValueKind == JsonValueKind.Array)
            {
                foreach (var limitation in stored.EnumerateArray())
                {
                    if (limitation.GetString() is { } text)
                    {
                        limitations.Add(text);
                    }
                }
            }

            try
            {
                // Re-validated on the way back in, so a row this build could not have written -
                // gaps, overlaps, a missing runtime - is unreadable rather than half a script.
                return Validate(
                    durationSeconds,
                    [.. accepted.Select(shot => new VideoStoryShotRequest(
                        shot.ScreenshotMediaId,
                        shot.Caption,
                        shot.Narration,
                        shot.Transition,
                        shot.StartMs,
                        shot.EndMs,
                        shot.SourceEvidenceIds))],
                    limitations);
            }
            catch (InvalidStorySchemaException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Renders the narration Markdown: one timed section per shot, the depicted picture named,
    /// the cited evidence named, the known limitations carried.
    /// </summary>
    public static string RenderNarrationMarkdown(string milestoneTitle, Document document)
    {
        ArgumentNullException.ThrowIfNull(milestoneTitle);
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder();
        builder.Append("# ").Append(milestoneTitle).Append('\n');
        builder.Append('\n');
        builder.Append(document.DurationSeconds.ToString(CultureInfo.InvariantCulture));
        builder.Append("-second storyboard: ").Append(document.Shots.Count.ToString(CultureInfo.InvariantCulture));
        builder.Append(document.Shots.Count == 1 ? " shot" : " shots");
        builder.Append(". A reviewed script a human acts on: no footage is claimed and none is attached.\n");

        for (var index = 0; index < document.Shots.Count; index++)
        {
            var shot = document.Shots[index];
            builder.Append("\n## Shot ").Append((index + 1).ToString(CultureInfo.InvariantCulture));
            builder.Append(" (").Append(FormatTimestamp(shot.StartMs));
            builder.Append(" - ").Append(FormatTimestamp(shot.EndMs));
            builder.Append(", ").Append(shot.Transition).Append(")\n");
            builder.Append('\n');
            builder.Append("Picture: ").Append(shot.ScreenshotMediaId.ToString("D")).Append('\n');
            builder.Append('\n');
            builder.Append(shot.Caption).Append('\n');
            builder.Append('\n');
            builder.Append("> ").Append(shot.Narration).Append('\n');
            builder.Append('\n');
            builder.Append("Evidence: ").Append(
                string.Join(", ", shot.SourceEvidenceIds.Select(id => id.ToString("D")))).Append('\n');
        }

        if (document.Limitations.Count > 0)
        {
            builder.Append("\n## Known limitations\n");
            foreach (var limitation in document.Limitations)
            {
                builder.Append("\n- ").Append(limitation).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders the SRT captions: one cue per shot carrying its spoken line.
    /// </summary>
    public static string RenderSrt(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder();

        for (var index = 0; index < document.Shots.Count; index++)
        {
            var shot = document.Shots[index];

            if (index > 0)
            {
                builder.Append('\n');
            }

            builder.Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(FormatTimestamp(shot.StartMs)).Append(" --> ").Append(FormatTimestamp(shot.EndMs)).Append('\n');
            builder.Append(shot.Narration).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Formats milliseconds as an SRT timestamp.</summary>
    public static string FormatTimestamp(long totalMs)
    {
        if (totalMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalMs), totalMs, "A cue timestamp is never negative.");
        }

        var hours = totalMs / 3_600_000;
        var minutes = (totalMs % 3_600_000) / 60_000;
        var seconds = (totalMs % 60_000) / 1000;
        var millis = totalMs % 1000;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:00}:{minutes:00}:{seconds:00},{millis:000}");
    }
}
