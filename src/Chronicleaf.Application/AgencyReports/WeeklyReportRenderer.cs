using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Core;

namespace Chronicleaf.Application.AgencyReports;

/// <summary>
/// The deterministic weekly-report template and its calendar window
/// (AGENCY-WEEKLY-REPORTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure: no IO, no clock, no provider.</b> The inputs are confirmed milestones already
/// filtered to the window, the evidence heads already read, and the owner's own explicit
/// notes. The output names only what those inputs state: a section with no source reads
/// <see cref="UnknownLabel"/> rather than an invented line, and an empty week says so in
/// words rather than claiming progress.
/// </para>
/// <para>
/// <b>The window is seven local calendar dates, whatever the offset does mid-week.</b> Both
/// bounds are converted separately, so a daylight-saving transition inside the week moves
/// the UTC instants without moving the dates: the report still covers exactly the days the
/// owner asked for. An unsupported zone is refused before anything is read.
/// </para>
/// </remarks>
public static class WeeklyReportRenderer
{
    /// <summary>What a section without source reads. Digit-free on purpose, like the variant templates.</summary>
    public const string UnknownLabel = "Unknown — no source evidence recorded.";

    /// <summary>The sentence an empty week carries instead of a success claim.</summary>
    public const string EmptyWeekLabel = "No recorded completed milestones in this window.";

    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Resolves the seven local calendar dates starting at <paramref name="weekStart"/> into
    /// UTC bounds. Refused before anything is read when the date or the zone is malformed.
    /// </summary>
    /// <exception cref="ValidationFailedException">The date is not YYYY-MM-DD or the zone is unsupported.</exception>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) ResolveWindow(string weekStart, string timeZone)
    {
        if (!DateTime.TryParseExact(
                weekStart,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var firstDay)
            || firstDay.Year < 2000
            || firstDay.Year > 2100)
        {
            throw new ValidationFailedException(
                "weekStart",
                "The week starts on a local calendar date as YYYY-MM-DD.");
        }

        TimeZoneInfo zone;

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new ValidationFailedException(
                "timeZone",
                "The time zone must be an IANA time zone id, such as America/New_York.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new ValidationFailedException(
                "timeZone",
                "The time zone must be an IANA time zone id, such as America/New_York.");
        }

