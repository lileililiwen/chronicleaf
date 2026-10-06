using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Chronicleaf.Publishers.CustomBlog;

/// <summary>
/// A definitive upstream refusal: nothing was created because of it
/// (PUBLISHER-CUSTOM-BLOG-ADAPTER-002).
/// </summary>
/// <remarks>
/// Raised for every answered failure - a refused key, a forbidden write, a rejected body, a
/// hash conflict, an incompatible contract, a rate limit, a refused redirect - and for every
/// transport failure that happened before the request could have left. What it never covers
/// is a lost answer after the create may have been committed: that answer is unknown rather
/// than failed, and the adapter raises the contract's own timeout for it instead of this.
/// </remarks>
public sealed class CustomBlogUpstreamException(string errorCode)
    : Exception("The custom-blog upstream refused the request before anything was created.")
{
    /// <summary>One of <see cref="CustomBlogError"/>: fixed words, never a server body.</summary>
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// One created post, as read (PUBLISHER-CUSTOM-BLOG-ADAPTER-002).
/// </summary>
/// <param name="Id">The server's post identifier, carried verbatim within the stored bound.</param>
/// <param name="Url">The server's URL for the post, when it named an https URL.</param>
public sealed record CustomBlogPostRef(string Id, string? Url);

/// <summary>
/// The calls a custom-blog publish is made of, and nothing else
/// (PUBLISHER-CUSTOM-BLOG-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport owns its client, but not its origin.</b> It builds one
/// <see cref="HttpClient"/> over a handler it creates itself, with redirects refused and no
/// cookies - so no bare-handler registration in a composition root can rewire it, and a
/// second transport cannot make single-handler resolution order-dependent. The client carries
/// no base address: the origin arrives per call from the operator's re-validated target, and
/// every request URI is built absolute from it. That is what lets a test supply a scripted
/// handler and see this class's real decisions - the capabilities check, the classification
/// of a status, the refusal of a redirect, the bearer credential, the fixed posts shape -
/// without a socket.
/// </para>
/// <para>
/// <b>Capabilities first, then one create.</b> Every publish first reads
/// <c>GET /chronicleaf/v1/capabilities</c> under the same bearer credential and refuses an
/// incompatible server with <c>customblog_incompatible_remote_contract</c> before any post
/// bytes leave. The create is then one <c>POST /chronicleaf/v1/posts</c> carrying the
/// approved title and Markdown with <c>mode</c> <c>draft</c> or <c>publish</c> by the
/// authorized mode and <c>Idempotency-Key</c> naming the operation. This class exposes no
/// PUT and never calls the server's operations endpoint: the contract dispatches each
/// attempt exactly once, so an update would overwrite a remote post the owner never asked to
/// rewrite, while a second dispatch is refused by the attempt claim before this class is
/// reached. The receipt carries the remote id for inspection by hand.
/// </para>
/// <para>
/// <b>The response body is untrusted input.</b> An error body is disposed unread in every
/// branch, because that is where a server echoes request text a receipt must never repeat.
/// A success body is parsed with <see cref="JsonDocument"/>, bounded before it leaves this
/// class, and never repeated into an exception, a log line or a result beyond the id and URL
/// the receipt owns. A remote id longer than the attempt column or a URL longer than its
/// column is not a receipt: the write may have happened but its reference cannot be stored,
/// so the answer is unknown rather than a second create.
/// </para>
/// </remarks>
public sealed class CustomBlogTransport : IDisposable
{
    /// <summary>The posts path every create is built from. The host is the target's.</summary>
    public const string PostsPath = "/chronicleaf/v1/posts";

    /// <summary>The capabilities path every publish probes first. The host is the target's.</summary>
    public const string CapabilitiesPath = "/chronicleaf/v1/capabilities";

    /// <summary>The one publisher-contract schema version this transport speaks.</summary>
    public const int SchemaVersion = 1;

    /// <summary>How long one upstream request may take before the transport gives up on it.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>How large an upstream JSON answer may be before it is refused unread.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>How long a remote post address may be as a carried label.</summary>
    public const int MaxUrlLength = 2000;

    /// <summary>How long a remote post identifier may be as a carried label.</summary>
    public const int MaxRemoteIdLength = 200;

    private readonly HttpClient http;
    private bool disposed;

    /// <summary>Builds the production transport over its own pinned handler.</summary>
    public CustomBlogTransport()
        : this(CreateHandler())
    {
    }

    /// <summary>
    /// Builds the transport over the given handler. The composition root uses the parameterless
    /// constructor; a test supplies the scripted far end of the wire here.
    /// </summary>
    public CustomBlogTransport(HttpMessageHandler handler)
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
    /// Creates one post from the approved Markdown under the operator's origin, after the
    /// server's capabilities document proves it speaks this contract.
    /// </summary>
    /// <param name="origin">The operator's validated server origin.</param>
    /// <param name="allowedFormats">The operator's validated content-format allowlist.</param>
    /// <param name="apiKey">The leased API key, for this call only.</param>
    /// <param name="operationId">The durable operation id, sent as the idempotency key.</param>
    /// <param name="contentHash">The digest of exactly the bytes being sent.</param>
    /// <param name="title">The approved milestone title, verbatim.</param>
    /// <param name="markdown">The approved revision's exact stored bytes.</param>
    /// <param name="mode">Either <c>draft</c> or <c>publish</c>, by the authorized mode.</param>
    /// <returns>The created post's reference, or null when the server answered success in JSON
    /// this client does not understand: the post may exist but its id cannot be named, so the
    /// adapter records unknown rather than failed - failed would invite a second create of the
    /// same post.</returns>
    /// <exception cref="CustomBlogUpstreamException">The server answered a refusal.</exception>
    public async Task<CustomBlogPostRef?> CreatePostAsync(
        Uri origin,
        IReadOnlyList<string> allowedFormats,
        string apiKey,
        Guid operationId,
        string contentHash,
        string title,
        string markdown,
        string mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(allowedFormats);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);

        await EnsureCompatibleAsync(origin, allowedFormats, apiKey, cancellationToken).ConfigureAwait(false);

        var body = await PostAsync(
            origin,
            apiKey,
            operationId,
            contentHash,
            title,
            markdown,
            mode,
            cancellationToken).ConfigureAwait(false);

        using var document = body;

        if (!document.RootElement.TryGetProperty("remoteId", out var id) ||
            id.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var remoteId = id.GetString();

        if (string.IsNullOrWhiteSpace(remoteId) || remoteId.Length > MaxRemoteIdLength)
        {
            return null;
        }

        // The upstream URL is the address the server named, never guessed from a title or a
        // slug: only the server knows where the post lives.
        string? url = null;

        if (document.RootElement.TryGetProperty("url", out var urlNode) &&
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

        return new CustomBlogPostRef(remoteId, url);
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
    /// Reads the server's capabilities document and refuses an incompatible server before any
    /// post bytes leave. A refused credential or a refused redirect is reported with its own
    /// code; any other mismatch is the incompatible-contract refusal.
    /// </summary>
    private async Task EnsureCompatibleAsync(
        Uri origin,
        IReadOnlyList<string> allowedFormats,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var address = new Uri(origin, CapabilitiesPath);

        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The API key travels as one bearer credential, never as a query parameter: a query
        // value would land in access logs. The probes and the create share this call's lease.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;

        try
        {
            response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // A reset connection before any answer is not a verdict about the contract: the
            // probe is a GET that creates nothing, so a failed wire is a failed run - the
            // adapter's unknown path is reserved for a create that may have committed.
            throw new CustomBlogUpstreamException(CustomBlogError.Unavailable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The probe timed out before answering: like the reset above, a GET created
            // nothing, so this is a failed run rather than an ambiguous write. A caller that
            // cancelled is not caught here and propagates untouched.
            throw new CustomBlogUpstreamException(CustomBlogError.Unavailable);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new CustomBlogUpstreamException(CustomBlogError.CredentialRejected);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new CustomBlogUpstreamException(CustomBlogError.PublishForbidden);
            }

            if (IsRedirect(response.StatusCode))
            {
                throw new CustomBlogUpstreamException(CustomBlogError.RedirectRefused);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new CustomBlogUpstreamException(CustomBlogError.RateLimited);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new CustomBlogUpstreamException(CustomBlogError.IncompatibleRemoteContract);
            }

            var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

            if (document is null)
            {
                throw new CustomBlogUpstreamException(CustomBlogError.IncompatibleRemoteContract);
            }

            using (document)
            {
                if (!IsCompatible(document.RootElement, allowedFormats))
                {
                    throw new CustomBlogUpstreamException(CustomBlogError.IncompatibleRemoteContract);
                }
            }
        }
    }

    /// <summary>
    /// True when the capabilities document advertises this contract: schema version 1,
    /// idempotent writes, and at least one content format on the operator's allowlist.
    /// </summary>
    private static bool IsCompatible(JsonElement capabilities, IReadOnlyList<string> allowedFormats)
    {
        if (!capabilities.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind is not JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) ||
            version != SchemaVersion)
        {
            return false;
        }

        if (!capabilities.TryGetProperty("idempotentWrites", out var idempotent) ||
            idempotent.ValueKind is not JsonValueKind.True)
        {
            return false;
        }

        if (!capabilities.TryGetProperty("formats", out var formats) ||
            formats.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in formats.EnumerateArray())
        {
            if (entry.ValueKind is not JsonValueKind.String)
            {
                continue;
            }

            var format = entry.GetString();

            if (!string.IsNullOrWhiteSpace(format) &&
                allowedFormats.Contains(format, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sends one posts create and returns the parsed success body. Answered refusals -
    /// any status but 200 or 201 - throw the classified code with the body disposed unread.
    /// </summary>
    private async Task<JsonDocument> PostAsync(
        Uri origin,
        string apiKey,
        Guid operationId,
        string contentHash,
        string title,
        string markdown,
        string mode,
        CancellationToken cancellationToken)
    {
        var address = new Uri(origin, PostsPath);

        // The shape is fixed: the contract version, the operation id the server reconciles by,
        // the digest of exactly the bytes being sent, the approved title and Markdown, an
        // empty media manifest - approval-covered pictures are named in the preview's warnings
        // rather than sent, because this contract carries no private media URLs - and the mode
        // the contract authorized. No slug, no taxonomy, no template of any kind.
        var payload = new Dictionary<string, object?>
        {
            ["schemaVersion"] = SchemaVersion,
            ["operationId"] = operationId.ToString("D"),
            ["contentHash"] = contentHash,
            ["title"] = title,
            ["markdown"] = markdown,
            ["mediaManifest"] = Array.Empty<string>(),
            ["mode"] = mode,
        };

        var request = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        // The operation id is the idempotency key the capabilities document promises the
        // server honors: a retried transport of the same operation names the same post.
        request.Headers.Add("Idempotency-Key", operationId.ToString("D"));

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
                // Every other status is the server's verdict, and a verdict means nothing was
                // created by this call: failed, never unknown. The body is disposed unread
                // with the response, because that is where a server echoes request text.
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
    private static CustomBlogUpstreamException ClassifyStatus(HttpStatusCode status)
    {
        if (status is HttpStatusCode.Unauthorized)
        {
            return new CustomBlogUpstreamException(CustomBlogError.CredentialRejected);
        }

        if (status is HttpStatusCode.Forbidden)
        {
            return new CustomBlogUpstreamException(CustomBlogError.PublishForbidden);
        }

        if (status is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType
            or (HttpStatusCode)422)
        {
            return new CustomBlogUpstreamException(CustomBlogError.InvalidPayload);
        }

        if (status is HttpStatusCode.Conflict)
        {
            return new CustomBlogUpstreamException(CustomBlogError.ContentConflict);
        }

        if (status is HttpStatusCode.PreconditionFailed)
        {
            return new CustomBlogUpstreamException(CustomBlogError.IncompatibleRemoteContract);
        }

        if (status is HttpStatusCode.TooManyRequests)
        {
            return new CustomBlogUpstreamException(CustomBlogError.RateLimited);
        }

        if (IsRedirect(status))
        {
            return new CustomBlogUpstreamException(CustomBlogError.RedirectRefused);
        }

        return new CustomBlogUpstreamException(CustomBlogError.Unavailable);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            or (HttpStatusCode)308;

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
        JsonDocument.Parse("{\"remoteId\":null}");
}
