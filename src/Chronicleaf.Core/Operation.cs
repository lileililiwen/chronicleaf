namespace Chronicleaf.Core;

/// <summary>
/// The durable unit of background work (BACKGROUND-JOBS-001, BACKGROUND-JOBS-002).
/// </summary>
/// <remarks>
/// The eleven fields the design fixes are all here - <c>id</c>, <c>scope_id</c>, <c>kind</c>,
/// <c>target_id</c>, <c>input_revision</c>, <c>idempotency_key</c>, <c>status</c>, <c>attempt</c>,
/// <c>heartbeat_at</c>, <c>error_code</c> and <c>result_ref</c> - and so are the few more the design's
/// own invariants need in order to be enforceable rather than merely stated: the lease that decides
/// which worker owns the run, the schedule the next attempt is due at, the cancellation stamp, and
/// the fingerprint that makes "an identical payload" a fact the database can check.
/// <para>
/// The record is immutable. Every state change is a conditional statement whose WHERE clause is the
/// safety property, exactly as a project edit is: there is no setter here that a caller could use to
/// move an operation from one status to another, so the only way to reach a status is to win the
/// compare-and-set that grants it.
/// </para>
/// <para>
/// Nothing that reaches a worker travels in the job payload. The queue carries the operation id and
/// nothing else; the worker reads this record from PostgreSQL, which is why a credential, a token or
/// a piece of evidence can never be serialized into a job that a provider might see.
/// </para>
/// </remarks>
public sealed class Operation
{
    public Operation(
        Guid id,
        Guid scopeId,
        string kind,
        Guid targetId,
        long inputRevision,
        string idempotencyKey,
        OperationStatus status,
        int attempt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string fingerprint,
        long revision = 1,
        DateTimeOffset? heartbeatAt = null,
        string? errorCode = null,
        string? resultRef = null,
        DateTimeOffset? leaseExpiresAt = null,
        Guid? leaseToken = null,
        DateTimeOffset? nextAttemptAt = null,
        DateTimeOffset? completedAt = null,
        DateTimeOffset? cancelRequestedAt = null,
        int leaseRecoveries = 0)
    {
        Id = id;
        ScopeId = scopeId;
        Kind = kind;
        TargetId = targetId;
        InputRevision = inputRevision;
        IdempotencyKey = idempotencyKey;
        Status = status;
        Attempt = attempt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Fingerprint = fingerprint;
        Revision = revision;
        HeartbeatAt = heartbeatAt;
        ErrorCode = errorCode;
        ResultRef = resultRef;
        LeaseExpiresAt = leaseExpiresAt;
        LeaseToken = leaseToken;
        NextAttemptAt = nextAttemptAt;
        CompletedAt = completedAt;
        CancelRequestedAt = cancelRequestedAt;
        LeaseRecoveries = leaseRecoveries;
    }

    public Guid Id { get; }

    /// <summary>The owner scope, and the only scope this operation may ever be read or claimed in.</summary>
    public Guid ScopeId { get; }

    /// <summary>Which handler owns this work; see <c>Application/Operations.IOperationHandler.Kind</c>.</summary>
    public string Kind { get; }

    /// <summary>The record the work acts on. Not a foreign key: the target's own package owns it.</summary>
    public Guid TargetId { get; }

    /// <summary>
    /// The revision of <see cref="TargetId"/> this operation was created against. It is re-read and
    /// re-checked at result commit, so an operation whose input moved under it is superseded instead
    /// of attaching a stale result.
    /// </summary>
    public long InputRevision { get; }

    /// <summary>Part of the unique key that makes a repeated request return the original operation.</summary>
    public string IdempotencyKey { get; }

    /// <summary>
    /// The fixed-shape value the idempotency key is bound to, derived from the request's target and
    /// input revision. Two requests agree if and only if they would produce the same operation, which
    /// is what "an identical payload" has to mean for the check to be something a database can do.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Advances on every write, and is the EF concurrency token. The worker's writes are already
    /// conditional statements and do not depend on it; it exists so that an accidental tracked save
    /// cannot overwrite a row a compare-and-set already moved.
    /// </summary>
    public long Revision { get; }

    public OperationStatus Status { get; }

