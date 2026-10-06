using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chronicleaf.Publishers.Ghost;

/// <summary>
/// The Ghost Admin API token this transport signs per call (PUBLISHER-GHOST-ADAPTER-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Signed, never stored.</b> The token is built from the leased integration key inside the
/// transport call and travels in the one <c>Authorization: Ghost</c> header: it reaches no
/// row, no log line and no exception, and the key it was signed with is returned to the
/// vault when the call returns. The claims follow the provider's versioned reference:
/// HS256 with <c>kid</c> naming the integration id, <c>iat</c> now, <c>exp</c> five minutes
/// later, <c>aud</c> <c>/admin/</c>, signed over the hex-decoded secret.
/// </para>
/// <para>
/// BCL only on purpose: the header, the claims and the HMAC are a few dozen lines, and a
/// JWT package here would be a dependency that can do far more than sign one audience.
/// </para>
/// </remarks>
internal static class GhostJwt
{
    /// <summary>The audience every Admin token is issued for.</summary>
    public const string Audience = "/admin/";

    /// <summary>How long one token lives: five minutes from issue.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Signs one Admin token for the call. The key id and secret arrive from the leased
    /// integration key; the instant arrives from the transport's clock so a scenario can fix it.
    /// </summary>
    public static string CreateToken(string keyId, byte[] keyBytes, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(keyBytes);

        if (keyBytes.Length == 0)
        {
            throw new ArgumentException("The signing key is empty.", nameof(keyBytes));
        }

        var issued = now.ToUnixTimeSeconds();
        var header = Base64Url(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT", kid = keyId })));
        var payload = Base64Url(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                iat = issued,
                exp = issued + (long)Lifetime.TotalSeconds,
                aud = Audience,
            })));

        var unsigned = string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{header}.{payload}");

        using var signer = new HMACSHA256(keyBytes);
        var signature = Base64Url(signer.ComputeHash(Encoding.ASCII.GetBytes(unsigned)));

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{unsigned}.{signature}");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
