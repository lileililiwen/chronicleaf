using System.Globalization;
using System.Text.Json.Serialization;
using Chronicleaf.Application.Operations;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Operations;

/// <summary>
/// The wire shape of one durable operation (BACKGROUND-JOBS-001, BACKGROUND-JOBS-002).
/// </summary>
/// <remarks>
/// <para>
/// Field names are pinned explicitly, as the access and milestone packages' are, so the contract
/// cannot drift with serializer policy. Timestamps cross the wire as UTC ISO-8601.
/// </para>
/// <para>
/// <b>What a client is told.</b> The status is the six persisted words, unchanged, because the panel
/// reports them and a client is not expected to translate anything. The attempt count is the row's
/// own, so a caller can see the bound being used rather than being told "it will retry". The error
/// code, when there is one, is from the package's fixed vocabulary and never a provider body. The
/// result reference is present exactly when the operation succeeded - which is the honest shape, since
/// a result reference and a success are committed in the same statement and so cannot disagree.
/// </para>
/// <para>
/// What a client is <em>not</em> told: the lease token, the lease deadline, the recovery count and the
/// fingerprint. They are the worker's internal bookkeeping and the idempotency binding respectively,
/// and neither is the caller's business; a scope-bounded list is the right place to learn that a
/// repeat key returned the original operation.
/// </para>
/// </remarks>
public sealed record OperationResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("inputRevision")] long InputRevision,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("maxAttempts")] int MaxAttempts,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("resultRef")] string? ResultRef,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt,
    [property: JsonPropertyName("heartbeatAt")] string? HeartbeatAt,
    [property: JsonPropertyName("nextAttemptAt")] string? NextAttemptAt,
    [property: JsonPropertyName("completedAt")] string? CompletedAt,
    [property: JsonPropertyName("cancelRequestedAt")] string? CancelRequestedAt,
    [property: JsonPropertyName("cancellable")] bool Cancellable)
{
    public static OperationResponse From(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return new OperationResponse(
            operation.Id.ToString("D"),
            operation.Kind,
            operation.TargetId.ToString("D"),
            operation.InputRevision,
            operation.IdempotencyKey,
            OperationStatuses.ToWire(operation.Status),
            operation.Attempt,
            OperationPolicy.MaxAttempts,
            operation.ErrorCode,
            operation.ResultRef,
            Timestamp(operation.CreatedAt),
            Timestamp(operation.UpdatedAt),
            Timestamp(operation.HeartbeatAt),
            Timestamp(operation.NextAttemptAt),
            Timestamp(operation.CompletedAt),
            Timestamp(operation.CancelRequestedAt),
            operation.IsCancellable);
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Timestamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>The scope's operations, newest first.</summary>
public sealed record OperationListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<OperationResponse> Items)
{
    public static OperationListResponse From(IReadOnlyList<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return new OperationListResponse([.. operations.Select(OperationResponse.From)]);
    }
}

/// <summary>
/// The body of <c>POST /api/operations</c>.
/// </summary>
/// <remarks>
/// The body names the work and the caller's idempotency key; it cannot name a status, an attempt
/// count, a result or an error code. A caller that submits the same key twice gets the original
/// operation back rather than a second one, and one that submits the same key with different work
/// gets a 409.
/// </remarks>
/// <param name="Kind">The handler kind. Must be one a registered handler owns.</param>
/// <param name="TargetId">The record the work acts on.</param>
/// <param name="InputRevision">The target revision this work is bound to.</param>
/// <param name="IdempotencyKey">The caller's key, unique within (scope, kind).</param>
public sealed record CreateOperationRequest(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("targetId")] string? TargetId,
    [property: JsonPropertyName("inputRevision")] long? InputRevision,
    [property: JsonPropertyName("idempotencyKey")] string? IdempotencyKey);
