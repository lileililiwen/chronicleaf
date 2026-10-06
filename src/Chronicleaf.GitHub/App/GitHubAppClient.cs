using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chronicleaf.GitHub.App;

/// <summary>
/// What GitHub answered about one App installation.
/// </summary>
/// <param name="InstallationId">GitHub's numeric identity for the installation.</param>
/// <param name="AccountLogin">The account or organisation the App was installed on, as GitHub spells it.</param>
/// <param name="RepositorySelection">Whether the installation covers every repository or a selection.</param>
/// <param name="Suspended">True when GitHub reports the installation suspended.</param>
public sealed record GitHubAppInstallationInfo(
    long InstallationId,
    string AccountLogin,
    string RepositorySelection,
    bool Suspended);

/// <summary>
/// One repository as GitHub reports it.
/// </summary>
/// <param name="RepositoryId">GitHub's numeric identity for the repository.</param>
/// <param name="Owner">The account or organisation, as GitHub spells it.</param>
/// <param name="Name">The repository name, as GitHub spells it.</param>
/// <param name="DefaultBranch">The default branch GitHub reports, when it reports one.</param>
/// <param name="Private">True when the repository is private.</param>
public sealed record GitHubAppRepositoryInfo(
    long RepositoryId,
    string Owner,
    string Name,
    string? DefaultBranch,
    bool Private);

/// <summary>
/// An installation token GitHub minted, and when it stops working.
/// </summary>
/// <param name="Token">The bearer token. Held in memory only, never stored.</param>
/// <param name="ExpiresAt">When GitHub says the token expires.</param>
public sealed record GitHubAppToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// What one App transport call produced. Every failure is a value, never an exception carrying a
/// body: a response body may hold a customer's name and occasionally a key, so it is disposed
/// unread in every branch.
/// </summary>
public enum GitHubAppOutcome
{
    Ok,
    NotFound,
    Unauthorized,
    RateLimited,
    Unavailable,
}

/// <summary>
/// The read-only GitHub App transport (GITHUB-APP-CONNECTION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport is injected, and this class has no idea how it is built.</b> It takes an
/// <see cref="HttpClient"/> and mints no credential of its own: the JWT is built from a PEM the
/// caller supplies per call, and the installation token it returns is held by the caller.
/// </para>
/// <para>
/// <b>No write is possible from here.</b> The only POST is the token mint, which creates a
/// credential rather than changing a repository. There is no method that changes an installation,
/// a repository, an issue or a pull request.
/// </para>
/// <para>
/// <b>A token is minted for exactly one repository.</b> The mint names <c>repository_ids</c> with a
/// single entry, so a token that escapes its connection cannot read the rest of the installation.
/// The permission set is the App's own read-only registration; this transport names none, so it
/// cannot widen one.
/// </para>
/// </remarks>
public sealed class GitHubAppClient
{
    /// <summary>The host this client is pinned to. Requests are built as relative paths.</summary>
    public const string Host = "api.github.com";

    /// <summary>The API version this client speaks, sent as a header rather than assumed.</summary>
    public const string ApiVersion = "2022-11-28";

    /// <summary>The user agent GitHub requires, which is never a secret and never a token.</summary>
    public const string UserAgent = "Chronicleaf-GitHubApp/1.0";

    /// <summary>How long a minted JWT may live. GitHub refuses anything past ten minutes.</summary>
    public static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(9);

    private readonly HttpClient http;
    private readonly TimeProvider clock;

    public GitHubAppClient(HttpClient http, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        this.http = http;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Builds a signed App JWT from a PEM private key. BCL only: no JWT package, no key package.
    /// </summary>
    /// <param name="appId">The App's numeric identity, as the <c>iss</c> claim.</param>
    /// <param name="privateKeyPem">The App's PEM private key. Used for signing and nothing else.</param>
    /// <returns>The compact JWT, valid for <see cref="JwtLifetime"/>.</returns>
    public string CreateAppJwt(long appId, string privateKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        var now = clock.GetUtcNow();
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var payloadJson = "{\"iat\":" + now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"exp\":" + now.Add(JwtLifetime).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"iss\":\"" + appId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"}";
        var payload = Base64Url(Encoding.UTF8.GetBytes(payloadJson));

        var unsigned = $"{header}.{payload}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var signature = Base64Url(rsa.SignData(
            Encoding.ASCII.GetBytes(unsigned),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        return $"{unsigned}.{signature}";
    }

    /// <summary>Reads one installation as the App itself.</summary>
    public async Task<(GitHubAppOutcome Outcome, GitHubAppInstallationInfo? Installation)> GetInstallationAsync(
        string appJwt,
        long installationId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/app/installations/{installationId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        ApplyCommon(request, $"Bearer {appJwt}");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return (GitHubAppOutcome.Unavailable, null);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return (GitHubAppOutcome.NotFound, null);
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return (GitHubAppOutcome.Unauthorized, null);
                case (HttpStatusCode)429:
                    return (GitHubAppOutcome.RateLimited, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (GitHubAppOutcome.Unavailable, null);
            }

            try
            {
                using var document = await JsonDocument
                    .ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var root = document.RootElement;
                var account = root.TryGetProperty("account", out var accountElement)
                    && accountElement.TryGetProperty("login", out var loginElement)
                    ? loginElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(account))
                {
                    return (GitHubAppOutcome.Unavailable, null);
                }

                var selection = root.TryGetProperty("repository_selection", out var selectionElement)
                    ? selectionElement.GetString()
                    : null;

                var suspended = root.TryGetProperty("suspended_by", out var suspendedElement)
                    && suspendedElement.ValueKind != JsonValueKind.Null;

                return (GitHubAppOutcome.Ok, new GitHubAppInstallationInfo(
                    installationId,
                    account!,
                    string.IsNullOrWhiteSpace(selection) ? "selected" : selection!,
                    suspended));
            }
            catch (JsonException)
            {
                return (GitHubAppOutcome.Unavailable, null);
            }
        }
    }

