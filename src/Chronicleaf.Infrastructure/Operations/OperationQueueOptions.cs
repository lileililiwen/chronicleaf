namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// What this build is willing to expose about background operations, and where it puts its queue.
/// </summary>
/// <remarks>
/// <para><b>The dashboard is off, and there is no setting that turns it on.</b> The Hangfire dashboard
/// is a full application - it lists every job, its arguments, its serialized state and its history,
/// and it accepts commands - and the job payload in this build is deliberately only an operation id,
/// but the surrounding rows would still describe work, timings and failures. Mapping it would put that
/// behind the public web pipeline, and the access boundary's public allow-list is the wrong place to
/// fix that: a dashboard is not an API route, so a route-level rule would not even see it. The
/// package therefore does not reference the dashboard integration at all. <see cref="DashboardEnabled"/>
/// exists so the decision is visible and testable rather than an absence nobody notices; a build that
/// sets it true is refused at startup instead of quietly starting a dashboard.</para>
///
/// <para>An operator who genuinely needs a queue view reads the operations table through the owner's
/// authenticated API, which is scoped, redacted and audited, rather than through a page that would
/// have to be separately secured.</para>
///
/// <para><b>The schema.</b> The queue lives in its own schema inside the same database, so the engine's
/// tables are separable, nameable and never confused with the application's own, and one connection
/// string still points at one database.</para>
/// </remarks>
public sealed class OperationQueueOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Chronicleaf:Operations";

    /// <summary>
    /// Always false. Setting it to true is a startup error, not a request to enable the dashboard:
    /// the dashboard integration is not referenced by this build, so there is nothing to turn on.
    /// </summary>
    public bool DashboardEnabled { get; set; }

    /// <summary>The PostgreSQL schema the queue's own tables live in.</summary>
    public string SchemaName { get; set; } = "hangfire";

    /// <summary>How many workers this process runs. More than one is what makes the boundary real.</summary>
    public int WorkerCount { get; set; } = 4;

    /// <summary>How often the engine looks for new work on its scheduling tables.</summary>
    public TimeSpan SchedulePollingInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often a worker reports that it is still alive.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The name this process's workers report themselves under. It appears in the queue's own tables
    /// and in the engine's logs; it carries no secret and is not shown to a client.
    /// </summary>
    /// <remarks>
    /// The engine does not take this from the application: the scheduling adapter's registration gives
    /// an application no way to set a server name, so the engine reports its own host-and-process
    /// identity instead. The setting is kept so an existing configuration is still accepted and
    /// documented rather than silently unknown, but it no longer changes what the engine writes.
    /// </remarks>
    public string ServerName { get; set; } = "chronicleaf";
}

/// <summary>
/// Whether this process actually has a running queue.
/// </summary>
/// <remarks>
/// This is a fact about the configuration, not a guess. A deployment with no connection string has no
/// queue, because the engine cannot be pointed at a host that does not exist, and a queue that
/// silently degraded to memory is the one failure mode this package exists to prevent. Anything that
/// needs to know - a test, a diagnostic - reads it rather than inferring from "did the server start".
/// </remarks>
/// <param name="Configured">True when a connection string was configured and the queue is running.</param>
public sealed record OperationQueueAvailability(bool Configured);
