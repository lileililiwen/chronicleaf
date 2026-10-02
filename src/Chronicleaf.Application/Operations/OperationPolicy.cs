namespace Chronicleaf.Application.Operations;

/// <summary>
/// The bounds every durable operation obeys (BACKGROUND-JOBS-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Attempts.</b> At most <see cref="MaxAttempts"/> claims. A permanent failure - a validation or
/// authentication refusal from a provider - is not retried at all: it fails after the first attempt,
/// because repeating a request that was refused changes nothing and only delays the truth. An
/// explicitly transient failure is retried on the fixed schedule
/// <see cref="BackoffFor"/>, and a third attempt is the last one whatever happens, so an operation can
/// never spin.
/// </para>
/// <para>
/// <b>Rate limiting.</b> A provider that answers 429 may state its own <c>Retry-After</c>. That value
/// is honoured but never trusted without a ceiling: anything above <see cref="MaxRetryAfter"/> is
/// clamped, so a provider cannot park an operation indefinitely by naming a year.
/// </para>
/// <para>
/// <b>Timeouts and leases.</b> An attempt that outlives its timeout is cancelled and treated as
/// transient. The lease is deliberately longer than the longest timeout: a worker that is still
/// inside a 120s attempt at 180s has already failed its own timeout, so the lease expiring can only
/// mean the worker is gone rather than slow, and taking the run over then cannot overlap two live
/// attempts of the same operation.
/// </para>
/// <para>
/// Every value here is a constant, and the tests assert these constants rather than the values the
/// tests happened to configure.
/// </para>
/// </remarks>
public static class OperationPolicy
{
    /// <summary>The total number of claims an operation may ever receive.</summary>
    public const int MaxAttempts = 3;

    /// <summary>How long the first retry waits after the first attempt failed.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);

    /// <summary>How long the second retry waits after the second attempt failed.</summary>
    public static readonly TimeSpan SecondBackoff = TimeSpan.FromSeconds(30);

    /// <summary>The ceiling applied to any provider-stated <c>Retry-After</c>.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(300);

    /// <summary>The attempt timeout for a kind with no more specific bound.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The attempt timeout for capture work, which is bounded tighter than anything else.</summary>
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(45);

    /// <summary>The attempt timeout for export work.</summary>
    public static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How long one worker's claim on an operation is good for without a heartbeat.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(180);

    /// <summary>How often the owning worker proves it is still alive.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>How many times an expired lease may be taken over. Once, and never again.</summary>
    public const int MaxLeaseRecoveries = 1;

    /// <summary>
    /// The delay before attempt number <paramref name="completedAttempts"/> + 1. The bound is the
    /// last backoff, because a fourth attempt is never scheduled.
    /// </summary>
    public static TimeSpan BackoffFor(int completedAttempts) => completedAttempts switch
    {
        1 => FirstBackoff,
        _ => SecondBackoff,
    };

    /// <summary>
    /// The delay a provider asked for, clamped. A missing, negative or oversized value falls back to
    /// the fixed backoff for the attempt, so the bound cannot be widened by a response body.
    /// </summary>
    public static TimeSpan DelayFor(int completedAttempts, TimeSpan? retryAfter)
    {
        if (retryAfter is not { } requested || requested <= TimeSpan.Zero)
        {
            return BackoffFor(completedAttempts);
        }

        return requested < MaxRetryAfter ? requested : MaxRetryAfter;
    }

    /// <summary>
    /// The attempt timeout for a kind. Capture is the tighter bound the design fixes; export and
    /// everything else share the default. The match is on the kind's words, not on an exact string, so
    /// a later package naming a kind <c>screenshot-capture</c> gets the capture bound without this
    /// file having to know the name.
    /// </summary>
    public static TimeSpan TimeoutFor(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);

        if (kind.Contains("capture", StringComparison.OrdinalIgnoreCase))
        {
            return CaptureTimeout;
        }

        return kind.Contains("export", StringComparison.OrdinalIgnoreCase)
            ? ExportTimeout
            : DefaultTimeout;
    }
}
