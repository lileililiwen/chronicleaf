using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
// `ApiError` also names the runtime shell's compact not_found/web_assets_unavailable record in
// namespace Chronicleaf.Api, which wins the lookup from inside this namespace. The alias keeps the
// envelope type unambiguous: the shell record is a different, shipped contract.
using Envelope = Chronicleaf.Application.Common.ApiError;

namespace Chronicleaf.Api.Common;

/// <summary>
/// Writes the repository's single error envelope, <c>{code,message,traceId,fieldErrors?}</c>,
/// for the failures this package owns.
/// </summary>
/// <remarks>
/// This middleware only ever translates failures raised by the application layer. The responses
/// written directly by the runtime shell - <c>not_found</c> and <c>web_assets_unavailable</c> -
/// keep their shipped body, so adding the envelope does not change a contracted response.
/// Every message is authored in the application layer: a provider response body, a token, a
/// connection string or a host path never reaches a client, and a request is identified by the
/// server-generated <c>traceId</c> rather than by anything the caller sent.
/// </remarks>
public sealed class ApiErrorMiddleware(
    RequestDelegate next,
    ILogger<ApiErrorMiddleware> logger)
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await WriteAsync(context, exception).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(HttpContext context, Exception exception)
    {
        if (context.Response.HasStarted)
        {
            logger.LogError(
                exception,
                "Unhandled failure after the response started for {Method} {Path}.",
                context.Request.Method,
                context.Request.Path);

            throw exception;
        }

        var (error, logLevel) = Translate(exception, context);

        logger.Log(
            logLevel,
            exception,
            "{Code} for {Method} {Path}.",
            error.Code,
            context.Request.Method,
            context.Request.Path);

        context.Response.Clear();
        context.Response.StatusCode = ApiErrorStatus.ForCode(error.Code);
        context.Response.ContentType = "application/json; charset=utf-8";

        // A throttled client is told when it may try again, derived from its own oldest counted
        // failure. The header carries a duration and nothing else: no address, no attempt count
        // and no token. It is written after Clear(), which is what drops previously set headers.
        if (exception is TooManyRequestsException throttled)
        {
            context.Response.Headers.RetryAfter = throttled.RetryAfterSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        await context.Response.WriteAsync(error.ToJson(), context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a failure to an envelope. Only exceptions this repository raises carry a client-safe
    /// message; anything else becomes an opaque <c>internal_error</c> that keeps the provider's
    /// own text in the log, where it belongs.
    /// </summary>
    internal static (Envelope Error, LogLevel LogLevel) Translate(Exception exception, HttpContext context)
    {
        var traceId = context.TraceIdentifier;

        switch (exception)
        {
            // COMMUNITY-ACCESS-001/002. The four refusals below are written here so the access
            // package never writes an envelope of its own; each message is generic by design, so
            // none of them discloses whether a token, a session or an origin exists.
            case UnauthenticatedException unauthenticated:
                return (Envelope.Create(AccessError.Unauthenticated, unauthenticated.Message, traceId), LogLevel.Information);

            case InvalidCredentialsException invalidCredentials:
                return (Envelope.Create(AccessError.InvalidCredentials, invalidCredentials.Message, traceId), LogLevel.Information);

            case OriginMismatchException originMismatch:
                return (Envelope.Create(AccessError.OriginMismatch, originMismatch.Message, traceId), LogLevel.Information);

            case AntiforgeryFailedException antiforgery:
                return (Envelope.Create(AccessError.AntiforgeryInvalid, antiforgery.Message, traceId), LogLevel.Information);

            case TooManyRequestsException tooManyRequests:
                return (Envelope.Create(AccessError.TooManyRequests, tooManyRequests.Message, traceId), LogLevel.Information);

            case InstallationNotProvisionedException notProvisioned:
                return (Envelope.Create(AccessError.InstallationMissing, notProvisioned.Message, traceId), LogLevel.Warning);

            // PROJECTS-AND-MILESTONES-002. Written here for the same reason the access refusals are:
            // the feature package raises them, and this middleware is still the only place an
            // envelope is written.
            case ResourceNotFoundException missing:
                return (Envelope.Create(MilestoneError.NotFound, missing.Message, traceId), LogLevel.Information);

            case ProjectArchivedException archived:
                return (Envelope.Create(MilestoneError.ProjectArchived, archived.Message, traceId), LogLevel.Information);

            case MalformedJsonException malformed:
                return (Envelope.Create(Envelope.MalformedJson, malformed.Message, traceId), LogLevel.Information);

            case ValidationFailedException validation:
                return (
                    Envelope.Create(Envelope.ValidationFailed, validation.Message, traceId, validation.FieldErrors),
                    LogLevel.Information);

            case IfMatchRequiredException ifMatch:
                return (Envelope.Create(Envelope.IfMatchRequired, ifMatch.Message, traceId), LogLevel.Information);

            case RevisionConflictException revision:
                return (
                    Envelope.Create(
                        Envelope.RevisionConflict,
                        revision.Message,
                        traceId,
                        new Dictionary<string, string[]>
                        {
                            ["If-Match"] = [$"the stored revision is {revision.ActualRevision}"],
                        }),
                    LogLevel.Information);

            case PayloadTooLargeException oversized:
                return (Envelope.Create(Envelope.PayloadTooLarge, oversized.Message, traceId), LogLevel.Information);

            case DependencyUnavailableException unavailable:
                return (
                    Envelope.Create(Envelope.DatabaseUnavailable, unavailable.Message, traceId),
                    LogLevel.Warning);

            case DependencyTimeoutException timedOut:
                return (
                    Envelope.Create(Envelope.DependencyTimeout, timedOut.Message, traceId),
                    LogLevel.Warning);

            case InternalFailureException internalFailure:
                return (
                    Envelope.Create(Envelope.InternalError, internalFailure.Message, traceId),
                    LogLevel.Error);

            case BadHttpRequestException badRequest when badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge:
                return (
                    Envelope.Create(Envelope.PayloadTooLarge, "The request body is too large.", traceId),
                    LogLevel.Information);

            case BadHttpRequestException:
                return (
                    Envelope.Create(Envelope.MalformedJson, "The request could not be read.", traceId),
                    LogLevel.Information);

            // JSON this runtime reads itself - a request body a handler deserializes, a stored
            // payload a consumer parses - is reported with the same 400 envelope code.
            case JsonException:
                return (
                    Envelope.Create(Envelope.MalformedJson, "The request body is not valid JSON.", traceId),
                    LogLevel.Information);

            default:
                return (
                    Envelope.Create(
                        Envelope.InternalError,
                        "The request could not be completed.",
                        traceId),
                    LogLevel.Error);
        }
    }
}
