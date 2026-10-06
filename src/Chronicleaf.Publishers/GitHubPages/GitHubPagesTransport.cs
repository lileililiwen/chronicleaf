using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Chronicleaf.GitHub.Evidence;

namespace Chronicleaf.Publishers.GitHubPages;

/// <summary>
/// A definitive upstream refusal: the branch ref cannot have moved because of it
/// (PUBLISHER-GITHUB-PAGES-ADAPTER-002).
/// </summary>
/// <remarks>
/// Raised for every answered failure - a refused credential, a missing repository, a moved
/// branch, a rejected body - and for every transport failure that happened before the one
/// request that publishes (the ref move). What it never covers is the ref move itself timing
/// out: that answer is unknown rather than failed, and the adapter raises the contract's own
/// timeout for it instead of this.
/// </remarks>
public sealed class GitHubPagesUpstreamException(string errorCode)
    : Exception("The GitHub Pages upstream refused the request before anything reachable changed.")
{
    /// <summary>One of <see cref="GitHubPagesError"/>: fixed words, never a provider body.</summary>
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// One branch ref, as read (PUBLISHER-GITHUB-PAGES-ADAPTER-001).
/// </summary>
/// <param name="Exists">False when the branch names nothing yet: the push creates it.</param>
/// <param name="Sha">The commit the branch points at, or null when it points nowhere.</param>
public sealed record GitBranchRef(bool Exists, string? Sha);

/// <summary>
/// The Git Data API calls a Pages push is made of, and nothing else
/// (PUBLISHER-GITHUB-PAGES-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport is injected, and this class has no idea how it is built.</b> It takes an
/// <see cref="HttpMessageHandler"/> and owns one <see cref="HttpClient"/> pinned to the API host
/// with redirects refused; the composition root decides the handler's lifetime. That is what lets
/// a test supply a scripted handler and see this class's real decisions - the classification of a
/// status, the refusal of a redirect, the conditional ref write - without a socket.
/// </para>
/// <para>
/// <b>Only the ref move publishes.</b> Blobs, trees and commit objects without a ref are
/// unreachable: no reader, no Pages build and no reconciler can find them. A transport failure
/// before the ref move is therefore a definitive failure, and only the ref move itself timing
/// out is ambiguous. The classification methods below are what makes that distinction exact:
/// every answered status becomes a <see cref="GitHubPagesUpstreamException"/>, while a lost
/// answer propagates untouched for the adapter to place by phase.
/// </para>
/// <para>
/// <b>The response body is untrusted input.</b> It is parsed with <see cref="JsonDocument"/>,
/// bounded before it leaves this class, and never repeated into an exception, a log line or a
/// result. A payload that is not the JSON this client understands is a failure, never a SHA.
/// </para>
/// </remarks>
public sealed class GitHubPagesTransport : IDisposable
{
    /// <summary>The host this transport is pinned to. Requests are built as relative paths.</summary>
    public const string Host = "api.github.com";

    /// <summary>The API version this transport speaks, sent as a header rather than assumed.</summary>
    public const string ApiVersion = "2022-11-28";

    /// <summary>The user agent GitHub requires, which is never a secret and never a token.</summary>
    public const string UserAgent = "Chronicleaf-Pages/1.0";

    /// <summary>How long one upstream request may take before the transport gives up on it.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>How large an upstream JSON answer may be before it is refused unread.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    private readonly HttpClient http;
    private bool disposed;

    public GitHubPagesTransport(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri($"https://{Host}"),
            Timeout = Timeout,
        };
    }

    /// <summary>
    /// The real transport handler: no automatic redirects, no cookies. A redirect that arrives
    /// anyway is answered as a refusal rather than a second hop, because the second hop is the
    /// one construct that could carry the leased credential to another host.
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

    /// <summary>
    /// Reads the configured repository. A 404 is the repository absent - or invisible to the
    /// leased credential - and both are the same refusal from the outside.
    /// </summary>
    /// <exception cref="GitHubPagesUpstreamException">The repository cannot be published to.</exception>
    public async Task ReadRepositoryAsync(
        RepoRef repository,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        using var response = await GetAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}",
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.OK)
        {
            response.Dispose();
            return;
        }

        throw Classify(response);
    }

    /// <summary>
    /// Reads the branch the push moves: its head commit, or missing when nothing names it yet.
    /// </summary>
    /// <exception cref="GitHubPagesUpstreamException">The ref cannot be read.</exception>
    public async Task<GitBranchRef> ReadRefAsync(
        RepoRef repository,
        string branch,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        using var response = await GetAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}/git/ref/heads/{Escape(branch)}",
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.OK)
        {
            var sha = await ReadShaAsync(response, "object", cancellationToken).ConfigureAwait(false);

            if (sha is null)
            {
                throw new GitHubPagesUpstreamException(GitHubPagesError.Unavailable);
            }

            return new GitBranchRef(Exists: true, sha);
        }

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return new GitBranchRef(Exists: false, Sha: null);
        }

        throw Classify(response);
    }

    /// <summary>Stores one file's bytes as a blob. Unreachable until a tree names it.</summary>
    /// <exception cref="GitHubPagesUpstreamException">The bytes were refused.</exception>
    public async Task<string> CreateBlobAsync(
        RepoRef repository,
        byte[] content,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(content);

        using var response = await PostAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}/git/blobs",
            JsonSerializer.Serialize(new
            {
                content = Convert.ToBase64String(content),
                encoding = "base64",
            }),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Created)
        {
            var sha = await ReadShaAsync(response, null, cancellationToken).ConfigureAwait(false);

            if (sha is not null)
            {
                return sha;
            }
        }
        else if (response.StatusCode == (HttpStatusCode)422)
        {
            throw new GitHubPagesUpstreamException(GitHubPagesError.Rejected);
        }

        throw Classify(response);
    }

    /// <summary>Stores one directory listing over an optional base. Unreachable until committed.</summary>
    /// <exception cref="GitHubPagesUpstreamException">The listing was refused.</exception>
    public async Task<string> CreateTreeAsync(
        RepoRef repository,
        string? baseSha,
        IReadOnlyList<(string Path, string BlobSha)> entries,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(entries);

        var ordered = entries
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(entry => new
            {
                path = entry.Path,
                mode = "100644",
                type = "blob",
                sha = entry.BlobSha,
            })
            .ToList();

        object body = baseSha is null
            ? new { tree = ordered }
            : new { tree = ordered, base_tree = baseSha };

        using var response = await PostAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}/git/trees",
            JsonSerializer.Serialize(body),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Created)
        {
            var sha = await ReadShaAsync(response, null, cancellationToken).ConfigureAwait(false);

            if (sha is not null)
            {
                return sha;
            }
        }
        else if (response.StatusCode == (HttpStatusCode)422)
        {
            throw new GitHubPagesUpstreamException(GitHubPagesError.Rejected);
        }

        throw Classify(response);
    }

    /// <summary>
    /// Stores one commit object. Its SHA is the planned SHA the ref move and the reconciler use:
    /// content-addressed, known before anything reachable changes.
    /// </summary>
    /// <exception cref="GitHubPagesUpstreamException">The commit was refused.</exception>
    public async Task<string> CreateCommitAsync(
        RepoRef repository,
        string message,
        string treeSha,
        IReadOnlyList<string> parents,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(treeSha);
        ArgumentNullException.ThrowIfNull(parents);

        using var response = await PostAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}/git/commits",
            JsonSerializer.Serialize(new
            {
                message,
                tree = treeSha,
                parents,
                author = new { name = GitHubPagesPublisher.CommitAuthorName, email = GitHubPagesPublisher.CommitAuthorEmail },
            }),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Created)
        {
            var sha = await ReadShaAsync(response, null, cancellationToken).ConfigureAwait(false);

            if (sha is not null)
            {
                return sha;
            }
        }
        else if (response.StatusCode == (HttpStatusCode)422)
        {
            throw new GitHubPagesUpstreamException(GitHubPagesError.Rejected);
        }

        throw Classify(response);
    }

    /// <summary>
    /// Moves the branch to the planned commit, conditionally: the write wins only when the branch
    /// still points where the base read found it. A moved branch is a refusal, never a force push.
    /// </summary>
    /// <returns>True when the branch now points at the planned commit.</returns>
    /// <exception cref="GitHubPagesUpstreamException">The move was refused.</exception>
    public async Task<bool> UpdateRefAsync(
        RepoRef repository,
        string branch,
        string commitSha,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);

        using var response = await PatchAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}/git/refs/heads/{Escape(branch)}",
            JsonSerializer.Serialize(new { sha = commitSha, force = false }),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.OK)
        {
            return true;
        }

        if (response.StatusCode == (HttpStatusCode)422)
        {
            // The conditional write lost: the branch moved under the base read. The dangling
            // commit is unreachable and no reader can see it, so this is failed, not unknown.
            throw new GitHubPagesUpstreamException(GitHubPagesError.UpstreamMoved);
        }

        throw Classify(response);
    }

    /// <summary>
    /// Names the planned commit on a branch that names nothing yet. A concurrent creation wins
    /// the same way a concurrent move does: as a refusal, never as an overwrite.
    /// </summary>
    /// <returns>True when the branch now points at the planned commit.</returns>
    /// <exception cref="GitHubPagesUpstreamException">The creation was refused.</exception>
    public async Task<bool> CreateRefAsync(
        RepoRef repository,
        string branch,
        string commitSha,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);

        using var response = await PostAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}/git/refs",
            JsonSerializer.Serialize(new
            {
                @ref = $"refs/heads/{branch}",
                sha = commitSha,
            }),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Created)
        {
            return true;
        }

        if (response.StatusCode == (HttpStatusCode)422)
        {
            // Somebody else named the branch between the read and this write. Their branch is
            // not this publish, and overwriting it is exactly what force=false exists to refuse.
            throw new GitHubPagesUpstreamException(GitHubPagesError.UpstreamMoved);
        }

        throw Classify(response);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        http.Dispose();
    }

    private async Task<HttpResponseMessage> GetAsync(
        RepoRef repository,
        string? credential,
        string path,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(HttpMethod.Get, path, credential, body: null);

        try
        {
            return await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> PostAsync(
        RepoRef repository,
        string? credential,
        string path,
        string body,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(HttpMethod.Post, path, credential, body);

        try
        {
            return await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> PatchAsync(
        RepoRef repository,
        string? credential,
        string path,
        string body,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(new HttpMethod("PATCH"), path, credential, body);

        try
        {
            return await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One request to the pinned host: fixed headers, the leased credential as the one bearer
    /// header, and a body only where the Git Data API takes one.
    /// </summary>
    /// <remarks>
    /// The credential is attached here and nowhere else. It is a lease the caller already holds
    /// and returns in the same call, and it is never logged, never put in a result and never
    /// put in an exception. The repository contributes two escaped name segments and nothing
    /// else: there is no caller-controlled host, port, path or query anywhere in this class.
    /// </remarks>
    private HttpRequestMessage BuildRequest(
        HttpMethod method,
        string path,
        string? credential,
        string? body)
    {
        var request = new HttpRequestMessage(method, path);

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        // Attached only when the caller holds a lease. The reconciler calls with none: it can
        // only ask what the branch says in public, and a private branch stays unknown rather
        // than becoming a credentialed read.
        if (!string.IsNullOrEmpty(credential))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>
    /// Turns an answered refusal into the code it means. A redirect is not an answer here, and
    /// following one is the one way the bearer header could leave the host it was issued for.
    /// </summary>
    private static GitHubPagesUpstreamException Classify(HttpResponseMessage response)
    {
        var status = response.StatusCode;

        if (status is HttpStatusCode.Unauthorized)
        {
            return new GitHubPagesUpstreamException(GitHubPagesError.Unauthorized);
        }

        if (status is HttpStatusCode.Forbidden)
        {
            // A secondary rate limit answers 403. The exhausted budget is the only thing that
            // distinguishes it from a refused credential, and it is a header, never a body.
            if (RateBudgetExhausted(response))
            {
                return new GitHubPagesUpstreamException(GitHubPagesError.RateLimited);
            }

            return new GitHubPagesUpstreamException(GitHubPagesError.Unauthorized);
        }

        if (status is HttpStatusCode.NotFound)
        {
            return new GitHubPagesUpstreamException(GitHubPagesError.NotFound);
        }

        if (status is HttpStatusCode.TooManyRequests)
        {
            return new GitHubPagesUpstreamException(GitHubPagesError.RateLimited);
        }

        if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            return new GitHubPagesUpstreamException(GitHubPagesError.Unavailable);
        }

        if (status == (HttpStatusCode)422)
        {
            return new GitHubPagesUpstreamException(GitHubPagesError.Rejected);
        }

        return new GitHubPagesUpstreamException(GitHubPagesError.Unavailable);
    }

    private static bool RateBudgetExhausted(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-ratelimit-remaining", out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (string.Equals(value.Trim(), "0", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads one SHA out of an upstream answer: the top-level <c>sha</c>, or the nested
    /// <c>object.sha</c> a ref read carries. Null when the answer is not the JSON understood.
    /// </summary>
    private static async Task<string?> ReadShaAsync(
        HttpResponseMessage response,
        string? container,
        CancellationToken cancellationToken)
    {
        byte[] bytes;

        try
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var buffered = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            int read;

            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;

                if (total > MaxResponseBytes)
                {
                    return null;
                }

                buffered.Write(chunk, 0, read);
            }

            bytes = buffered.ToArray();
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);

            var node = document.RootElement;

            if (container is not null)
            {
                if (!node.TryGetProperty(container, out var inner)
                    || inner.ValueKind is not JsonValueKind.Object)
                {
                    return null;
                }

                node = inner;
            }

            if (!node.TryGetProperty("sha", out var sha)
                || sha.ValueKind is not JsonValueKind.String)
            {
                return null;
            }

            var value = sha.GetString();

            return IsGitSha(value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsGitSha(string? value)
    {
        if (value is null || value.Length != 40)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isHex = (character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f')
                || (character >= 'A' && character <= 'F');

            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
