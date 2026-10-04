using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.AI;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Secrets;
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

            // BACKGROUND-JOBS-001/002. Written here for the same reason as the refusals above: the
            // feature package raises them and this middleware is still the only place an envelope is
            // written. The messages are authored in the operations package and name nothing about any
            // target, provider or stored error code.
            case OperationNotFoundException operationMissing:
                return (
                    Envelope.Create(OperationError.NotFound, operationMissing.Message, traceId),
                    LogLevel.Information);

            case OperationIdempotencyConflictException idempotencyConflict:
                return (
                    Envelope.Create(
                        OperationError.IdempotencyConflict,
                        idempotencyConflict.Message,
                        traceId,
                        new Dictionary<string, string[]>
                        {
                            ["Idempotency-Key"] = ["this key is already bound to a different request"],
                        }),
                    LogLevel.Information);

            case OperationNotCancellableException notCancellable:
                return (
                    Envelope.Create(
                        OperationError.NotCancellable,
                        notCancellable.Message,
                        traceId,
                        new Dictionary<string, string[]>
                        {
                            ["status"] = [Core.OperationStatuses.ToWire(notCancellable.Status)],
                        }),
                    LogLevel.Information);

            // LOCAL-MEDIA-STORAGE-001/002. Written here for the same reason as the refusals above: the
            // feature package raises them and this middleware is still the only place an envelope is
            // written. The order matters - MediaOutsideRootException derives from the not-found, and
            // both produce the identical envelope on purpose, so a caller cannot tell a refused key
            // from an absent record. Every message is authored in the media package and names no
            // provider, no key, no path and no filesystem location.
            case MediaOutsideRootException:
            case MediaNotFoundException:
                return (
                    Envelope.Create(MediaError.NotFound, "No such media.", traceId),
                    LogLevel.Information);

            case UnsupportedMediaTypeException unsupportedType:
                return (
                    Envelope.Create(MediaError.UnsupportedMediaType, unsupportedType.Message, traceId),
                    LogLevel.Information);

            case MediaImageTooLargeException imageTooLarge:
                return (
                    Envelope.Create(
                        MediaError.ImageTooLarge,
                        imageTooLarge.Message,
                        traceId,
                        imageTooLarge.FieldErrors),
                    LogLevel.Information);

            case MediaTooLargeException mediaTooLarge:
                return (
                    Envelope.Create(MediaError.PayloadTooLarge, mediaTooLarge.Message, traceId),
                    LogLevel.Information);

            case MediaInUseException inUse:
                return (Envelope.Create(MediaError.MediaInUse, inUse.Message, traceId), LogLevel.Information);

            case MediaStorageUnavailableException storageUnavailable:
                return (
                    Envelope.Create(MediaError.StorageUnavailable, storageUnavailable.Message, traceId),
                    LogLevel.Warning);

            // CREDENTIAL-VAULT-001/002. Written here for the same reason as every other feature
            // package's refusals: the vault raises them and this middleware is still the only place an
            // envelope is written. A 404 for a credential that is not this owner's is the already
            // delivered not-found, so a caller cannot probe another installation by comparing codes.
            // The two 503s say the operator's key is unusable and that stored bytes no longer
            // authenticate; both messages are authored in the secrets package, name no key file, no
            // path, no provider and no value, and are logged as warnings because they are conditions
            // an operator has to fix rather than bugs in the request.
            case CredentialNotFoundException credentialNotFound:
                return (
                    Envelope.Create(Envelope.NotFound, credentialNotFound.Message, traceId),
                    LogLevel.Information);

            case SecretVaultUnavailableException vaultUnavailable:
                return (
                    Envelope.Create(CredentialVaultError.VaultUnavailable, vaultUnavailable.Message, traceId),
                    LogLevel.Warning);

            case CredentialUndecryptableException undecryptable:
                return (
                    Envelope.Create(CredentialVaultError.Undecryptable, undecryptable.Message, traceId),
                    LogLevel.Warning);

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

            // LLM-PROVIDER-CONTRACT-001/002. Written here for the same reason as every other feature
            // package's refusals: this package raises them and this middleware is still the only place
            // an envelope is written. The messages are authored in the AI package and name no
            // provider, no model, no credential, no key and no endpoint - a provider's own error body,
            // status line and request id stay inside the adapter, and this middleware would otherwise
            // be the place they leaked. GenerationFailedException carries the code the adapter
            // reported, which is the one point in the request path where a provider outcome is named;
            // it is named as a closed-set code and never as provider prose.
            case ConsentStaleException stale:
                return (Envelope.Create(AiError.ConsentStale, stale.Message, traceId), LogLevel.Information);

            case GenerationTimeoutException generationTimedOut:
                return (Envelope.Create(AiError.GenerationTimeout, generationTimedOut.Message, traceId), LogLevel.Warning);

            case ProviderNotInstalledException notInstalled:
                return (Envelope.Create(AiError.ProviderNotInstalled, notInstalled.Message, traceId), LogLevel.Information);

            case ModelNotSupportedException unsupportedModel:
                return (Envelope.Create(AiError.ModelNotSupported, unsupportedModel.Message, traceId), LogLevel.Information);

            case GenerationPreviewConflictException previewConflict:
                return (Envelope.Create(AiError.PreviewConflict, previewConflict.Message, traceId), LogLevel.Information);

            case GenerationFailedException failed:
                // A throttle and a rejected credential are the provider's own answers passed through
                // rather than conditions of this server, so they are logged as warnings - something an
                // owner has to act on - and never at error level.
                return (
                    Envelope.Create(failed.Code, failed.Message, traceId),
                    failed.Code is AiError.RateLimited or AiError.CredentialRejected
                        ? LogLevel.Warning
                        : LogLevel.Information);

            // GITHUB-EVIDENCE-001/002. Written here for the same reason as the refusals above: this
            // feature package raises them, and this middleware is still the only place an envelope is
            // written. The messages are authored in the github-evidence package and name no host, no
            // response body and no token - a repository that cannot be read says exactly that, because
            // GitHub does not distinguish a private repository from one that does not exist and this
            // package must not distinguish them either.
            case GitHubRepositoryUnavailableException repositoryUnavailable:
                return (
                    Envelope.Create(GitHubError.RepositoryUnavailable, repositoryUnavailable.Message, traceId),
                    LogLevel.Information);

            case GitHubCredentialRejectedException credentialRejected:
                // Something the owner has to act on, and never an error of this server's.
                return (
                    Envelope.Create(GitHubError.CredentialRejected, credentialRejected.Message, traceId),
                    LogLevel.Warning);

            case GitHubConnectionMissingException connectionMissing:
                return (
                    Envelope.Create(GitHubError.ConnectionMissing, connectionMissing.Message, traceId),
                    LogLevel.Information);

            case GitHubUnavailableException githubUnavailable:
                return (
                    Envelope.Create(GitHubError.Unreachable, githubUnavailable.Message, traceId),
                    LogLevel.Warning);

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
