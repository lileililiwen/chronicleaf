using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.AI;

/// <summary>
/// The codes this package adds to the delivered error envelope
/// (LLM-PROVIDER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// The delivered <c>{code,message,traceId,fieldErrors?}</c> envelope and its status mapping are
/// unchanged; this is the vocabulary, not a second envelope. Every message in this package is
/// authored here in fixed words: the middleware writes <c>Exception.Message</c> verbatim into the
/// envelope and into the log line beside it, so a message that interpolated a provider's body, its
/// token, a model name from a response or an absolute endpoint path would publish all four. That is
/// why nothing in this file takes a value to interpolate.
/// </remarks>
public static class AiError
{
    /// <summary>
    /// The consent no longer covers the input, the provider selection or the configuration it was
    /// bound to (409).
    /// </summary>
    /// <remarks>
    /// This is the refusal that keeps a provider call from happening, and it is deliberately not a
    /// 404 and not a validation error: nothing is wrong with the request, and something the owner
    /// already consented to has since changed. A 422 would invite a client to retry the same
    /// consented bytes; a 404 would invite it to go looking for another preview. The answer is a new
    /// preview, and the message says so.
    /// </remarks>
    public const string ConsentStale = "consent_stale";

    /// <summary>
    /// The provider did not answer inside its budget (504).
    /// </summary>
    /// <remarks>
    /// <b>Never retried by this system.</b> A timeout is ambiguous: the provider may have accepted
    /// the request and started billing it, and a silent second attempt would be a second paid call
    /// the owner did not authorize. The owner decides whether to try again.
    /// </remarks>
    public const string GenerationTimeout = "generation_timeout";

    /// <summary>The provider rejected the stored credential (422).</summary>
    public const string CredentialRejected = "credential_rejected";

    /// <summary>The provider is throttling (429).</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>
    /// No adapter is installed for the selected provider slot (422).
    /// </summary>
    /// <remarks>
    /// Honest and specific. The five adapter packages are separate roadmap units, so before one is
    /// installed the product can list the slot, show that a credential is configured and describe the
    /// gap - but it cannot reach the provider. Answering anything else here would either be a lie
    /// about capability or a silent no-op that looked like a success.
    /// </remarks>
    public const string ProviderNotInstalled = "provider_not_installed";

    /// <summary>The named model is not one the selected adapter's provider accepts (422).</summary>
    public const string ModelNotSupported = "model_not_supported";

    /// <summary>The provider is unavailable and the bounded retry budget is spent (503).</summary>
    public const string ProviderUnavailable = "provider_unavailable";

    /// <summary>The provider answered with something that is not usable (502).</summary>
    public const string ProviderInvalidResponse = "provider_invalid_response";

    /// <summary>The provider declined to generate (502).</summary>
    public const string ProviderRefused = "provider_refused";

    /// <summary>A preview id is already taken (409).</summary>
    public const string PreviewConflict = "preview_conflict";

    /// <summary>Semantic validation failed (422), with per-field detail.</summary>
    public const string ValidationFailed = "validation_failed";

    /// <summary>The resource is absent, or belongs to another scope (404).</summary>
    public const string NotFound = "not_found";
}

/// <summary>
/// The consent the owner gave no longer covers what would be sent (409).
/// </summary>
/// <remarks>
/// The raised case, not the message, is what makes this safe: the snapshot is never re-read and
/// never re-bound to make it look current, and no provider is resolved before this is decided, so
/// the transport is not merely declined afterwards - it is never reached.
/// </remarks>
public sealed class ConsentStaleException : ApiException
{
    /// <param name="reason">
    /// Which part of the binding moved, as one of the <see cref="ConsentStaleReasons"/> words. It
    /// names the kind of change and never the new value, so the envelope cannot be used to read the
    /// current configuration an owner did not just consent to.
    /// </param>
    public ConsentStaleException(string reason)
        : base(AiError.ConsentStale, $"The consent no longer covers this preview ({reason}). Review the redacted input and authorize it again.")
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>
/// Why a consent went stale, as closed-set words safe to log and to return.
/// </summary>
public static class ConsentStaleReasons
{
    /// <summary>The preview's fifteen minutes are up. The row is kept, so the answer is still 409.</summary>
    public const string Expired = "the authorization window has closed";

