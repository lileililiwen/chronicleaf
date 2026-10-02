using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Access;

/// <summary>
/// The access package's envelope codes. They extend the shared envelope rather than replacing it:
/// the body shape stays <c>{code,message,traceId,fieldErrors?}</c> and the messages are authored
/// here, never taken from a token, a cookie or a database message.
/// </summary>
public static class AccessError
{
    /// <summary>A private route was requested without a usable owner session (401).</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>A login presented a token this installation does not accept (401).</summary>
    public const string InvalidCredentials = "invalid_credentials";

    /// <summary>A mutation's <c>Origin</c> is not the configured public origin (403).</summary>
    public const string OriginMismatch = "origin_mismatch";

    /// <summary>A mutation carried no usable per-session antiforgery token (403).</summary>
    public const string AntiforgeryInvalid = "antiforgery_invalid";

    /// <summary>Too many failed logins inside the throttle window (429).</summary>
    public const string TooManyRequests = "too_many_requests";

    /// <summary>The installation row this build needs does not exist yet (503).</summary>
    public const string InstallationMissing = "installation_missing";
}

/// <summary>
/// A private route was requested without a usable owner session. The message is the same for a
/// missing cookie, an unknown session, a revoked session and an expired one, so it discloses
/// nothing about the session that was or was not presented.
/// </summary>
public sealed class UnauthenticatedException()
    : ApiException(AccessError.Unauthenticated, "This request requires an authenticated owner session.");

/// <summary>
/// A presented setup token was not accepted. One generic failure covers a wrong token, a malformed
/// token and a rotated one: the caller cannot tell which, and neither can a log reader of a
/// captured request or response.
/// </summary>
public sealed class InvalidCredentialsException()
    : ApiException(AccessError.InvalidCredentials, "The access token was not accepted.");

/// <summary>
/// A mutation arrived from an origin other than the configured public origin. No write has
/// happened at the moment this is raised.
/// </summary>
public sealed class OriginMismatchException()
    : ApiException(AccessError.OriginMismatch, "This mutation was not sent from the configured public origin.");

/// <summary>
/// A mutation carried a missing, malformed or foreign antiforgery token. The value itself is
/// never echoed back, so a caller cannot use the error as an oracle for guessing it.
/// </summary>
public sealed class AntiforgeryFailedException()
    : ApiException(AccessError.AntiforgeryInvalid, "This mutation did not carry a valid antiforgery token.");

/// <summary>
/// The client is inside its login throttle window. <see cref="RetryAfterSeconds"/> is derived from
/// the oldest counted failure, so it tells the caller when the next attempt can succeed.
/// </summary>
public sealed class TooManyRequestsException(int retryAfterSeconds)
    : ApiException(
        AccessError.TooManyRequests,
        "Too many failed login attempts. Try again after the cooldown.")
{
    /// <summary>Seconds until the oldest counted failure leaves the window. Never negative.</summary>
    public int RetryAfterSeconds { get; } = Math.Max(1, retryAfterSeconds);
}

/// <summary>
/// The singleton installation row does not exist, so this deployment has no owner identity to
/// bind a session to. It is an operator problem: the installation is provisioned explicitly, and
/// this failure never masquerades as a rejected token.
/// </summary>
public sealed class InstallationNotProvisionedException()
    : ApiException(
        AccessError.InstallationMissing,
        "This Chronicleaf installation has not been provisioned yet.");
