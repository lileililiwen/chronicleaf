namespace Chronicleaf.Core;

/// <summary>
/// The singleton installation record. Its <see cref="Id"/> is the initial Community scope id
/// and its <see cref="SchemaVersion"/> is the version of the persisted Community state.
/// </summary>
public sealed class Installation
{
    public Installation(Guid id, int schemaVersion, DateTimeOffset createdAt)
    {
        Id = id;
        SchemaVersion = schemaVersion;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public int SchemaVersion { get; }

    public DateTimeOffset CreatedAt { get; }
}

/// <summary>
/// Append-only evidence of a state change. The actor is nullable because the access package
/// owns stable owner ids and later packages may record system-initiated changes; the safe
/// metadata is caller-supplied JSON that must never carry secrets or evidence plaintext.
/// </summary>
public sealed class AuditEvent
{
    public AuditEvent(
        Guid id,
        Guid? actorId,
        string action,
        Guid targetId,
        DateTimeOffset occurredAt,
        string safeMetadataJson)
    {
        Id = id;
        ActorId = actorId;
        Action = action;
        TargetId = targetId;
        OccurredAt = occurredAt;
        SafeMetadataJson = safeMetadataJson;
    }

    public Guid Id { get; }

    public Guid? ActorId { get; }

    /// <summary>At most 80 characters; see the <c>audit_events.action</c> column.</summary>
    public string Action { get; }

    public Guid TargetId { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>camelCase JSON object persisted as <c>jsonb</c>.</summary>
    public string SafeMetadataJson { get; }
}

/// <summary>
/// A durable intent to dispatch work, written in the same transaction as the domain state it
/// describes. <c>(Kind, AggregateId, Revision)</c> is unique, which is what makes redelivery
/// of the same revision impossible.
/// </summary>
public sealed class OutboxEvent
{
    public OutboxEvent(
        Guid id,
        string kind,
        Guid aggregateId,
        long revision,
        string payloadJson,
        DateTimeOffset createdAt,
        DateTimeOffset? dispatchedAt = null)
    {
        Id = id;
        Kind = kind;
        AggregateId = aggregateId;
        Revision = revision;
        PayloadJson = payloadJson;
        CreatedAt = createdAt;
        DispatchedAt = dispatchedAt;
    }

    public Guid Id { get; }

    /// <summary>At most 80 characters; see the <c>outbox.kind</c> column.</summary>
    public string Kind { get; }

    public Guid AggregateId { get; }

    /// <summary>Optimistic revision of the aggregate; part of the uniqueness key.</summary>
    public long Revision { get; }

    /// <summary>camelCase JSON object persisted as <c>jsonb</c>.</summary>
    public string PayloadJson { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? DispatchedAt { get; }

    public bool IsDispatched => DispatchedAt.HasValue;
}
