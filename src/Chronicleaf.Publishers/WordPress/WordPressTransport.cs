using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Chronicleaf.Publishers.WordPress;

/// <summary>
/// A definitive upstream refusal: nothing was created because of it
/// (PUBLISHER-WORDPRESS-ADAPTER-002).
/// </summary>
/// <remarks>
/// Raised for every answered failure - a refused password, a forbidden role, a rejected body
/// or unknown taxonomy, a conflict, a rate limit, a refused redirect - and for every transport
/// failure that happened before the request could have left. What it never covers is a lost
/// answer after the create may have been committed: that answer is unknown rather than
/// failed, and the adapter raises the contract's own timeout for it instead of this.
/// </remarks>
public sealed class WordPressUpstreamException(string errorCode)
    : Exception("The WordPress upstream refused the request before anything was created.")
{
    /// <summary>One of <see cref="WordPressError"/>: fixed words, never a provider body.</summary>
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// One created post, as read (PUBLISHER-WORDPRESS-ADAPTER-002).
/// </summary>
/// <param name="Id">The provider's numeric post identifier, carried verbatim.</param>
/// <param name="Url">The provider's link for the post, when it named an https URL.</param>
public sealed record WordPressPostRef(string Id, string? Url);

/// <summary>
/// The posts call a WordPress publish is made of, and nothing else
/// (PUBLISHER-WORDPRESS-ADAPTER-001/002).
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
/// of a redirect, the Basic credential, the fixed posts shape - without a socket.
/// </para>
/// <para>
/// <b>Create only.</b> Every publish is one <c>POST /wp-json/wp/v2/posts</c> creating one
/// post with <c>status</c> <c>draft</c> or <c>publish</c> by the authorized mode. This class
/// exposes no PUT: the contract dispatches each attempt exactly once, so an update would
/// overwrite a remote post the owner never asked to rewrite, while a second dispatch is
/// refused by the attempt claim before this class is reached. The receipt carries the remote
/// id for inspection by hand; the provider's <c>modified_gmt</c> guard therefore stays
/// provider-side, where no blind retry can trip it.
/// </para>
/// <para>
/// <b>The response body is untrusted input.</b> An error body is disposed unread in every
/// branch, because that is where a provider echoes request text a receipt must never repeat.
/// A success body is parsed with <see cref="JsonDocument"/>, bounded before it leaves this
/// class, and never repeated into an exception, a log line or a result beyond the id and link
/// the receipt owns.
/// </para>
/// </remarks>
public sealed class WordPressTransport : IDisposable
{
    /// <summary>The posts path every request is built from. The host is the target's.</summary>
    public const string PostsPath = "/wp-json/wp/v2/posts";

    /// <summary>How long one upstream request may take before the transport gives up on it.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>How large an upstream JSON answer may be before it is refused unread.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>How long a remote post address may be as a carried label.</summary>
    public const int MaxUrlLength = 2000;

    private readonly HttpClient http;
    private bool disposed;

    /// <summary>Builds the production transport over its own pinned handler.</summary>
    public WordPressTransport()
        : this(CreateHandler())
    {
    }

    /// <summary>
    /// Builds the transport over the given handler. The composition root uses the parameterless
    /// constructor; a test supplies the scripted far end of the wire here.
    /// </summary>
    public WordPressTransport(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // No base address on purpose: the origin is the operator's per-call target, not a
        // constant of this class, so every request URI is built absolute from it.
        http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout,
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd("Chronicleaf");
    }

    /// <summary>
    /// The pinned handler: no automatic redirects, no cookies. A redirect that arrives anyway
    /// is answered as a refusal rather than a second hop, because the second hop is the one
    /// construct that could carry the leased credential to another host.
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
    /// <param name="origin">The operator's validated site origin.</param>
    /// <param name="username">The operator's validated user the application password belongs to.</param>
    /// <param name="title">The approved milestone title, verbatim.</param>
    /// <param name="html">The shared export's sanitized article body for the approved bytes.</param>
    /// <param name="status">Either <c>draft</c> or <c>publish</c>, by the authorized mode.</param>
    /// <param name="slug">The operator's validated default slug, or null for the provider-generated slug.</param>
    /// <param name="categories">The operator's validated default category ids, or empty for none.</param>
    /// <param name="tags">The operator's validated default tag ids, or empty for none.</param>
    /// <param name="applicationPassword">The leased application password, for this call only.</param>
    /// <returns>The created post's reference, or null when the provider answered 200 in JSON
    /// this client does not understand: the post may exist but its id cannot be named, so the
    /// adapter records unknown rather than failed - failed would invite a second create of the
    /// same post.</returns>
    /// <exception cref="WordPressUpstreamException">The provider answered a refusal.</exception>
    public async Task<WordPressPostRef?> CreatePostAsync(
        Uri origin,
        string username,
        string title,
        string html,
        string status,
        string? slug,
        IReadOnlyList<int> categories,
        IReadOnlyList<int> tags,
        string applicationPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPassword);

        var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{applicationPassword}"));

        var body = await PostAsync(
            origin,
            title,
            html,
            status,
            slug,
            categories,
            tags,
            credential,
            cancellationToken).ConfigureAwait(false);

        using var document = body;

        if (!document.RootElement.TryGetProperty("id", out var id) ||
            id.ValueKind is not JsonValueKind.Number ||
            !id.TryGetInt32(out var postId) ||
            postId <= 0)
        {
            return null;
        }

        // The upstream URL is derived from the post's link, never guessed from a slug or a
        // title: a slug names a path, not an address, and only the provider knows where
        // the post lives. The provider's modified_gmt is read and dropped on purpose: the
        // contract never re-dispatches an attempt, so no update guard of ours could use it.
        string? url = null;

        if (document.RootElement.TryGetProperty("link", out var linkNode) &&
            linkNode.ValueKind is JsonValueKind.String)
        {
            var candidate = linkNode.GetString();

            if (!string.IsNullOrWhiteSpace(candidate) &&
                candidate.Length <= MaxUrlLength &&
                Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
                string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            {
                url = candidate;
            }
        }

        return new WordPressPostRef(
            postId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            url);
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
    /// Sends one posts create and returns the parsed success body. Answered refusals -
    /// any status but 200 or 201 - throw the classified code with the body disposed unread.
    /// </summary>
    private async Task<JsonDocument> PostAsync(
        Uri origin,
        string title,
        string html,
        string status,
        string? slug,
        IReadOnlyList<int> categories,
        IReadOnlyList<int> tags,
        string credential,
        CancellationToken cancellationToken)
    {
        var address = new Uri(origin, PostsPath);

        // The shape is fixed: title and content as the provider's plain strings, status by the
        // authorized mode, and the operator's slug and taxonomy only when configured. No
        // excerpt, no author override, no featured media, no site administration of any kind.
        var payload = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["content"] = html,
            ["status"] = status,
        };

        if (!string.IsNullOrWhiteSpace(slug))
        {
            payload["slug"] = slug;
        }

        if (categories.Count > 0)
        {
            payload["categories"] = categories.ToArray();
        }

        if (tags.Count > 0)
        {
            payload["tags"] = tags.ToArray();
        }

        var request = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The application password travels as one Basic credential pair for the configured
        // user, never as a query parameter: a query value would land in access logs. The
        // password bytes are already inside the pair here; the transport holds no copy after
        // the call.
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);

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
                // refusal - the write may have happened - so it is an empty shape, not
                // a throw: unknown, never a second create.
                return EmptyPost();
            }

            return parsed;
        }
    }

    /// <summary>
    /// Turns an answered HTTP refusal into the code it means. A redirect is not an answer
    /// here, and following one is the one way the leased credential could leave the origin it
    /// was issued for.
    /// </summary>
    private static WordPressUpstreamException ClassifyStatus(HttpStatusCode status)
    {
        if (status is HttpStatusCode.Unauthorized)
        {
            return new WordPressUpstreamException(WordPressError.CredentialRejected);
        }

        if (status is HttpStatusCode.Forbidden)
        {
            return new WordPressUpstreamException(WordPressError.PublishForbidden);
        }

        if (status is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType
            or (HttpStatusCode)422)
        {
            return new WordPressUpstreamException(WordPressError.InvalidPayload);
        }

        if (status is HttpStatusCode.Conflict)
        {
            return new WordPressUpstreamException(WordPressError.UpdateConflict);
        }

        if (status is HttpStatusCode.TooManyRequests)
        {
            return new WordPressUpstreamException(WordPressError.RateLimited);
        }

        if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            or (HttpStatusCode)308)
        {
            return new WordPressUpstreamException(WordPressError.RedirectRefused);
        }

        return new WordPressUpstreamException(WordPressError.Unavailable);
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

    private static JsonDocument EmptyPost() =>
        JsonDocument.Parse("{\"id\":0}");
}
