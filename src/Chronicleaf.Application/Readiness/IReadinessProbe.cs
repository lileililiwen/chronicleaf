namespace Chronicleaf.Application.Readiness;

/// <summary>Why the runtime is or is not ready to serve traffic.</summary>
public enum ReadinessStatus
{
    /// <summary>The expected schema is reachable and current.</summary>
    Ready,

    /// <summary>PostgreSQL could not be reached or is not configured.</summary>
    DatabaseUnavailable,

    /// <summary>PostgreSQL answered, but the schema is missing or older than this build requires.</summary>
    SchemaMismatch,
}

/// <summary>The externally visible readiness answer, mapped to the HTTP contract in the API layer.</summary>
/// <param name="Status">The observed state.</param>
/// <param name="Code">The envelope code: <c>database_unavailable</c> or <c>schema_mismatch</c>.</param>
/// <param name="Message">An authored, redacted description. Never a provider body or a host path.</param>
/// <param name="RequiredSchemaVersion">The schema version this build requires.</param>
/// <param name="AppliedSchemaVersion">The newest version the database has applied, when readable.</param>
public readonly record struct ReadinessReport(
    ReadinessStatus Status,
    string Code,
    string Message,
    int RequiredSchemaVersion,
    int? AppliedSchemaVersion)
{
    public bool IsReady => Status == ReadinessStatus.Ready;
}

/// <summary>
/// Checks the durable dependency the runtime cannot work without. Implementations must answer
/// from a real connection attempt: a process that cannot reach its database is not ready.
/// </summary>
public interface IReadinessProbe
{
    Task<ReadinessReport> CheckAsync(CancellationToken cancellationToken = default);
}
