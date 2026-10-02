namespace Chronicleaf.Application.Common;

/// <summary>
/// Single source of truth for the status code of every envelope code owned by this package.
/// The mapping is contractual: changing a code here changes an HTTP status for shipped clients.
/// </summary>
public static class ApiErrorStatus
{
    public static int ForCode(string code) => code switch
    {
        ApiError.MalformedJson => 400,
        ApiError.ValidationFailed => 422,
        ApiError.IfMatchRequired => 428,
        ApiError.RevisionConflict => 409,
        ApiError.PayloadTooLarge => 413,
        ApiError.DatabaseUnavailable => 503,
        ApiError.SchemaMismatch => 503,
        ApiError.DependencyTimeout => 504,
        _ => 500,
    };
}

/// <summary>
/// Base class for failures that a request may translate into a <see cref="ApiError"/>.
/// </summary>
/// <remarks>
/// Messages carried by these exceptions are authored here, never taken from a provider
/// response body, a token or a filesystem path. The middleware writes
/// <see cref="Message"/> verbatim, so a caller that interpolates untrusted text here
/// would leak it.
/// </remarks>
public abstract class ApiException : Exception
{
    protected ApiException(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(message) => (Code, FieldErrors) = (code, fieldErrors);

    public string Code { get; }

    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; }
}

/// <summary>The request body was not valid JSON (400).</summary>
public sealed class MalformedJsonException(string message = "The request body is not valid JSON.")
    : ApiException(ApiError.MalformedJson, message);

/// <summary>The request parsed but failed semantic validation (422).</summary>
public sealed class ValidationFailedException(IReadOnlyDictionary<string, string[]> fieldErrors)
    : ApiException(ApiError.ValidationFailed, "The request failed validation.", fieldErrors)
{
    public ValidationFailedException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }
}

/// <summary>A mutation that requires <c>If-Match</c> arrived without it (428).</summary>
public sealed class IfMatchRequiredException(string resource = "resource")
    : ApiException(ApiError.IfMatchRequired, $"An If-Match revision is required for this {resource}.");

/// <summary>The supplied revision no longer matches the stored revision (409).</summary>
public sealed class RevisionConflictException(long actualRevision)
    : ApiException(ApiError.RevisionConflict, "The supplied revision is stale.")
{
    /// <summary>The revision actually stored. The caller's stale value is never echoed.</summary>
    public long ActualRevision { get; } = actualRevision;
}

/// <summary>The request body exceeded the accepted size (413).</summary>
public sealed class PayloadTooLargeException(long limitBytes)
    : ApiException(ApiError.PayloadTooLarge, $"The request body exceeds the {limitBytes} byte limit.");

/// <summary>A dependency could not serve the request right now (503).</summary>
public sealed class DependencyUnavailableException(string dependency, string? reason = null)
    : ApiException(ApiError.DatabaseUnavailable, $"The {dependency} dependency is unavailable. {reason}".TrimEnd());

/// <summary>A dependency did not answer in time (504).</summary>
public sealed class DependencyTimeoutException(string dependency, TimeSpan timeout)
    : ApiException(ApiError.DependencyTimeout, $"The {dependency} dependency did not answer within {timeout.TotalSeconds:0.##}s.");

/// <summary>
/// An unexpected server-side failure (500). The message stays authored here; the middleware adds
/// only the trace id, never the exception text of a provider.
/// </summary>
public sealed class InternalFailureException(string message)
    : ApiException(ApiError.InternalError, message);
