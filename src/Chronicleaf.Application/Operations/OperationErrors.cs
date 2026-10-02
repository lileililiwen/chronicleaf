using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Operations;

/// <summary>
/// The stored vocabulary of operation request and outcome errors. Like every other code in the
/// envelope, each is a stable machine word: a client switches on it and a human reads a message.
/// </summary>
public static class OperationError
{
    /// <summary>No operation with that id exists in the caller's scope, or it never did.</summary>
    public const string NotFound = "operation_not_found";

    /// <summary>The idempotency key is already bound to different work.</summary>
    public const string IdempotencyConflict = "operation_idempotency_conflict";

    /// <summary>The operation has already reached a terminal status and cannot be cancelled.</summary>
    public const string NotCancellable = "operation_not_cancellable";

    /// <summary>Messages a client may see. None of them contains anything from a provider or a target.</summary>
    public static class Messages
    {
        public const string NotFound = "The requested operation does not exist.";
        public const string IdempotencyConflict =
            "That idempotency key is already used for a different request.";
        public const string NotCancellable = "The operation has already finished and cannot be cancelled.";
    }
}

/// <summary>
/// No operation with that id exists in the caller's scope. The same answer is given for an id that
/// exists in a different scope, so the boundary reveals nothing about another scope's operations.
/// </summary>
public sealed class OperationNotFoundException(Guid operationId)
    : ApiException(OperationError.NotFound, OperationError.Messages.NotFound)
{
    public Guid OperationId { get; } = operationId;
}

/// <summary>
/// The idempotency key is already bound to a different request. The existing operation is left
/// exactly as it is: a conflict never creates, mutates or re-dispatches anything.
/// </summary>
public sealed class OperationIdempotencyConflictException(
    string idempotencyKey,
    string existingFingerprint)
    : ApiException(OperationError.IdempotencyConflict, OperationError.Messages.IdempotencyConflict)
{
    public string IdempotencyKey { get; } = idempotencyKey;

    /// <summary>The bound request's fingerprint, so a caller can see which payload won.</summary>
    public string ExistingFingerprint { get; } = existingFingerprint;
}

/// <summary>
/// The operation is already terminal, so asking it to stop is a conflict rather than a silent success.
/// </summary>
public sealed class OperationNotCancellableException(Core.OperationStatus status)
    : ApiException(OperationError.NotCancellable, OperationError.Messages.NotCancellable)
{
    public Core.OperationStatus Status { get; } = status;
}