        // Each bound converts on its own, so a transition between them moves the instants without
        // moving the dates. A midnight that never happened (a spring-forward at 00:00, as a few
        // zones observe) advances to the first valid minute rather than refusing the whole week.
        return (ToUtc(firstDay, zone), ToUtc(firstDay.AddDays(7), zone));
    }

    /// <summary>
    /// The attribution a report pins: the client, its membership generation and the exact
    /// project set, hashed so a regrouping is recognizably stale.
    /// </summary>
    public static string AttributionHash(Guid clientId, long contextRevision, IReadOnlyList<Guid> projectIds)
    {
        ArgumentNullException.ThrowIfNull(projectIds);

        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{clientId:D}|{contextRevision.ToString(CultureInfo.InvariantCulture)}"
            + $"|{string.Join(",", projectIds.OrderBy(id => id).Select(id => id.ToString("D")))}");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>
    /// The canonical source bindings one report row stores: every in-window confirmed
    /// milestone with the evidence head it was rendered from, or an explicit unknown when the
    /// milestone has no evidence yet.
    /// </summary>
    public static string SourceRefsJson(IReadOnlyList<WeeklyReportSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        return JsonSerializer.Serialize(
            sources
                .OrderBy(source => source.ProjectId)
                .ThenBy(source => source.MilestoneId)
                .Select(source => new
                {
                    projectId = source.ProjectId.ToString("D"),
                    milestoneId = source.MilestoneId.ToString("D"),
                    bundleId = source.BundleId?.ToString("D"),
                    bundleRevision = source.BundleRevision,
                })
                .ToArray(),
            CanonicalJson);
    }

    /// <summary>The canonical generation inputs one idempotency key is bound to.</summary>
    public static string InputsHash(
        Guid clientId,
        string weekStart,
        string timeZone,
        IReadOnlyList<Guid> projectIds,
        string inProgressNotes,
        string nextWeekNotes)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{clientId:D}|{weekStart}|{timeZone}"
            + $"|{string.Join(",", projectIds.OrderBy(id => id).Select(id => id.ToString("D")))}"
            + $"|{inProgressNotes}|{nextWeekNotes}");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Renders the report body. Every section the design names is always present.</summary>
    public static string Render(WeeklyReportContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var body = new StringBuilder();

        body.Append(CultureInfo.InvariantCulture, $"# Weekly progress — {content.ClientName}\n");
        body.Append(CultureInfo.InvariantCulture, $"Week of {content.WeekStart} ({content.TimeZone})\n");
        body.Append(CultureInfo.InvariantCulture,
            $"Window: {content.WindowStartUtc:O} to {content.WindowEndUtc:O} UTC.\n");

        body.Append(Completed(content));
        body.Append(Resolved());
        body.Append(Screenshots(content));
        body.Append(InProgress(content));
        body.Append(NextWeek(content));
        body.Append(Limitations(content));
        body.Append(Sources(content));

        return body.ToString();
    }

    private static StringBuilder Completed(WeeklyReportContent content)
    {
        var section = new StringBuilder();
        section.Append("\n## Completed\n");

        var any = false;

        foreach (var project in content.Projects)
        {
            section.Append(CultureInfo.InvariantCulture, $"\n### {project.ProjectName}\n");

            if (project.Milestones.Count == 0)
            {
                section.Append("None recorded.\n");
                continue;
            }

            any = true;

            foreach (var milestone in project.Milestones)
            {
                section.Append(CultureInfo.InvariantCulture, $"- {milestone.Title} ({milestone.OccurredAt:O}");

                if (milestone.Version is not null)
                {
                    section.Append(CultureInfo.InvariantCulture, $", {milestone.Version}");
                }

                section.Append(")\n");
            }
        }

        if (!any)
        {
            section.Append(EmptyWeekLabel).Append('\n');
        }

        return section;
    }

    private static StringBuilder Resolved() =>
        // Milestones carry no problems-solved field, and the evidence kinds carry none either:
        // a resolution written here would be invented. The owner's words stay in their notes,
        // quoted nowhere as a claim this system resolved.
        new StringBuilder().Append("\n## Resolved\n").Append(UnknownLabel).Append('\n');

    private static StringBuilder Screenshots(WeeklyReportContent content)
    {
        var section = new StringBuilder();
        section.Append("\n## Screenshots\n");

        var any = false;

        foreach (var project in content.Projects)
        {
            if (project.Screenshots.Count == 0)
            {
                continue;
            }

            any = true;
            section.Append(CultureInfo.InvariantCulture, $"\n### {project.ProjectName}\n");

            foreach (var screenshot in project.Screenshots.OrderBy(shot => shot.MediaId))
            {
                section.Append(CultureInfo.InvariantCulture,
                    $"- {screenshot.Caption} (media {screenshot.MediaId:D})\n");
            }
        }

        if (!any)
        {
            section.Append(UnknownLabel).Append('\n');
        }

        return section;
    }

    private static StringBuilder InProgress(WeeklyReportContent content)
    {
        var section = new StringBuilder();
        section.Append("\n## In progress\n");

        // Only the owner's explicit notes: nothing here is inferred, so incomplete work the
        // owner did not label stays unlabeled rather than guessed from activity.
        section.Append(string.IsNullOrEmpty(content.InProgressNotes) ? UnknownLabel : content.InProgressNotes);
        return section.Append('\n');
    }

    private static StringBuilder NextWeek(WeeklyReportContent content)
    {
        var section = new StringBuilder();
        section.Append("\n## Next week\n");

        var lines = new List<string>();

        foreach (var project in content.Projects)
        {
            foreach (var milestone in project.Milestones)
            {
                lines.AddRange(milestone.NextSteps.Select(step =>
                    string.Create(CultureInfo.InvariantCulture, $"- {step} (from {milestone.Title})\n")));
            }
        }

        if (!string.IsNullOrEmpty(content.NextWeekNotes))
        {
            lines.Add(content.NextWeekNotes + "\n");
        }

        if (lines.Count == 0)
        {
            section.Append(UnknownLabel).Append('\n');
            return section;
        }

        foreach (var line in lines)
        {
            section.Append(line);
        }

        return section;
    }

    private static StringBuilder Limitations(WeeklyReportContent content)
    {
        var section = new StringBuilder();
        section.Append("\n## Known limitations\n");

        var any = false;

        foreach (var project in content.Projects)
        {
            foreach (var milestone in project.Milestones)
            {
                foreach (var limitation in milestone.KnownLimitations)
                {
                    any = true;
                    section.Append(CultureInfo.InvariantCulture, $"- {limitation} (from {milestone.Title})\n");
                }
            }
        }

        if (!any)
        {
            section.Append(UnknownLabel).Append('\n');
        }

        return section;
    }

    private static StringBuilder Sources(WeeklyReportContent content)
    {
        var section = new StringBuilder();
        section.Append("\n## Sources\n");

        var any = false;

        foreach (var project in content.Projects)
        {
            foreach (var milestone in project.Milestones)
            {
                any = true;
                var source = content.Sources.SingleOrDefault(candidate =>
                    candidate.ProjectId == project.ProjectId && candidate.MilestoneId == milestone.MilestoneId);

                if (source?.BundleId is null)
                {
                    section.Append(CultureInfo.InvariantCulture,
                        $"- {project.ProjectName} / {milestone.Title}: no recorded evidence.\n");
                }
                else
                {
                    section.Append(CultureInfo.InvariantCulture,
                        $"- {project.ProjectName} / {milestone.Title}: evidence bundle {source.BundleId:D} "
                        + $"revision {source.BundleRevision.ToString(CultureInfo.InvariantCulture)}.\n");
                }
            }
        }

        if (!any)
        {
            section.Append("No milestones in this window, so no sources were bound.\n");
        }

        return section;
    }

    private static DateTimeOffset ToUtc(DateTime localMidnight, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified);

        // A midnight skipped by a spring-forward transition advances to the first valid minute.
        // The loop is bounded: a gap longer than three hours is not a time zone, it is data
        // corruption, and refusing it is safer than sliding the window by a day.
        for (var attempt = 0; attempt < 180; attempt++)
        {
            var candidate = unspecified.AddMinutes(attempt);

            if (!zone.IsInvalidTime(candidate))
            {
                return TimeZoneInfo.ConvertTimeToUtc(candidate, zone);
            }
        }

        throw new ValidationFailedException(
            "weekStart",
            "The week starts on a local calendar date as YYYY-MM-DD.");
    }
}

