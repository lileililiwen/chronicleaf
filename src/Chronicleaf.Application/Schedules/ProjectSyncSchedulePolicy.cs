using System.Globalization;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Schedules;

/// <summary>
/// The closed policy of the scheduled-project-sync package
/// (SCHEDULED-PROJECT-SYNC-001/002).
/// </summary>
/// <remarks>
/// <b>Every bound here is a refusal, never a default.</b> An interval outside the cadence range is a
/// 422 naming the field, an activation past the quota is a 429 naming the limit, and a tick that
/// finds nothing to do advances nothing. Nothing in this file reaches a provider, a vault or a
/// queue: it classifies requests so the service can decide.
/// </remarks>
public static class ProjectSyncSchedulePolicy
{
    /// <summary>How many due schedules one tick pass takes on. The rest wait for the next minute.</summary>
    public const int MaxDuePerTick = 100;

    /// <summary>True when the interval is one the service may store.</summary>
    public static bool IsStorableInterval(int intervalMinutes) =>
        intervalMinutes >= ProjectSyncSchedule.MinIntervalMinutes &&
        intervalMinutes <= ProjectSyncSchedule.MaxIntervalMinutes;

    /// <summary>The refusal text for an interval the service may not store.</summary>
    public static string IntervalMessage(int _)
        => $"A sync interval is {ProjectSyncSchedule.MinIntervalMinutes} to {ProjectSyncSchedule.MaxIntervalMinutes} minutes.";

    /// <summary>
    /// The idempotency key a firing tick enqueues under: the schedule and the instant it fired for.
    /// Two ticks for one due instant key alike, so the second gets the original operation rather than
    /// a second poll.
    /// </summary>
    public static string IdempotencyKey(Guid scheduleId, DateTimeOffset dueAt) =>
        $"{scheduleId:N}:{dueAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
}