    /// <summary>
    /// Mints an installation token restricted to one repository.
    /// </summary>
    /// <remarks>
    /// The request names exactly one repository id and no permission. A permission set travels with
    /// the App's registration, which this transport cannot widen: the only scoping this call adds
    /// is the narrowing to one repository.
    /// </remarks>
    public async Task<(GitHubAppOutcome Outcome, GitHubAppToken? Token, string RequestBody)> CreateInstallationTokenAsync(
        string appJwt,
        long installationId,
        long repositoryId,
        CancellationToken cancellationToken = default)
    {
        // Built as text first so a test can read exactly what narrowing this transport asked for:
        // one repository, no permission, and nothing else.
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["repository_ids"] = new[]
            {
                repositoryId,
            },
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/app/installations/{installationId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/access_tokens")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        ApplyCommon(request, $"Bearer {appJwt}");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return (GitHubAppOutcome.Unavailable, null, body);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return (GitHubAppOutcome.NotFound, null, body);
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return (GitHubAppOutcome.Unauthorized, null, body);
                case (HttpStatusCode)429:
                    return (GitHubAppOutcome.RateLimited, null, body);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (GitHubAppOutcome.Unavailable, null, body);
            }

            try
            {
                using var document = await JsonDocument
                    .ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var root = document.RootElement;
                var token = root.TryGetProperty("token", out var tokenElement)
                    ? tokenElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(token))
                {
                    return (GitHubAppOutcome.Unavailable, null, body);
                }

                var expires = root.TryGetProperty("expires_at", out var expiresElement)
                    && expiresElement.GetString() is { } expiresRaw
                    && DateTimeOffset.TryParse(expiresRaw, out var parsed)
                    ? parsed
                    : clock.GetUtcNow().AddMinutes(55);

                return (GitHubAppOutcome.Ok, new GitHubAppToken(token!, expires), body);
            }
            catch (JsonException)
            {
                return (GitHubAppOutcome.Unavailable, null, body);
            }
        }
    }

    /// <summary>Reads one repository through an installation token.</summary>
    public async Task<(GitHubAppOutcome Outcome, GitHubAppRepositoryInfo? Repository)> GetRepositoryAsync(
        string installationToken,
        long repositoryId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/repositories/{repositoryId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        ApplyCommon(request, $"Bearer {installationToken}");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return (GitHubAppOutcome.Unavailable, null);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A repository outside the installation's selection and a repository that does not
                // exist are the same answer from GitHub, and they stay the same answer here.
                return (GitHubAppOutcome.NotFound, null);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (GitHubAppOutcome.Unauthorized, null);
            }

            if ((int)response.StatusCode == 429)
            {
                return (GitHubAppOutcome.RateLimited, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (GitHubAppOutcome.Unavailable, null);
            }

            try
            {
                using var document = await JsonDocument
                    .ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var root = document.RootElement;
                var id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var parsedId)
                    ? parsedId
                    : (long?)null;
                var fullName = root.TryGetProperty("full_name", out var nameElement)
                    ? nameElement.GetString()
                    : null;

                if (id is null || string.IsNullOrWhiteSpace(fullName))
                {
                    return (GitHubAppOutcome.Unavailable, null);
                }

                var slash = fullName!.IndexOf('/');
                if (slash < 1 || slash == fullName.Length - 1)
                {
                    return (GitHubAppOutcome.Unavailable, null);
                }

                var isPrivate = root.TryGetProperty("private", out var privateElement)
                    && privateElement.ValueKind == JsonValueKind.True;
                var branch = root.TryGetProperty("default_branch", out var branchElement)
                    ? branchElement.GetString()
                    : null;

                return (GitHubAppOutcome.Ok, new GitHubAppRepositoryInfo(
                    id.Value,
                    fullName[..slash],
                    fullName[(slash + 1)..],
                    string.IsNullOrWhiteSpace(branch) ? null : branch,
                    isPrivate));
            }
            catch (JsonException)
            {
                return (GitHubAppOutcome.Unavailable, null);
            }
        }
    }

    private void ApplyCommon(HttpRequestMessage request, string authorization)
    {
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
    }

    /// <summary>
    /// Sends one request. A redirect is never followed: an Authorization header is attached, and a
    /// redirect is the one construct that could carry it somewhere else. Any 3xx is therefore a
    /// transport failure, reported as unavailable.
    /// </summary>
    /// <returns>The response, or <c>null</c> when no answer should be classified further.</returns>
    private async Task<HttpResponseMessage?> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            return null;
        }

        return response;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
