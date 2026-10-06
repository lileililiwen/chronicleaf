using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chronicleaf.Application.Tenancy;

/// <summary>
/// The wire boundary to the configured OIDC issuer (CLOUD-TENANT-BOUNDARY-001).
/// </summary>
/// <remarks>
/// The only thing a test may stand in for is the issuer itself, with a handler that answers the
/// three OIDC resources from scripted bytes: the discovery document, the key set and the token
/// endpoint. Everything above the socket - the state/nonce lookup, the issuer/audience/lifetime
/// checks and the signature verification - is production code and runs in every scenario.
/// </remarks>
public interface IOidcTransport
{
    /// <summary>Reads the issuer's discovery document: the token endpoint and the key-set URL.</summary>
    Task<OidcDiscovery> GetDiscoveryAsync(string issuer, CancellationToken cancellationToken = default);

    /// <summary>Reads the issuer's JSON key set.</summary>
    Task<string> GetKeySetAsync(string jwksUri, CancellationToken cancellationToken = default);

    /// <summary>Exchanges an authorization code for tokens, presenting the PKCE verifier.</summary>
    Task<OidcTokenResponse> ExchangeCodeAsync(
        string tokenEndpoint,
        string code,
        string codeVerifier,
        string clientId,
        string redirectUri,
        CancellationToken cancellationToken = default);
}

/// <summary>What the discovery document carries that this package acts on.</summary>
public sealed record OidcDiscovery(string TokenEndpoint, string JwksUri);

/// <summary>What the token endpoint answered: the ID token this package validates.</summary>
public sealed record OidcTokenResponse(string IdToken);

/// <summary>
/// The verified identity an ID token carries: the exact issuer and subject the user row is
/// keyed by (CLOUD-TENANT-BOUNDARY-001).
/// </summary>
public sealed record OidcIdentity(string Issuer, string Subject);

/// <summary>
/// Validates an ID token against the configured issuer, audience and stored nonce, and verifies
/// its RS256 signature against the issuer's key set (CLOUD-TENANT-BOUNDARY-001).
/// </summary>
/// <remarks>
/// <para>
/// BCL only, for the same reason the GitHub transport is: the check is parsing, one RSA
/// verification and five comparisons, and a dependency would move the trust without moving the
/// work. The algorithm is fixed to <c>RS256</c>: a token naming any other algorithm is refused
/// rather than negotiated.
/// </para>
/// <para>
/// Every failure is the same generic refusal. The token's own text never reaches a message, a
/// log line or an envelope: a mis-signed or foreign-issuer token is indistinguishable from an
/// expired one by design.
/// </para>
/// </remarks>
public static class OidcIdTokenValidator
{
    /// <summary>
    /// Validates <paramref name="idToken"/> and returns the identity it carries. Throws
    /// <see cref="OidcLoginFailedException"/> for every refusal.
    /// </summary>
    /// <param name="idToken">The compact JWT the token endpoint answered.</param>
    /// <param name="expectedIssuer">The configured issuer, normalized (no trailing slash).</param>
    /// <param name="expectedClientId">This site's client id: the required audience.</param>
    /// <param name="expectedNonceHash">The SHA-256 hex of the nonce the login started.</param>
    /// <param name="jwksJson">The issuer's key-set document.</param>
    /// <param name="now">The instant expiry is judged at.</param>
    public static OidcIdentity Validate(
        string? idToken,
        string expectedIssuer,
        string expectedClientId,
        string expectedNonceHash,
        string jwksJson,
        DateTimeOffset now)
    {
        try
        {
            return ValidateCore(idToken, expectedIssuer, expectedClientId, expectedNonceHash, jwksJson, now);
        }
        catch (OidcLoginFailedException)
        {
            throw;
        }
        catch (Exception)
        {
            // Parsing, key import and decoding failures are all the same refusal: the shape of a
            // malformed token must not decide the shape of the answer.
            throw new OidcLoginFailedException();
        }
    }

