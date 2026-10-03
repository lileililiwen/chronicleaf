using System.Diagnostics.CodeAnalysis;

namespace Chronicleaf.AI.Contracts;

/// <summary>
/// One provider-neutral outcome, and the only thing an adapter returns.
/// </summary>
/// <remarks>
/// <para>
/// The shape is total and validated: a caller can rely on <see cref="Status"/> and
/// <see cref="FinishReason"/> always being meaningful, and on text appearing if and only if the
/// status is <see cref="GenerationStatuses.Completed"/>. That invariant is enforced here rather than
/// left to five adapters to remember, because the failure it prevents - a refusal surfacing as a
/// draft - is the exact confusion this contract exists to remove.
/// </para>
/// <para>
/// A <see cref="Failed"/> or <see cref="Invalid"/> result carries no text on purpose. A partial,
/// truncated or malformed body has no business in a field a caller might render, and the rule is
/// cheaper than auditing five adapters for the habit.
/// </para>
/// </remarks>
public sealed record GenerationResult
{
    private GenerationResult(
        string status,
        string finishReason,
        string? text,
        string? providerRequestId,
        int? inputTokens,
        int? outputTokens,
        string? errorCode)
    {
        Status = status;
        FinishReason = finishReason;
        Text = text;
        ProviderRequestId = providerRequestId;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        ErrorCode = errorCode;
    }

    /// <summary>One of <see cref="GenerationStatuses"/>.</summary>
    public string Status { get; }

    /// <summary>The generated text. Present only when <see cref="Status"/> is <c>completed</c>.</summary>
    public string? Text { get; }

    /// <summary>
    /// The provider's own request identifier, when it returned one. Safe to store and to show: it
    /// is the handle a provider uses to correlate a billed request with its own logs.
    /// </summary>
    public string? ProviderRequestId { get; }

    /// <summary>Input tokens as the provider reported them, when it reported them.</summary>
    public int? InputTokens { get; }

    /// <summary>Output tokens as the provider reported them, when it reported them.</summary>
    public int? OutputTokens { get; }

    /// <summary>One of <see cref="GenerationFinishReasons"/>.</summary>
    public string FinishReason { get; }

    /// <summary>One of <see cref="GenerationErrorCodes"/>, when the status is not <c>completed</c>.</summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// A usable result. <paramref name="text"/> must be present and non-blank, and the status is
    /// <c>completed</c> whatever the provider's reason was - a <see cref="GenerationFinishReasons.Length"/>
    /// result is a short draft, not a failure, and only the finish reason says so.
    /// </summary>
    public static GenerationResult Completed(
        string text,
        string finishReason,
        string? providerRequestId = null,
        int? inputTokens = null,
        int? outputTokens = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        RequireKnownFinishReason(finishReason, nameof(finishReason));

        if (inputTokens is < 0 || outputTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputTokens),
                "Token counts are counts. A negative one is a provider reporting nonsense.");
        }

        return new GenerationResult(
            GenerationStatuses.Completed,
            finishReason,
            text,
            providerRequestId,
            inputTokens,
            outputTokens,
            errorCode: null);
    }

    /// <summary>
    /// The provider declined. No text is kept even if the provider sent an explanation, because a
    /// refusal's explanation is provider prose about an owner's content and has no product meaning.
    /// </summary>
    public static GenerationResult Refused(string? providerRequestId = null) =>
        new(
            GenerationStatuses.Refused,
            GenerationFinishReasons.ContentFilter,
            text: null,
            providerRequestId,
            inputTokens: null,
            outputTokens: null,
            errorCode: GenerationErrorCodes.ProviderRefused);

    /// <summary>The provider answered with something unusable.</summary>
    public static GenerationResult Invalid(string finishReason, string? providerRequestId = null) =>
        new(
            GenerationStatuses.Invalid,
            RequireKnownFinishReason(finishReason, nameof(finishReason)),
            text: null,
            providerRequestId,
            inputTokens: null,
            outputTokens: null,
            errorCode: GenerationErrorCodes.ProviderInvalidResponse);

    /// <summary>The call did not complete.</summary>
    public static GenerationResult Failed(string errorCode, string? providerRequestId = null) =>
        new(
            GenerationStatuses.Failed,
            GenerationFinishReasons.Error,
            text: null,
            providerRequestId,
            inputTokens: null,
            outputTokens: null,
            errorCode: RequireKnownErrorCode(errorCode));

    private static string RequireKnownFinishReason(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!GenerationFinishReasons.IsKnown(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a GenerationFinishReasons value.",
                parameterName);
        }

        return value;
    }

    /// <summary>
    /// Refuses an error code this contract does not define, rather than passing it up to be mapped
    /// somewhere further along.
    /// </summary>
    /// <remarks>
    /// The closed set is the point. A free-form code would reach the API envelope, where a status
    /// mapping cannot exist for it and the fallback would be a 500 - turning a provider's own wording
    /// into a server error. Forcing the choice here means an adapter adds a code to the contract,
    /// with a status, before it can report one.
    /// </remarks>
    private static string RequireKnownErrorCode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!GenerationErrorCodes.IsKnown(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a GenerationErrorCodes value.",
                nameof(value));
        }

        return value;
    }
}
