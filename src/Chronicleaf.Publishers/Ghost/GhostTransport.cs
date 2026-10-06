using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Chronicleaf.Publishers.Ghost;

/// <summary>
/// A definitive upstream refusal: nothing was created because of it
/// (PUBLISHER-GHOST-ADAPTER-002).
/// </summary>
/// <remarks>
/// Raised for every answered failure - a refused key, a forbidden role, a rejected body, a
/// conflict, a rate limit, a refused redirect, an unsupported API major - and for every
/// transport failure that happened before the request could have left. What it never covers
/// is a lost answer after the create may have been committed: that answer is unknown rather
/// than failed, and the adapter raises the contract's own timeout for it instead of this.
/// </remarks>
public sealed class GhostUpstreamException(string errorCode)
    : Exception("The Ghost upstream refused the request before anything was created.")
{
    /// <summary>One of <see cref="GhostError"/>: fixed words, never a provider body.</summary>
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// The leased integration key was not an <c>id:secret</c> pair this transport could sign with
/// (PUBLISHER-GHOST-ADAPTER-002).
/// </summary>
/// <remarks>
/// Caught by the adapter and reported as <c>ghost_credential_rejected</c> before any byte
/// leaves: the remedy is a valid stored key either way, and the malformed value itself never
/// reaches a row, a log line or the wire. The message is fixed words - it names no secret,
/// no id and no host.
/// </remarks>
public sealed class GhostKeyUnusableException()
    : Exception("The stored Ghost integration key is not one this transport can sign with.");

/// <summary>
/// One created post, as read (PUBLISHER-GHOST-ADAPTER-002).
/// </summary>
/// <param name="Id">The provider's post identifier, carried verbatim.</param>
/// <param name="Url">The provider's address for the post, when it named an https URL.</param>
public sealed record GhostPostRef(string Id, string? Url);

/// <summary>
/// The Admin post call a Ghost publish is made of, and nothing else
/// (PUBLISHER-GHOST-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport owns its client, but not its origin.</b> It builds one
/// <see cref="HttpClient"/> over a handler it creates itself, with redirects refused and no
/// cookies - so no bare-handler registration in a composition root can rewire it, and a
/// second transport cannot make single-handler resolution order-dependent. The client carries
/// no base address: the origin arrives per call from the operator's re-validated target, and
/// every request URI is built absolute from it. That is what lets a test supply a scripted
/// handler and see this class's real decisions - the classification of a status, the refusal
/// of a redirect, the Ghost header, the Accept-Version, the fixed posts shape - without a
/// socket.
/// </para>
/// <para>
/// <b>Create only.</b> Every publish is one <c>POST /ghost/api/admin/posts/?source=html</c>
/// creating one post with <c>status</c> <c>draft</c> or <c>published</c> by the authorized
/// mode. This class exposes no PUT: the contract dispatches each attempt exactly once, so
/// an update would overwrite a remote post the owner never asked to rewrite, while a second
/// dispatch is refused by the attempt claim before this class is reached. The receipt
/// carries the remote id for inspection by hand; the provider's <c>updated_at</c> guard
/// therefore stays provider-side, where no blind retry can trip it.
/// </para>
/// <para>
/// <b>The response body is untrusted input.</b> An error body is disposed unread in every
/// branch, because that is where a provider echoes request text a receipt must never repeat.
/// A success body is parsed with <see cref="JsonDocument"/>, bounded before it leaves this
/// class, and never repeated into an exception, a log line or a result beyond the id and URL
/// the receipt owns.
/// </para>
/// </remarks>
public sealed class GhostTransport : IDisposable
{
    /// <summary>The Admin posts path every request is built from. The host is the target's.</summary>
    public const string AdminPostsPath = "/ghost/api/admin/posts/";

    /// <summary>How long one upstream request may take before the transport gives up on it.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>How large an upstream JSON answer may be before it is refused unread.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>How long a remote post address may be as a carried label.</summary>
    public const int MaxUrlLength = 2000;

    private readonly HttpClient http;
    private readonly TimeProvider clock;
    private bool disposed;

    /// <summary>Builds the production transport over its own pinned handler.</summary>
    public GhostTransport()
        : this(CreateHandler(), TimeProvider.System)
    {
    }

    /// <summary>
    /// Builds the transport over the given handler. The composition root uses the parameterless
    /// constructor; a test supplies the scripted far end of the wire here.
    /// </summary>
    public GhostTransport(HttpMessageHandler handler)
        : this(handler, TimeProvider.System)
    {
    }

    /// <summary>
    /// Builds the transport over the given handler with the given clock. The composition root
    /// uses the parameterless constructor; a scenario fixes the clock to assert the token's
    /// instants.
    /// </summary>
    public GhostTransport(HttpMessageHandler handler, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(clock);

        // No base address on purpose: the origin is the operator's per-call target, not a
        // constant of this class, so every request URI is built absolute from it.
        http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout,
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd("Chronicleaf");
        this.clock = clock;
    }

    /// <summary>
    /// The pinned handler: no automatic redirects, no cookies. A redirect that arrives anyway
    /// is answered as a refusal rather than a second hop, because the second hop is the one
    /// construct that could carry the leased token to another host.
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
    /// Creates one post from the approved HTML under the operator's origin.
    /// </summary>
    /// <param name="origin">The operator's validated publication origin.</param>
    /// <param name="apiVersion">The operator's validated Admin API version, sent verbatim.</param>
    /// <param name="title">The approved milestone title, verbatim.</param>
    /// <param name="html">The shared export's sanitized article body for the approved bytes.</param>
    /// <param name="status">Either <c>draft</c> or <c>published</c>, by the authorized mode.</param>
    /// <param name="tags">The operator's validated default tag names, or empty for none.</param>
    /// <param name="integrationKey">The leased <c>id:secret</c> integration key, for this call only.</param>
    /// <returns>The created post's reference, or null when the provider answered 200 in JSON
    /// this client does not understand: the post may exist but its id cannot be named, so the
    /// adapter records unknown rather than failed - failed would invite a second create of the
    /// same post.</returns>
    /// <exception cref="GhostKeyUnusableException">The leased key is not an id:secret pair.</exception>
    /// <exception cref="GhostUpstreamException">The provider answered a refusal.</exception>
    public async Task<GhostPostRef?> CreatePostAsync(
        Uri origin,
        string apiVersion,
        string title,
        string html,
        string status,
        IReadOnlyList<string> tags,
        string integrationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentException.ThrowIfNullOrWhiteSpace(integrationKey);

        var (keyId, keyBytes) = SplitKey(integrationKey);
        var token = GhostJwt.CreateToken(keyId, keyBytes, clock.GetUtcNow());

        var body = await PostAsync(
            origin,
            apiVersion,
            title,
            html,
            status,
            tags,
            token,
            cancellationToken).ConfigureAwait(false);

        using var document = body;

        if (!document.RootElement.TryGetProperty("posts", out var posts) ||
            posts.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        foreach (var post in posts.EnumerateArray())
        {
            if (post.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            if (!post.TryGetProperty("id", out var id) ||
                id.ValueKind is not JsonValueKind.String)
            {
                return null;
            }

            var postId = id.GetString();

            if (string.IsNullOrWhiteSpace(postId))
            {
                return null;
            }

            // The upstream URL is derived from the post's url, never guessed from a slug or a
            // title: a slug names a path, not an address, and only the provider knows where
            // the post lives. The provider's updated_at is read and dropped on purpose: the
            // contract never re-dispatches an attempt, so no update guard of ours could use it.
            string? url = null;

            if (post.TryGetProperty("url", out var urlNode) &&
                urlNode.ValueKind is JsonValueKind.String)
            {
                var candidate = urlNode.GetString();

                if (!string.IsNullOrWhiteSpace(candidate) &&
                    candidate.Length <= MaxUrlLength &&
                    Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
                    string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
                {
                    url = candidate;
                }
            }

            return new GhostPostRef(postId, url);
        }

        return null;
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

    /// <summary>
    /// Splits one leased integration key into the id the token names and the bytes it signs
    /// with. Anything that is not <c>id:hex-secret</c> is unusable, reported before any byte
    /// leaves rather than as a refused publish.
    /// </summary>
    private static (string KeyId, byte[] KeyBytes) SplitKey(string integrationKey)
    {
        var separator = integrationKey.IndexOf(':');

        if (separator <= 0 || separator == integrationKey.Length - 1)
        {
            throw new GhostKeyUnusableException();
        }

        var keyId = integrationKey[..separator];
        var secret = integrationKey[(separator + 1)..];

        byte[] keyBytes;

        try
        {
            keyBytes = Convert.FromHexString(secret);
        }
        catch (FormatException)
        {
            throw new GhostKeyUnusableException();
        }

        if (keyBytes.Length == 0)
        {
            throw new GhostKeyUnusableException();
        }

        return (keyId, keyBytes);
    }

    /// <summary>
    /// Sends one Admin posts create and returns the parsed success body. Answered refusals -
    /// any status but 200 or 201 - throw the classified code with the body disposed unread.
    /// </summary>
    private async Task<JsonDocument> PostAsync(
        Uri origin,
        string apiVersion,
        string title,
        string html,
        string status,
        IReadOnlyList<string> tags,
        string token,
        CancellationToken cancellationToken)
    {
        var address = new Uri(origin, $"{AdminPostsPath}?source=html");
        var request = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    posts = new[]
                    {
                        new
                        {
                            title,
                            html,
                            status,
                            tags = tags.Select(name => new { name }).ToArray(),
                        },
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The Admin API takes the signed token as a Ghost-scheme authorization header, never
        // as a query parameter: a query value would land in access logs.
        request.Headers.Authorization = new AuthenticationHeaderValue("Ghost", token);
        request.Headers.Add("Accept-Version", apiVersion);

        HttpResponseMessage response;

        try
        {
            response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            request.Dispose();
            throw;
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Created)
            {
                // Every other status is the provider's verdict, and a verdict means nothing
                // was created by this call: failed, never unknown. The body is disposed unread
                // with the response, because that is where a provider echoes request text.
                throw ClassifyStatus(response.StatusCode);
            }

            var parsed = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

            if (parsed is null)
            {
                // An answered success whose body cannot be read as bounded JSON is not a
                // refusal - the write may have happened - so it is an empty posts shape, not
                // a throw: unknown, never a second create.
                return EmptyPosts();
            }

            return parsed;
        }
    }

    /// <summary>
    /// Turns an answered HTTP refusal into the code it means. A redirect is not an answer
    /// here, and following one is the one way the leased token could leave the origin it was
    /// issued for.
    /// </summary>
    private static GhostUpstreamException ClassifyStatus(HttpStatusCode status)
    {
        if (status is HttpStatusCode.Unauthorized)
        {
            return new GhostUpstreamException(GhostError.CredentialRejected);
        }

        if (status is HttpStatusCode.Forbidden)
        {
            return new GhostUpstreamException(GhostError.PublishForbidden);
        }

        if (status is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType
            or (HttpStatusCode)422)
        {
            return new GhostUpstreamException(GhostError.InvalidPayload);
        }

        if (status is HttpStatusCode.Conflict)
        {
            return new GhostUpstreamException(GhostError.UpdateConflict);
        }

        if (status is HttpStatusCode.TooManyRequests)
        {
            return new GhostUpstreamException(GhostError.RateLimited);
        }

        if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            or (HttpStatusCode)308)
        {
            return new GhostUpstreamException(GhostError.RedirectRefused);
        }

        return new GhostUpstreamException(GhostError.Unavailable);
    }

    /// <summary>
    /// Reads one bounded JSON body: null when the bytes are not bounded readable JSON - an
    /// ambiguous answer, never a refusal.
    /// </summary>
    private static async Task<JsonDocument?> ReadJsonAsync(
        HttpResponseMessage response,
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
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonDocument EmptyPosts() =>
        JsonDocument.Parse("{\"posts\":[]}");
}