    private static OidcIdentity ValidateCore(
        string? idToken,
        string expectedIssuer,
        string expectedClientId,
        string expectedNonceHash,
        string jwksJson,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            throw new OidcLoginFailedException();
        }

        var parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            throw new OidcLoginFailedException();
        }

        using var header = JsonDocument.Parse(DecodePart(parts[0]));
        if (!header.RootElement.TryGetProperty("alg", out var alg)
            || alg.GetString() != "RS256"
            || !header.RootElement.TryGetProperty("kid", out var kid)
            || string.IsNullOrWhiteSpace(kid.GetString()))
        {
            throw new OidcLoginFailedException();
        }

        using var payload = JsonDocument.Parse(DecodePart(parts[1]));

        var issuer = ReadString(payload, "iss");
        if (!IssuersMatch(issuer, expectedIssuer))
        {
            throw new OidcLoginFailedException();
        }

        if (!AudienceMatches(payload, expectedClientId))
        {
            throw new OidcLoginFailedException();
        }

        var nowSeconds = now.ToUnixTimeSeconds();
        if (!payload.RootElement.TryGetProperty("exp", out var exp)
            || exp.GetInt64() <= nowSeconds)
        {
            throw new OidcLoginFailedException();
        }

        if (payload.RootElement.TryGetProperty("nbf", out var nbf)
            && nbf.GetInt64() > nowSeconds)
        {
            throw new OidcLoginFailedException();
        }

        var nonce = ReadString(payload, "nonce");
        if (string.IsNullOrEmpty(nonce)
            || !FixedTimeEqualsHex(expectedNonceHash, Sha256Hex(nonce)))
        {
            throw new OidcLoginFailedException();
        }

        var subject = ReadString(payload, "sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new OidcLoginFailedException();
        }

        VerifySignature(parts, kid.GetString()!, jwksJson);

        return new OidcIdentity(expectedIssuer, subject);
    }

    private static void VerifySignature(string[] parts, string kid, string jwksJson)
    {
        using var keySet = JsonDocument.Parse(jwksJson);
        if (!keySet.RootElement.TryGetProperty("keys", out var keys))
        {
            throw new OidcLoginFailedException();
        }

        foreach (var key in keys.EnumerateArray())
        {
            if (key.TryGetProperty("kid", out var keyId)
                && keyId.GetString() == kid
                && key.TryGetProperty("kty", out var kty)
                && kty.GetString() == "RSA"
                && key.TryGetProperty("n", out var modulus)
                && key.TryGetProperty("e", out var exponent))
            {
                using var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Base64UrlDecode(modulus.GetString()!),
                    Exponent = Base64UrlDecode(exponent.GetString()!),
                });

                var signedBytes = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
                var signature = Base64UrlDecode(parts[2]);
                var hash = SHA256.HashData(signedBytes);

                if (rsa.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    return;
                }

                throw new OidcLoginFailedException();
            }
        }

        throw new OidcLoginFailedException();
    }

    private static bool IssuersMatch(string? tokenIssuer, string expectedIssuer) =>
        tokenIssuer is not null
        && string.Equals(
            tokenIssuer.TrimEnd('/'),
            expectedIssuer.TrimEnd('/'),
            StringComparison.Ordinal);

    private static bool AudienceMatches(JsonDocument payload, string expectedClientId)
    {
        if (!payload.RootElement.TryGetProperty("aud", out var audience))
        {
            return false;
        }

        return audience.ValueKind switch
        {
            JsonValueKind.String => audience.GetString() == expectedClientId,
            JsonValueKind.Array => audience.EnumerateArray()
                .Any(entry => entry.ValueKind == JsonValueKind.String && entry.GetString() == expectedClientId),
            _ => false,
        };
    }

    private static string? ReadString(JsonDocument payload, string name) =>
        payload.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static byte[] DecodePart(string part) => Base64UrlDecode(part);

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder == 1)
        {
            throw new FormatException("Invalid base64url length.");
        }

        if (remainder != 0)
        {
            padded += new string('=', 4 - remainder);
        }

        return Convert.FromBase64String(padded);
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEqualsHex(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }
}
