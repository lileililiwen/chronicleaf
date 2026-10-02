using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Application.Access;

/// <summary>
/// The operator configuration the access boundary reads, resolved once at start-up.
/// </summary>
/// <remarks>
/// Two settings are required, both supplied as environment variables at run time and neither
/// carried by the image:
/// <list type="bullet">
/// <item><description>
/// <c>Chronicleaf__AccessTokenHash</c> - the SHA-256 hex of a random setup token of at least
/// 32 random bytes. The plaintext token is never configured, never persisted and never logged;
/// only its hash is, and a hash alone cannot be used to log in.
/// </description></item>
/// <item><description>
/// <c>Chronicleaf__PublicOrigin</c> - the single origin, for example
/// <c>https://chronicleaf.example</c>, whose mutations this installation accepts.
/// </description></item>
/// </list>
/// An absent access token hash prevents start-up outside Development with a redacted message that
/// names the key and never its value. In Development the process starts, and no login can ever
/// succeed because there is no token to match: the boundary fails closed instead of inventing one.
/// </remarks>
public sealed record AccessSettings(string? AccessTokenHash, string? PublicOrigin, bool IsDevelopment)
{
    /// <summary>Configuration key; the environment variable form is <c>Chronicleaf__AccessTokenHash</c>.</summary>
    public const string AccessTokenHashSettingName = "Chronicleaf:AccessTokenHash";

    /// <summary>Configuration key; the environment variable form is <c>Chronicleaf__PublicOrigin</c>.</summary>
    public const string PublicOriginSettingName = "Chronicleaf:PublicOrigin";

    /// <summary>SHA-256 hex length, and therefore the length of a token of 32 random bytes.</summary>
    public const int TokenHashHexLength = 64;

    /// <summary>How long a session may sit unused before it stops being accepted.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(12);

    /// <summary>How long a session may exist in total, whatever the owner does with it.</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(7);

    /// <summary>How long failed logins are counted for one client.</summary>
    public static readonly TimeSpan LoginThrottleWindow = TimeSpan.FromMinutes(15);

    /// <summary>Failed logins one client may make inside <see cref="LoginThrottleWindow"/>.</summary>
    public const int LoginThrottleAllowance = 5;

    /// <summary>True when a usable access token hash was configured for this process.</summary>
    public bool HasAccessTokenHash => AccessTokenHashBytes is not null;

    /// <summary>
    /// The configured hash decoded to its 32 bytes, or <c>null</c> when none was configured. The
    /// bytes are compared in constant time and are never formatted into a message or a log.
    /// </summary>
    public byte[]? AccessTokenHashBytes { get; } = DecodeHash(AccessTokenHash);

    /// <summary>
    /// The single accepted mutation origin, normalized, or <c>null</c> when none is configured.
    /// A missing origin is not a start-up failure: every mutation then fails closed at the
    /// boundary with 403.
    /// </summary>
    public Uri? PublicOriginUri { get; } = NormalizeOrigin(PublicOrigin);

    /// <summary>
    /// Whether the session cookie may omit <c>Secure</c>. It may only when this process is an
    /// explicit Development environment pointed at a loopback origin; every other deployment,
    /// including a plain-HTTP host name, gets <c>Secure</c>.
    /// </summary>
    public bool AllowInsecureSessionCookie =>
        IsDevelopment && PublicOriginUri is { IsLoopback: true };

    /// <summary>
    /// What this installation remembers about the token it last started with: a SHA-256 hex of
    /// the configured token hash, or of the empty string when no hash is configured. Storing a
    /// fingerprint of a hash rather than the hash itself keeps the durable marker useless as a
    /// credential, and it still detects every change, including the removal of a configured hash.
    /// </summary>
    public string TokenHashFingerprint =>
        Sha256.Sha256Hex(AccessTokenHash ?? string.Empty);