    /// <summary>
    /// The authorized provider, model or credential reference no longer resolves to the same thing.
    /// </summary>
    public const string ProviderConfigurationChanged = "the provider configuration changed";

    /// <summary>
    /// The stored snapshot no longer hashes to the digest it was stored with, or no longer names the
    /// purpose and aggregate it was created for.
    /// </summary>
    /// <remarks>
    /// Deliberately a different word from the provider-configuration case above, although both answer
    /// 409 and both send the caller back for a new preview. The distinction is for whoever reads the
    /// envelope and the log: one means a credential moved, the other means the stored bytes are not
    /// the bytes that were authorized, and those are very different things to investigate.
    /// </remarks>
    public const string SnapshotAltered = "the stored input is not the input that was authorized";

    /// <summary>The evidence head behind the snapshot moved, or an authorized item left it.</summary>
    public const string EvidenceChanged = "the evidence changed";

    /// <summary>The project's voice profile moved, so the authorized wording is no longer the wording.</summary>
    public const string VoiceChanged = "the voice profile changed";

    /// <summary>The caller presented a consent hash that is not this preview's.</summary>
    public const string HashMismatch = "the presented consent does not match this preview";
}

/// <summary>
/// The provider did not answer inside its budget (504). The call is not retried.
/// </summary>
public sealed class GenerationTimeoutException : ApiException
{
    public GenerationTimeoutException(int timeoutSeconds)
        : base(AiError.GenerationTimeout, $"The provider did not answer within {timeoutSeconds}s. It may or may not have accepted the request, so it was not retried.")
    {
        TimeoutSeconds = timeoutSeconds;
    }

    public int TimeoutSeconds { get; }
}

/// <summary>
/// A provider outcome that is not a draft (502 for refusal and malformed output, 422 for a rejected
/// credential, 429 for throttling, 503 for an exhausted retry budget).
/// </summary>
/// <remarks>
/// The code is taken from <see cref="GenerationErrorCodes"/> and the message is fixed text. The
/// provider's own error body, status line, request id and endpoint are dropped on the floor here:
/// this exception is the only place a provider failure becomes an HTTP response, and a provider body
/// is exactly where a leaked key fragment or a customer's name would be sitting in plain text.
/// </remarks>
public sealed class GenerationFailedException : ApiException
{
    public GenerationFailedException(string code, string message)
        : base(code, message) { }
}

/// <summary>
/// The selected provider slot has no adapter installed (422).
/// </summary>
public sealed class ProviderNotInstalledException : ApiException
{
    public ProviderNotInstalledException(string providerId)
        : base(AiError.ProviderNotInstalled, "No adapter is installed for the selected provider, so it cannot be called.")
    {
        ProviderId = providerId;
    }

    /// <summary>The slot that was asked for. A provider name, not a secret.</summary>
    public string ProviderId { get; }
}

/// <summary>
/// The named model is not one the adapter's provider accepts (422).
/// </summary>
/// <remarks>
/// The value the caller sent is echoed in <c>fieldErrors</c> as the field name's own detail only
/// through <see cref="ValidationFailedException"/>; here the model string is deliberately not
/// retained, so a model name that a client filled in with something sensitive is not stored on a
/// server-side object that could reach a log.
/// </remarks>
public sealed class ModelNotSupportedException : ApiException
{
    public ModelNotSupportedException(string providerId)
        : base(AiError.ModelNotSupported, $"The selected provider does not accept that model, so no model was substituted and no call was made.")
    {
        ProviderId = providerId;
    }

    public string ProviderId { get; }
}

/// <summary>
/// A preview id was already taken (409).
/// </summary>
/// <remarks>
/// Reachable only by a collision, because preview ids are freshly generated. It is answered rather
/// than swallowed because a silent retry on the caller's behalf would be a write this package cannot
/// account for, and a 409 is the honest name for "this id is not free".
/// </remarks>
public sealed class GenerationPreviewConflictException()
    : ApiException(AiError.PreviewConflict, "A generation preview with that id already exists.");
