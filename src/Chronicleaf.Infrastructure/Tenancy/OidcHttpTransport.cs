using System.Net;
using System.Text.Json;
using Chronicleaf.Application.Tenancy;

namespace Chronicleaf.Infrastructure.Tenancy;

/// <summary>
/// The real OIDC transport: three HTTPS resources of the configured issuer, with redirects
/// refused and no cookies (CLOUD-TENANT-BOUNDARY-001).
/// </summary>
/// <remarks>
/// Everything above the socket runs in every scenario: the caller still looks its state up,
/// still compares the issuer and audience exactly, still checks the lifetime and nonce, and
/// still verifies the signature. The handler below is the only thing a test stands in for, and
/// it answers the same three resources from scripted bytes.
/// </remarks>
public sealed class OidcHttpTransport(HttpClient http) : IOidcTransport
{
    /// <summary>How long one OIDC request may take before the login refuses on it.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The real transport handler: no automatic redirects and no cookies. Returns a new
    /// instance, so a test can hold one and read its settings rather than inferring them from
    /// a call it made.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = true,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = Timeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public async Task<OidcDiscovery> GetDiscoveryAsync(string issuer, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);

        var document = issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        using var response = await http.GetAsync(document, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OidcLoginFailedException();
        }

        using var body = JsonDocument.Parse(await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false));

        var tokenEndpoint = ReadEndpoint(body, "token_endpoint");
        var jwksUri = ReadEndpoint(body, "jwks_uri");

        if (tokenEndpoint is null || jwksUri is null)
        {
            throw new OidcLoginFailedException();
        }

        return new OidcDiscovery(tokenEndpoint, jwksUri);
    }

    public async Task<string> GetKeySetAsync(string jwksUri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jwksUri);
        RequireUsableEndpoint(jwksUri);

        using var response = await http.GetAsync(jwksUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OidcLoginFailedException();
        }

        return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OidcTokenResponse> ExchangeCodeAsync(
        string tokenEndpoint,
        string code,
        string codeVerifier,
        string clientId,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenEndpoint);
        RequireUsableEndpoint(tokenEndpoint);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
        });

        using var response = await http.PostAsync(tokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OidcLoginFailedException();
        }

        using var body = JsonDocument.Parse(await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false));
        if (!body.RootElement.TryGetProperty("id_token", out var idToken)
            || idToken.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idToken.GetString()))
        {
            throw new OidcLoginFailedException();
        }

        return new OidcTokenResponse(idToken.GetString()!);
    }

    private static string? ReadEndpoint(JsonDocument body, string name)
    {
        if (!body.RootElement.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var endpoint = value.GetString();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        try
        {
            RequireUsableEndpoint(endpoint);
            return endpoint;
        }
        catch (OidcLoginFailedException)
        {
            return null;
        }
    }

    /// <summary>
    /// An OIDC endpoint is this site's own HTTPS issuer, or a loopback address a test controls.
    /// Anything else - plain HTTP to a remote host, a non-HTTP scheme - is refused before a byte
    /// is sent, so a misconfigured issuer cannot turn the exchange into cleartext.
    /// </summary>
    private static void RequireUsableEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new OidcLoginFailedException();
        }

        if (parsed.Scheme == Uri.UriSchemeHttp && !parsed.IsLoopback)
        {
            throw new OidcLoginFailedException();
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new OidcLoginFailedException();
        }
    }
}
