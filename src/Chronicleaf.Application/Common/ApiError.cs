using System.Text.Json;

namespace Chronicleaf.Application.Common;

/// <summary>
/// The one error envelope this repository serializes: <c>{code,message,traceId,fieldErrors?}</c>.
/// Feature packages add codes; they do not change this shape.
/// </summary>
public sealed record ApiError
{
    public const string MalformedJson = "malformed_json";
    public const string ValidationFailed = "validation_failed";
    public const string IfMatchRequired = "if_match_required";
    public const string RevisionConflict = "revision_conflict";
    public const string PayloadTooLarge = "payload_too_large";
    public const string DatabaseUnavailable = "database_unavailable";
    public const string SchemaMismatch = "schema_mismatch";
    public const string DependencyTimeout = "dependency_timeout";
    public const string InternalError = "internal_error";

    /// <summary>
    /// A resource id that does not exist, or exists in a scope this caller may not read. It is the
    /// same code the runtime shell already uses for an unserved <c>/api</c> path, for the same
    /// reason: a caller learns that there is nothing here and nothing about what might be.
    /// </summary>
    public const string NotFound = "not_found";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public required string Code { get; init; }

    public required string Message { get; init; }

    public required string TraceId { get; init; }

    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; init; }

    public static ApiError Create(
        string code,
        string message,
        string traceId,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        new() { Code = code, Message = message, TraceId = traceId, FieldErrors = fieldErrors };

    /// <summary>Serializes the envelope with the repository's camelCase JSON contract.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Wire);
}