/// <summary>One evidence binding a report was rendered from.</summary>
public sealed record WeeklyReportSource(
    Guid ProjectId,
    Guid MilestoneId,
    Guid? BundleId,
    long BundleRevision);

/// <summary>One in-window confirmed milestone, as the renderer reads it.</summary>
public sealed record WeeklyReportMilestone(
    Guid MilestoneId,
    string Title,
    DateTimeOffset OccurredAt,
    string? Version,
    IReadOnlyList<string> KnownLimitations,
    IReadOnlyList<string> NextSteps);

/// <summary>One complete screenshot item, as the renderer reads it.</summary>
public sealed record WeeklyReportScreenshot(Guid MediaId, string Caption);

/// <summary>One covered project with its window milestones and screenshots.</summary>
public sealed record WeeklyReportProject(
    Guid ProjectId,
    string ProjectName,
    IReadOnlyList<WeeklyReportMilestone> Milestones,
    IReadOnlyList<WeeklyReportScreenshot> Screenshots);

/// <summary>Everything the renderer may read: confirmed window state and owner words.</summary>
public sealed record WeeklyReportContent(
    string ClientName,
    string WeekStart,
    string TimeZone,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<WeeklyReportProject> Projects,
    IReadOnlyList<WeeklyReportSource> Sources,
    string InProgressNotes,
    string NextWeekNotes);