    /// <summary>How many times this operation has been claimed by a worker.</summary>
    public int Attempt { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>When the owning worker last proved it was still alive.</summary>
    public DateTimeOffset? HeartbeatAt { get; }

    /// <summary>
    /// A redacted, bounded failure code. Never a provider body, a message, a stack or a path: the
    /// worker maps a failure onto this small vocabulary before it is stored.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// The opaque reference to what the work produced. It is committed in the same statement that
    /// moves the operation to <see cref="OperationStatus.Succeeded"/>, so a success without a result
    /// reference is not representable.
    /// </summary>
    public string? ResultRef { get; }

    /// <summary>When the current lease ends. A worker that has not heartbeated by then loses the run.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; }

    /// <summary>
    /// The identity of the worker that currently holds the lease. Every later write is conditional on
    /// it, which is what makes a second worker's commit a no-op rather than a second result.
    /// </summary>
    public Guid? LeaseToken { get; }

    /// <summary>When the next attempt is due. Recorded so the backoff schedule is observable state.</summary>
    public DateTimeOffset? NextAttemptAt { get; }

    public DateTimeOffset? CompletedAt { get; }

    /// <summary>Set the moment an owner asks for this operation to stop, before the worker notices.</summary>
    public DateTimeOffset? CancelRequestedAt { get; }

    /// <summary>
    /// How many times an expired lease has been taken over. The design allows exactly one recovery,
    /// and this counter is what makes that a database fact rather than a convention.
    /// </summary>
    public int LeaseRecoveries { get; }

    /// <summary>True while the owner may still ask for this operation to stop.</summary>
    public bool IsCancellable =>
        Status is OperationStatus.Queued or OperationStatus.Running;

    /// <summary>True once no further transition is possible.</summary>
    public bool IsTerminal =>
        Status is OperationStatus.Succeeded
            or OperationStatus.Failed
            or OperationStatus.Cancelled
            or OperationStatus.Superseded;
}

/// <summary>
/// The six states a durable operation can be in, and nothing else.
/// </summary>
/// <remarks>
/// The persisted values are the lowercase wire words - <c>queued</c>, <c>running</c>, <c>succeeded</c>,
/// <c>failed</c>, <c>cancelled</c>, <c>superseded</c> - because the operations UI reports exactly
/// these and a client is not expected to translate them. Only the first two are non-terminal.
/// </remarks>
public enum OperationStatus
{
    /// <summary>Committed to the outbox and waiting for a worker to claim it.</summary>
    Queued = 0,

    /// <summary>Claimed by exactly one worker, which holds a heartbeat until it finishes.</summary>
    Running = 1,

    /// <summary>Finished, with a committed result reference in the same statement.</summary>
    Succeeded = 2,

    /// <summary>Stopped after its attempt budget, with a redacted error code.</summary>
    Failed = 3,

    /// <summary>Stopped on the owner's request, with no result committed.</summary>
    Cancelled = 4,

    /// <summary>
    /// Its input revision moved before the result could be committed, so the result was discarded
    /// rather than attached to input that no longer exists.
    /// </summary>
    Superseded = 5,
}

/// <summary>The persisted and wire spelling of an <see cref="OperationStatus"/>.</summary>
public static class OperationStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Superseded = "superseded";

    /// <summary>Every status, in the order the UI lists them.</summary>
    public static readonly IReadOnlyList<string> All =
        [Queued, Running, Succeeded, Failed, Cancelled, Superseded];

    /// <summary>Parses a stored or presented status. Anything unknown is refused, never guessed.</summary>
    public static bool TryParse(string? value, out OperationStatus status)
    {
        switch (value)
        {
            case Queued: status = OperationStatus.Queued; return true;
            case Running: status = OperationStatus.Running; return true;
            case Succeeded: status = OperationStatus.Succeeded; return true;
            case Failed: status = OperationStatus.Failed; return true;
            case Cancelled: status = OperationStatus.Cancelled; return true;
            case Superseded: status = OperationStatus.Superseded; return true;
            default: status = default; return false;
        }
    }

    /// <summary>The stored spelling of a status.</summary>
    public static string ToWire(OperationStatus status) => status switch
    {
        OperationStatus.Queued => Queued,
        OperationStatus.Running => Running,
        OperationStatus.Succeeded => Succeeded,
        OperationStatus.Failed => Failed,
        OperationStatus.Cancelled => Cancelled,
        OperationStatus.Superseded => Superseded,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown operation status."),
    };
}