    public static AccessSettings Read(IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new AccessSettings(
            Trimmed(configuration[AccessTokenHashSettingName]),
            Trimmed(configuration[PublicOriginSettingName]),
            IsDevelopmentEnvironment(environmentName));
    }

    /// <summary>
    /// Fails start-up when the access boundary cannot work. The message names the setting and the
    /// required shape; it never contains a configured value, because a hash and a connection
    /// string are both secrets.
    /// </summary>
    public static void EnsureUsable(AccessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.HasAccessTokenHash)
        {
            return;
        }

        if (settings.IsDevelopment)
        {
            // Documented Development behaviour: the process starts so the shell stays usable, but
            // no token is configured, so no presented token can ever match and every login is
            // refused with the generic invalid_credentials response. Nothing is weakened: the
            // boundary is closed, not open.
            return;
        }

        throw new InvalidOperationException(
            $"Configuration error: '{AccessTokenHashSettingName}' is required outside Development. " +
            $"Set it to the SHA-256 hex ({TokenHashHexLength} lower-case hex characters) of a random " +
            "setup token of at least 32 random bytes. The configured value is never logged. " +
            "Generate one with: openssl rand -hex 32 | xxd -r -p | sha256sum");
    }

    /// <summary>
    /// Constant-time comparison of a presented token against the configured hash. Both sides are
    /// always 32 bytes, so the comparison performs the same work for every input.
    /// </summary>
    public bool MatchesAccessToken(string presentedToken)
    {
        ArgumentNullException.ThrowIfNull(presentedToken);

        if (AccessTokenHashBytes is not { } expected || presentedToken.Length == 0)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, Sha256.Sha256HexBytes(presentedToken));
    }

    private static bool IsDevelopmentEnvironment(string? environmentName) =>
        string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static byte[]? DecodeHash(string? value)
    {
        // Exactly 64 hex characters, which is a SHA-256 digest and therefore 32 bytes. A value of
        // any other length or with a non-hex character is not a hash this build can use, and is
        // treated exactly like an absent one rather than being partially accepted.
        if (value is null || value.Length != TokenHashHexLength)
        {
            return null;
        }

        try
        {
            // Accepts either case; a hash is case-insensitive, so an operator who pasted an
            // upper-case digest still gets a working deployment.
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Normalizes a configured origin to the form a browser sends in its <c>Origin</c> header:
    /// lower-case scheme and host, the default port removed, and no path, query or fragment.
    /// </summary>
    private static Uri? NormalizeOrigin(string? value)
    {
        if (value is null
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || (parsed.AbsolutePath.Length > 0 && parsed.AbsolutePath != "/"))
        {
            return null;
        }

        return new UriBuilder(parsed.Scheme, parsed.Host, parsed.IsDefaultPort ? -1 : parsed.Port).Uri;
    }
}

/// <summary>
/// The one SHA-256 helper this boundary uses. Tokens are hashed and compared through it, so no
/// call site can accidentally compare or store a plaintext secret.
/// </summary>
public static class Sha256
{
    /// <summary>The lower-case hex SHA-256 of a UTF-8 secret: what a session row stores.</summary>
    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    /// <summary>The raw 32-byte SHA-256 of a UTF-8 secret.</summary>
    public static byte[] Sha256HexBytes(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));

    /// <summary>
    /// Compares two stored hashes without leaking, through timing, how many leading bytes
    /// matched. A length difference is a mismatch and is not compared byte by byte.
    /// </summary>
    public static bool FixedTimeEqualsHex(string left, string right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        if (left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }
}

/// <summary>
/// Generates the opaque secrets this boundary issues: 32 cryptographically random bytes rendered
/// as lower-case hex, so a value is safe in a cookie or a JSON field.
/// </summary>
public static class OpaqueSecret
{
    /// <summary>32 random bytes; the length the design fixes for session and antiforgery tokens.</summary>
    public const int TokenByteLength = 32;

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenByteLength));
}
