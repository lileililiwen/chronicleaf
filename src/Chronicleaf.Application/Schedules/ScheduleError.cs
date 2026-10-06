using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Schedules;

/// <summary>
/// The scheduled-project-sync envelope codes. They extend the shared envelope rather than replacing
/// it: the body shape stays <c>{code,message,traceId,fieldErrors?}</c> and the messages are authored
/// here, never taken from a provider body, a token or a database message.
/// </summary>
public static class ScheduleError
{
    /// <summary>The scope already holds the maximum number of enabled schedules (429).</summary>
    public const string QuotaExceeded = "schedule_quota";

    /// <summary>The project has no readable repository connection to schedule against (409).</summary>
    public const string NoConnection = "schedule_no_connection";
}

/// <summary>
/// Enabling this schedule would pass the scope's enabled-schedule quota. The remedy is to disable
/// another schedule first, never to retry this one.
/// </summary>
public sealed class ScheduleQuotaExceededException(int limit)
    : ApiException(
        ScheduleError.QuotaExceeded,
        $"This scope already holds the maximum of {limit} enabled sync schedules.")
{
    /// <summary>The quota that refused this activation. The caller's count is never echoed.</summary>
    public int Limit { get; } = limit;
}

/// <summary>
/// The project has no repository connection this schedule could poll. The remedy is to connect a
/// repository first, never to schedule against nothing.
/// </summary>
public sealed class ScheduleConnectionMissingException()
    : ApiException(
        ScheduleError.NoConnection,
        "This project has no connected repository to synchronize.");
