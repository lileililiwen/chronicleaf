namespace Chronicleaf.Core;

/// <summary>
/// One project's opt-in recurring repository synchronization
/// (SCHEDULED-PROJECT-SYNC-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A schedule is off until an owner turns it on, and there is no other way to be on.</b> There is
/// no default row, no implicit cadence and no constructor that enables: a project without a row has
/// no schedule, and a row with <see cref="Enabled"/> false schedules nothing. That is the whole of
/// the "opt-in" in the requirement, and it is structural rather than a convention: the tick only
/// reads rows that say enabled.
/// </para>
/// <para>
/// <b>A schedule proposes and nothing else.</b> When it fires it asks the delivered candidate poll to
/// read, which is how the "candidate-only" half is enforced without a second mechanism: there is no
/// milestone write, no evidence append, no draft and no publish reachable from this row, because the
/// job it triggers is the delivered poll whose own contract already refuses to confirm anything.
/// </para>
/// <para>
/// <b><see cref="Revision"/> guards every change.</b> A schedule edit presents the revision it was
/// read at, and a stale one loses, for the same reason a project edit does. The tick advances the
/// same revision when it fires, so a change that raced a firing tick is a conflict rather than a
/// silent overwrite.
/// </para>
/// </remarks>
public sealed class ProjectSyncSchedule
{
    /// <summary>The smallest cadence an owner may ask for, in minutes (quarter-hourly).</summary>
    public const int MinIntervalMinutes = 15;

    /// <summary>The largest cadence an owner may ask for, in minutes (weekly).</summary>
    public const int MaxIntervalMinutes = 10080;

    /// <summary>How many enabled schedules one scope may hold. Enabling past this is refused.</summary>
    public const int MaxEnabledPerScope = 10;

    /// <param name="id">This schedule's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="projectId">The project whose repository is polled. One schedule per project.</param>
    /// <param name="enabled">Whether the tick may fire for this row.</param>
    /// <param name="intervalMinutes">The cadence, between <see cref="MinIntervalMinutes"/> and <see cref="MaxIntervalMinutes"/>.</param>
    /// <param name="nextRunAt">When the next poll is due. Advanced on every firing tick.</param>
    /// <param name="lastOperationId">The operation the most recent tick created, if any.</param>
    /// <param name="createdAt">When the schedule was first stored.</param>
    /// <param name="updatedAt">When the schedule last changed.</param>
    public ProjectSyncSchedule(
        Guid id,
        Guid scopeId,
        Guid projectId,
        bool enabled,
        int intervalMinutes,
        DateTimeOffset nextRunAt,
        Guid? lastOperationId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A sync schedule needs an id.", nameof(id));
        }

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A sync schedule needs a scope.", nameof(scopeId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A sync schedule needs a project.", nameof(projectId));
        }

        if (intervalMinutes < MinIntervalMinutes || intervalMinutes > MaxIntervalMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervalMinutes),
                intervalMinutes,
                $"A sync interval is {MinIntervalMinutes} to {MaxIntervalMinutes} minutes.");
        }

        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        Enabled = enabled;
        IntervalMinutes = intervalMinutes;
        NextRunAt = nextRunAt;
        LastOperationId = lastOperationId;
        Revision = 1;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    /// <summary>
    /// Rebuilds the stored record: the same checks as a new schedule, with the stored revision
    /// rather than a fresh one. Only the store calls this; owners change a schedule through
    /// <see cref="Change"/>, <see cref="MarkFired"/> and <see cref="Disable"/>.
    /// </summary>
    public static ProjectSyncSchedule Rehydrate(
        Guid id,
        Guid scopeId,
        Guid projectId,
        bool enabled,
        int intervalMinutes,
        DateTimeOffset nextRunAt,
        Guid? lastOperationId,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        var schedule = new ProjectSyncSchedule(
            id, scopeId, projectId, enabled, intervalMinutes, nextRunAt, lastOperationId, createdAt, updatedAt);

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision), revision, "A stored schedule revision is one or greater.");
        }

        schedule.Revision = revision;
        return schedule;
    }

    public Guid ScopeId { get; }

    public Guid ProjectId { get; }

    public bool Enabled { get; private set; }

    public int IntervalMinutes { get; private set; }

    /// <summary>When the next poll is due. The tick moves this forward, never backward.</summary>
    public DateTimeOffset NextRunAt { get; private set; }

    /// <summary>The operation the most recent firing tick created. The operation row itself carries the outcome.</summary>
    public Guid? LastOperationId { get; private set; }

    /// <summary>The optimistic revision every schedule change presents.</summary>
    public long Revision { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Applies an owner's change. The caller has already established that the presented revision is
    /// current; this records the new intent and advances the revision so a concurrent writer loses.
    /// </summary>
    public void Change(bool enabled, int intervalMinutes, DateTimeOffset nextRunAt, Guid? lastOperationId, DateTimeOffset updatedAt)
    {
        if (intervalMinutes < MinIntervalMinutes || intervalMinutes > MaxIntervalMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervalMinutes),
                intervalMinutes,
                $"A sync interval is {MinIntervalMinutes} to {MaxIntervalMinutes} minutes.");
        }

        Enabled = enabled;
        IntervalMinutes = intervalMinutes;
        NextRunAt = nextRunAt;
        LastOperationId = lastOperationId;
        Revision += 1;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Records that the tick fired: the next run moves one interval past <paramref name="now"/>, so a
    /// missed window collapses into one poll rather than a burst of catch-ups, and the revision moves
    /// so a concurrent edit loses instead of overwriting the advance.
    /// </summary>
    public void MarkFired(DateTimeOffset now, Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A fired tick needs the operation it created.", nameof(operationId));
        }

        NextRunAt = now.AddMinutes(IntervalMinutes);
        LastOperationId = operationId;
        Revision += 1;
        UpdatedAt = now;
    }

    /// <summary>
    /// Turns the schedule off without forgetting it: the cadence and the last operation stay, so
    /// re-enabling resumes rather than restarts, and the revision moves so a concurrent edit loses.
    /// </summary>
    public void Disable(DateTimeOffset now)
    {
        Enabled = false;
        Revision += 1;
        UpdatedAt = now;
    }
}
