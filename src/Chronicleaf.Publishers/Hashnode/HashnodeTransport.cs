using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Chronicleaf.Publishers.Hashnode;

/// <summary>
/// A definitive upstream refusal: nothing was created because of it
/// (PUBLISHER-HASHNODE-ADAPTER-002).
/// </summary>
/// <remarks>
/// Raised for every answered failure - a refused token, a forbidden role, a plan-gated
/// publication, a rejected body, a rate limit, a refused redirect, a GraphQL errors array on
/// an HTTP 200 - and for every transport failure that happened before the request could have
/// left. What it never covers is a lost answer after a mutation may have been committed:
/// that answer is unknown rather than failed, and the adapter raises the contract's own
/// timeout for it instead of this.
/// </remarks>
public sealed class HashnodeUpstreamException(string errorCode)
    : Exception("The Hashnode upstream refused the request before anything was created.")
{
    /// <summary>One of <see cref="HashnodeError"/>: fixed words, never a provider body.</summary>
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// One created draft, as read (PUBLISHER-HASHNODE-ADAPTER-002).
/// </summary>
/// <param name="Id">The provider's draft identifier, carried verbatim.</param>
public sealed record HashnodeDraftRef(string Id);

/// <summary>
/// One published post, as read (PUBLISHER-HASHNODE-ADAPTER-002).
/// </summary>
/// <param name="Id">The provider's post identifier, carried verbatim.</param>
/// <param name="Url">The provider's address for the post, when it named an https URL.</param>
public sealed record HashnodePostRef(string Id, string? Url);

/// <summary>
/// The Hashnode GraphQL calls a publish is made of, and nothing else
/// (PUBLISHER-HASHNODE-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport owns its client.</b> It builds one <see cref="HttpClient"/> pinned to the
/// Hashnode GraphQL host over a handler it creates itself, with redirects refused and no
/// cookies - so no bare-handler registration in a composition root can rewire it, and a second
/// transport cannot make single-handler resolution order-dependent. That is what lets a test
/// supply a scripted handler and see this class's real decisions - the classification of a
/// status, the refusal of a redirect, the bearer header, the fixed query - without a socket.
/// </para>
/// <para>
/// <b>The queries are fixed literals.</b> Both documents are constants: no value an operator
/// or a request could supply can reshape them, because values travel only in
/// <c>variables</c>. There is no pagination in either mutation, so there is no page size to
/// parameterize either - the "literals only" rule holds vacuously here rather than by a cap.
/// </para>
/// <para>
/// <b>Draft first, then publish - never a publish without its draft.</b> A draft-mode run is
/// one <c>createDraft</c> call. A publish-mode run is the same create followed by one
/// <c>publishDraft</c> for the created draft id: the publish mutation addresses the draft
/// this run created, never an id from another run, and a publish failure keeps that draft
/// id in the receipt rather than issuing a new create.
/// </para>
/// <para>
/// <b>The response body is untrusted input.</b> An error body is disposed unread in every
/// branch, because that is where a provider echoes request text a receipt must never repeat.
/// A success body is parsed with <see cref="JsonDocument"/>, bounded before it leaves this
/// class, and never repeated into an exception, a log line or a result beyond the draft and
/// post references the receipt owns. A GraphQL errors array on an HTTP 200 is an answered
/// refusal, classified the same way - never retried, never quoted.
/// </para>
/// </remarks>
public sealed class HashnodeTransport : IDisposable
{
    /// <summary>The host this transport is pinned to. Requests are built as relative paths.</summary>
    public const string Host = "gql-beta.hashnode.com";

    /// <summary>How long one upstream request may take before the transport gives up on it.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>How large an upstream JSON answer may be before it is refused unread.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>
    /// The one draft mutation this transport sends. Fixed: values travel in variables.
    /// </summary>
    public const string CreateDraftDocument =
        "mutation CreateDraft($input: CreateDraftInput!) { createDraft(input: $input) { draft { id } } }";

    /// <summary>
    /// The one publish mutation this transport sends. Fixed: values travel in variables.
    /// </summary>
    public const string PublishDraftDocument =
        "mutation PublishDraft($input: PublishDraftInput!) { publishDraft(input: $input) { post { id slug url } } }";

    private readonly HttpClient http;
    private bool disposed;

    /// <summary>Builds the production transport over its own pinned handler.</summary>
    public HashnodeTransport()
        : this(CreateHandler())
    {
    }

    /// <summary>
    /// Builds the transport over the given handler. The composition root uses the parameterless
    /// constructor; a test supplies the scripted far end of the wire here.
    /// </summary>
    public HashnodeTransport(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri($"https://{Host}"),
            Timeout = Timeout,
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd("Chronicleaf");
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
    /// Creates one draft under the operator's publication from the approved text.
    /// </summary>
    /// <param name="publicationId">The operator's validated publication id.</param>
    /// <param name="title">The approved milestone title, verbatim.</param>
    /// <param name="bodyMarkdown">The approved revision's exact stored bytes.</param>
    /// <param name="credential">The leased personal access token, for this call only.</param>
    /// <returns>The created draft's reference, or null when the provider answered 200 in JSON
    /// this client does not understand: the draft may exist but its id cannot be named, so the
    /// adapter records unknown rather than failed - failed would invite a second create of the
    /// same text.</returns>
    /// <exception cref="HashnodeUpstreamException">The provider answered a refusal.</exception>
    public async Task<HashnodeDraftRef?> CreateDraftAsync(
        string publicationId,
        string title,
        string bodyMarkdown,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(bodyMarkdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);

        var body = await PostAsync(
            CreateDraftDocument,
            new { input = new { publicationId, title, contentMarkdown = bodyMarkdown } },
            credential,
            cancellationToken).ConfigureAwait(false);

        using var document = body;
        var data = DataNode(document);

        if (data is null ||
            !data.Value.TryGetProperty("createDraft", out var created) ||
            created.ValueKind is not JsonValueKind.Object ||
            !created.TryGetProperty("draft", out var draft) ||
            draft.ValueKind is not JsonValueKind.Object ||
            !draft.TryGetProperty("id", out var id) ||
            id.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var draftId = id.GetString();

        return string.IsNullOrWhiteSpace(draftId) ? null : new HashnodeDraftRef(draftId);
    }

    /// <summary>
    /// Publishes one draft this run created.
    /// </summary>
    /// <param name="draftId">The draft id the create call of this run returned.</param>
    /// <param name="credential">The leased personal access token, for this call only.</param>
    /// <returns>The published post's reference, or null when the provider answered 200 in JSON
    /// this client does not understand: the post may exist but its id cannot be named, so the
    /// adapter records unknown with the draft id rather than failed.</returns>
    /// <exception cref="HashnodeUpstreamException">The provider answered a refusal.</exception>
    public async Task<HashnodePostRef?> PublishDraftAsync(
        string draftId,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);

        var body = await PostAsync(
            PublishDraftDocument,
            new { input = new { draftId } },
            credential,
            cancellationToken).ConfigureAwait(false);

        using var document = body;
        var data = DataNode(document);

        if (data is null ||
            !data.Value.TryGetProperty("publishDraft", out var published) ||
            published.ValueKind is not JsonValueKind.Object ||
            !published.TryGetProperty("post", out var post) ||
            post.ValueKind is not JsonValueKind.Object ||
            !post.TryGetProperty("id", out var id) ||
            id.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var postId = id.GetString();

        if (string.IsNullOrWhiteSpace(postId))
        {
            return null;
        }

        // The upstream URL is derived from post.url, never guessed from a slug: a slug names a
        // path, not an address, and only the provider knows where the post lives.
        string? url = null;

        if (post.TryGetProperty("url", out var urlNode) &&
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

        return new HashnodePostRef(postId, url);
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
    /// Sends one GraphQL document with its variables and returns the parsed success body.
    /// Answered refusals - any non-200 status, or a GraphQL errors array on a 200 - throw the
    /// classified code with the body disposed unread.
    /// </summary>
    private async Task<JsonDocument> PostAsync(
        string document,
        object variables,
        string credential,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query = document, variables }),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The Hashnode GraphQL API takes the personal access token as a bearer token, never
        // as a query parameter: a query value would land in access logs.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);

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
            if (response.StatusCode is not HttpStatusCode.OK)
            {
                // Every non-200 status is the provider's verdict, and a verdict means nothing
                // was created by this call: failed, never unknown. The body is disposed unread
                // with the response, because that is where a provider echoes request text.
                throw ClassifyStatus(response.StatusCode);
            }

            var parsed = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

            if (parsed is null)
            {
                // A 200 whose body cannot be read as bounded JSON is not a refusal - the write
                // may have happened - so it is a null, not a throw: unknown, never a second
                // create.
                return EmptyData();
            }

            using (parsed)
            {
                if (HasErrors(parsed))
                {
                    // A GraphQL errors array on an HTTP 200 is an answered refusal: the
                    // mutation did not commit. Classified from the fixed code words, never
                    // quoted, with the body disposed unread.
                    throw ClassifyGraphQLErrors(parsed);
                }

                return Detach(parsed);
            }
        }
    }

    /// <summary>
    /// Turns an answered HTTP refusal into the code it means. A redirect is not an answer
    /// here, and following one is the one way the bearer token could leave the host it was
    /// issued for.
    /// </summary>
    private static HashnodeUpstreamException ClassifyStatus(HttpStatusCode status)
    {
        if (status is HttpStatusCode.Unauthorized)
        {
            return new HashnodeUpstreamException(HashnodeError.CredentialRejected);
        }

        if (status is HttpStatusCode.Forbidden)
        {
            return new HashnodeUpstreamException(HashnodeError.PublishForbidden);
        }

        if (status == (HttpStatusCode)422)
        {
            return new HashnodeUpstreamException(HashnodeError.InvalidPayload);
        }

        if (status is HttpStatusCode.TooManyRequests)
        {
            return new HashnodeUpstreamException(HashnodeError.RateLimited);
        }

        if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            return new HashnodeUpstreamException(HashnodeError.RedirectRefused);
        }

        return new HashnodeUpstreamException(HashnodeError.Unavailable);
    }

    /// <summary>
    /// Turns a GraphQL errors array into the code the first error means. Reads only the fixed
    /// <c>extensions.code</c> words and - for FORBIDDEN - whether the message names the plan;
    /// the message itself is never repeated anywhere.
    /// </summary>
    private static HashnodeUpstreamException ClassifyGraphQLErrors(JsonDocument body)
    {
        var (code, plan) = FirstError(body);

        if (string.Equals(code, "UNAUTHENTICATED", StringComparison.Ordinal))
        {
            return new HashnodeUpstreamException(HashnodeError.CredentialRejected);
        }

        if (string.Equals(code, "FORBIDDEN", StringComparison.Ordinal))
        {
            // The Hashnode-owned guide gates writes behind Pro: a FORBIDDEN caused by the
            // plan is a permanent capability outcome, while any other FORBIDDEN is the role
            // refusing - a contributor never publishes.
            return new HashnodeUpstreamException(
                plan ? HashnodeError.CapabilityUnavailable : HashnodeError.PublishForbidden);
        }

        if (string.Equals(code, "BAD_USER_INPUT", StringComparison.Ordinal) ||
            string.Equals(code, "GRAPHQL_VALIDATION_FAILED", StringComparison.Ordinal) ||
            string.Equals(code, "VALIDATION_ERROR", StringComparison.Ordinal))
        {
            return new HashnodeUpstreamException(HashnodeError.InvalidPayload);
        }

        if (string.Equals(code, "RATE_LIMITED", StringComparison.Ordinal))
        {
            return new HashnodeUpstreamException(HashnodeError.RateLimited);
        }

        return new HashnodeUpstreamException(HashnodeError.Unavailable);
    }

    /// <summary>
    /// Reads the first error's fixed code words and whether its message names the plan. The
    /// message text never leaves this method: it decides one branch and is then dropped.
    /// </summary>
    private static (string? Code, bool Plan) FirstError(JsonDocument body)
    {
        try
        {
            var root = body.RootElement;

            if (!root.TryGetProperty("errors", out var errors) ||
                errors.ValueKind is not JsonValueKind.Array)
            {
                return (null, false);
            }

            foreach (var error in errors.EnumerateArray())
            {
                if (error.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                string? code = null;

                if (error.TryGetProperty("extensions", out var extensions) &&
                    extensions.ValueKind is JsonValueKind.Object &&
                    extensions.TryGetProperty("code", out var codeNode) &&
                    codeNode.ValueKind is JsonValueKind.String)
                {
                    code = codeNode.GetString();
                }

                var plan = false;

                if (error.TryGetProperty("message", out var message) &&
                    message.ValueKind is JsonValueKind.String)
                {
                    plan = message.GetString()?.Contains(
                        "plan", StringComparison.OrdinalIgnoreCase) is true;
                }

                return (code, plan);
            }

            return (null, false);
        }
        catch (InvalidOperationException)
        {
            return (null, false);
        }
    }

    private static bool HasErrors(JsonDocument body)
    {
        try
        {
            return body.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind is JsonValueKind.Array &&
                errors.GetArrayLength() > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static JsonElement? DataNode(JsonDocument body)
    {
        try
        {
            if (!body.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            return data;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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

    private static JsonDocument EmptyData() =>
        JsonDocument.Parse("{\"data\":{}}");

    /// <summary>
    /// Re-parses a document so the returned copy outlives the using the caller parsed under.
    /// </summary>
    private static JsonDocument Detach(JsonDocument parsed) =>
        JsonDocument.Parse(parsed.RootElement.GetRawText());
}
