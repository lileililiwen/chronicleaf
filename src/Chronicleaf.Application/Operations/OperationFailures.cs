namespace Chronicleaf.Application.Operations;

/// <summary>
/// The bounded vocabulary a failure is reduced to before it is stored in
/// <c>operations.error_code</c>.
/// </summary>
/// <remarks>
/// The stored value is a code, never a message. A provider's response body, an exception message, a
/// file path, a stack trace and a connection string are all things that routinely carry a credential or
/// a fragment of user content, so none of them is ever written to the operation row or returned to a
/// client. Anything a handler does not name is stored as <see cref="Unhandled"/>, which is the honest
/// answer: it says the operation failed and says nothing about why.
/// </remarks>
public static class OperationErrorCodes
{
    /// <summary>The handler named a failure that will not succeed on a repeat attempt.</summary>
    public const string Permanent = "operation_permanent_failure";

    /// <summary>The attempt exceeded the kind's timeout.</summary>
    public const string Timeout = "operation_timeout";

    /// <summary>The provider asked us to slow down; the delay is its own, clamped.</summary>
    public const string RateLimited = "operation_rate_limited";

    /// <summary>The provider was unreachable or answered 5xx.</summary>
    public const string ProviderUnavailable = "operation_provider_unavailable";

    /// <summary>The operation's own timeout passed and a slower retry is still possible.</summary>
    public const string Transient = "operation_transient_failure";

    /// <summary>No handler is registered for the kind, so no work could be started at all.</summary>
    public const string NoHandler = "operation_handler_unavailable";

    /// <summary>The failure was not classified, and nothing about it is stored.</summary>
    public const string Unhandled = "operation_failed";

    /// <summary>Every code this build can write, so a test can assert the set is closed.</summary>
    public static readonly IReadOnlyList<string> All =
        [Permanent, Timeout, RateLimited, ProviderUnavailable, Transient, NoHandler, Unhandled];
}

/// <summary>
/// Base type for a failure a handler raises to classify its own outcome.
/// </summary>
/// <remarks>
/// A handler knows whether its failure will succeed on a second attempt; nothing else can, and a
/// policy that guessed would either retry a refusal forever or give up on a blip. So classification is
/// explicit at the throw site, and anything thrown that is not one of these is treated as
/// <see cref="OperationErrorCodes.Transient"/> with the backoff bound still applied - the bound, not a
/// guess, is what makes an unclassified failure safe.
/// </remarks>
public abstract class OperationFailedException : Exception
{
    protected OperationFailedException(string errorCode, bool isTransient, TimeSpan? retryAfter, Exception? inner)
        : base($"Operation failed: {errorCode}.", inner)
    {
        ErrorCode = errorCode;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    /// <summary>The redacted code stored on the operation and returned to the owner.</summary>
    public string ErrorCode { get; }

    /// <summary>Whether a later attempt is allowed at all.</summary>
    public bool IsTransient { get; }

    /// <summary>
    /// The provider-stated delay, if any. Clamped by <see cref="OperationPolicy.DelayFor"/> before it
    /// reaches a schedule.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// A failure that will not succeed on a repeat attempt: a validation refusal, an authentication
/// refusal, a malformed request. The operation fails after this attempt and is not retried.
/// </summary>
public sealed class OperationPermanentException : OperationFailedException
{
    public OperationPermanentException(string errorCode = OperationErrorCodes.Permanent, Exception? inner = null)
        : base(errorCode, isTransient: false, retryAfter: null, inner)
    {
    }
}

/// <summary>
/// A failure that is explicitly worth another attempt: a timeout, an unreachable provider, a 5xx.
/// Retried on <see cref="OperationPolicy.BackoffFor"/> up to <see cref="OperationPolicy.MaxAttempts"/>.
/// </summary>
public sealed class OperationTransientException : OperationFailedException
{
    public OperationTransientException(
        string errorCode = OperationErrorCodes.Transient,
        TimeSpan? retryAfter = null,
        Exception? inner = null)
        : base(errorCode, isTransient: true, retryAfter, inner)
    {
    }
}

/// <summary>
/// A provider answered 429. Transient, and carries the provider's requested delay, which the worker
/// clamps to <see cref="OperationPolicy.MaxRetryAfter"/> before scheduling the next attempt.
/// </summary>
public sealed class OperationRateLimitedException : OperationFailedException
{
    public OperationRateLimitedException(TimeSpan? retryAfter = null, Exception? inner = null)
        : base(OperationErrorCodes.RateLimited, isTransient: true, retryAfter, inner)
    {
    }
}
