using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Chronicleaf.Publishers.DevTo;

/// <summary>
/// A definitive upstream refusal: no article was created because of it
/// (PUBLISHER-DEVTO-ADAPTER-002).
/// </summary>
/// <remarks>
/// Raised for every answered failure - a refused key, a rejected body, a rate limit, a refused
/// redirect - and for every transport failure that happened before the request could have left.
/// What it never covers is a lost answer after the create may have been committed: that answer
/// is unknown rather than failed, and the adapter raises the contract's own timeout for it
/// instead of this.
/// </remarks>
public sealed class DevToUpstreamException(string errorCode)
    : Exception("The Dev.to upstream refused the request before any article was created.")
{
    /// <summary>One of <see cref="DevToError"/>: fixed words, never a provider body.</summary>
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// One created article, as read (PUBLISHER-DEVTO-ADAPTER-002).
/// </summary>
/// <param name="Id">The provider's numeric identifier, carried as text.</param>
/// <param name="Url">The provider's address for the article, when it named one.</param>
public sealed record DevToArticleRef(string Id, string? Url);

/// <summary>
/// The Forem article calls a Dev.to publish is made of, and nothing else
/// (PUBLISHER-DEVTO-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport owns its client.</b> It builds one <see cref="HttpClient"/> pinned to the
/// Dev.to host over a handler it creates itself, with redirects refused and no cookies - so no
/// bare-handler registration in a composition root can rewire it, and a second transport cannot
/// make single-handler resolution order-dependent. That is what lets a test supply a scripted
/// handler and see this class's real decisions - the classification of a status, the refusal of
/// a redirect, the api-key header - without a socket.
/// </para>
/// <para>
/// <b>Create has no update.</b> Every publish is a <c>POST /api/articles</c> that creates one
/// article; this class exposes no PUT, because an update would overwrite the remote article's
/// current text - which may no longer be what was approved - while a duplicate create is at
/// worst a second draft the owner deletes. The contract's idempotency key prevents a second
/// dispatch from this side; the provider's own idempotency is not assumed.
/// </para>
/// <para>
/// <b>The response body is untrusted input.</b> An error body is disposed unread in every
/// branch, because that is where a provider echoes request text a receipt must never repeat.
/// A success body is parsed with <see cref="JsonDocument"/>, bounded before it leaves this
/// class, and never repeated into an exception, a log line or a result beyond the id and URL
/// the receipt owns.
/// </para>
/// </remarks>
public sealed class DevToTransport : IDisposable
{
    /// <summary>The host this transport is pinned to. Requests are built as relative paths.</summary>
    public const string Host = "dev.to";

    /// <summary>The vendor media type the Forem API answers, sent as Accept rather than assumed.</summary>
    public const string AcceptMediaType = "application/vnd.forem.api-v1+json";

    /// <summary>How long one upstream request may take before the transport gives up on it.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>How large an upstream JSON answer may be before it is refused unread.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    private readonly HttpClient http;
    private bool disposed;

    /// <summary>Builds the production transport over its own pinned handler.</summary>
    public DevToTransport()
        : this(CreateHandler())
    {
    }

    /// <summary>
    /// Builds the transport over the given handler. The composition root uses the parameterless
    /// constructor; a test supplies the scripted far end of the wire here.
    /// </summary>
    public DevToTransport(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri($"https://{Host}"),
            Timeout = Timeout,
        };
    }

    /// <summary>
    /// The pinned handler: no automatic redirects, no cookies. A redirect that arrives anyway
    /// is answered as a refusal rather than a second hop, because the second hop is the one
    /// construct that could carry the leased key to another host.
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
    /// Creates one article from the approved text. There is no update verb on this transport.
    /// </summary>
    /// <param name="title">The approved milestone title, verbatim.</param>
    /// <param name="bodyMarkdown">The approved revision's exact stored bytes.</param>
    /// <param name="published">True only for an explicitly confirmed publish; otherwise a draft.</param>
    /// <param name="tags">The operator's validated default tags, or empty for none.</param>
    /// <param name="credential">The leased API key, for this call only.</param>
    /// <returns>The created article's reference, or null when the provider answered 201 in JSON
    /// this client does not understand: the article exists but its id cannot be named.</returns>
    /// <exception cref="DevToUpstreamException">The provider answered a refusal.</exception>
    public async Task<DevToArticleRef?> CreateArticleAsync(
        string title,
        string bodyMarkdown,
        bool published,
        IReadOnlyList<string> tags,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(bodyMarkdown);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);

        object article = tags.Count == 0
            ? new { title, body_markdown = bodyMarkdown, published }
            : new { title, body_markdown = bodyMarkdown, published, tags };

        var request = new HttpRequestMessage(HttpMethod.Post, "api/articles")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { article }),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptMediaType));

        // The Forem API takes the key as an api-key header, never as a bearer token and never
        // as a query parameter: a query value would land in access logs, and a bearer scheme
        // is not what this provider reads.
        request.Headers.TryAddWithoutValidation("api-key", credential);

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
            if (response.StatusCode is HttpStatusCode.Created)
            {
                // A 201 without the JSON understood is a null, not a refusal: the article exists
                // but its id cannot be named, so the adapter records unknown rather than failed -
                // failed would invite a second create of the same text.
                return await ReadArticleRefAsync(response, cancellationToken).ConfigureAwait(false);
            }

            // Every other answered status is the provider's verdict, and a verdict means the
            // create did not happen: failed, never unknown. The body is disposed unread with
            // the response, because that is where a provider echoes request text.
            throw Classify(response);
        }
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
    /// Turns an answered refusal into the code it means. A redirect is not an answer here, and
    /// following one is the one way the api-key header could leave the host it was issued for.
    /// </summary>
    private static DevToUpstreamException Classify(HttpResponseMessage response)
    {
        var status = response.StatusCode;

        if (status is HttpStatusCode.Unauthorized)
        {
            return new DevToUpstreamException(DevToError.CredentialRejected);
        }

        if (status is HttpStatusCode.Forbidden)
        {
            return new DevToUpstreamException(DevToError.CredentialRejected);
        }

        if (status == (HttpStatusCode)422)
        {
            return new DevToUpstreamException(DevToError.InvalidPayload);
        }

        if (status is HttpStatusCode.TooManyRequests)
        {
            return new DevToUpstreamException(DevToError.RateLimited);
        }

        if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            return new DevToUpstreamException(DevToError.RedirectRefused);
        }

        return new DevToUpstreamException(DevToError.Unavailable);
    }

    /// <summary>
    /// Reads one article reference out of a 201 answer: the numeric <c>id</c> and the <c>url</c>
    /// the provider named. Null when the answer is not the JSON understood.
    /// </summary>
    private static async Task<DevToArticleRef?> ReadArticleRefAsync(
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
            using var document = JsonDocument.Parse(bytes);
            var node = document.RootElement;

            if (!node.TryGetProperty("id", out var id) ||
                id.ValueKind is not JsonValueKind.Number ||
                !id.TryGetInt64(out var numericId) ||
                numericId <= 0)
            {
                return null;
            }

            string? url = null;

            if (node.TryGetProperty("url", out var urlNode) &&
                urlNode.ValueKind is JsonValueKind.String)
            {
                var candidate = urlNode.GetString();

                if (!string.IsNullOrWhiteSpace(candidate) &&
                    candidate.Length <= 2000 &&
                    Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
                    string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
                {
                    url = candidate;
                }
            }

            return new DevToArticleRef(
                numericId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                url);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
