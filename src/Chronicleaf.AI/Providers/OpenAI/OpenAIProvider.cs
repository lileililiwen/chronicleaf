using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Chronicleaf.AI.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronicleaf.AI.OpenAI;

/// <summary>
/// The OpenAI BYOK transport: one bounded, text-only, nonstreaming call to a fixed origin
/// (LLM-OPENAI-ADAPTER-001 transport mapping, LLM-OPENAI-ADAPTER-002 safe failure handling).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this type is.</b> A translator. It serialises the request the contract already resolved,
/// posts it to <see cref="OpenAIEndpoint.ResponsesUri"/> with a leased key, and maps whatever comes
/// back onto a <see cref="GenerationResult"/>. It does not decide what to generate, it does not read
/// the database, it does not choose a model, and it does not know what a milestone is - all of that
/// happened before it was resolved, and every fact it needs arrived inside the
/// <see cref="GenerationRequest"/>.
/// </para>
/// <para>
/// <b>What it refuses to do.</b> It never retries, because the contract's caller owns the attempt
/// budget and only a failure provably raised before the provider accepted the request is worth
/// repeating. It never follows a redirect, because the bytes an owner authorized were addressed to
/// one host. It never honours a caller-supplied base URL, because <see cref="GenerationRequest.EndpointRef"/>
/// exists for providers that run on an operator's own endpoint and OpenAI is not one. And it never
/// turns a refusal, a truncated answer or an unparsable body into a completed result, because a draft
/// is the one thing in this product that a human will act on.
/// </para>
/// <para>
/// <b>Secrets.</b> The key is leased for the length of the call, used to build one header, and
/// returned before this method returns. No log line, exception message or result field in this class
/// contains it, a request header, or a provider response body - a provider error body is precisely
/// where a key or a customer's name is most likely to be sitting in plain text, so bodies are
/// counted and discarded rather than read.
/// </para>
/// <para>
/// <b>Concurrency.</b> One instance per process, one <see cref="HttpClient"/> over it, and no mutable
/// per-call state, so concurrent calls are safe. The client is created with an infinite timeout on
/// purpose: the caller's cancellation token <i>is</i> the budget
/// (<see cref="GenerationRequest.TimeoutSeconds"/>), and a second, longer timeout here would be a way
/// for a call to outlive the authorization that paid for it.
/// </para>
/// </remarks>
public sealed class OpenAIProvider : ILLMProvider, IDisposable
{
    private readonly HttpClient _client;
    private readonly IOpenAIApiKeySource _keys;
    private readonly OpenAIProviderSettings _settings;
    private readonly ILogger<OpenAIProvider> _logger;
    private readonly ProviderCapability _capability;
    private bool _disposed;

    /// <summary>
    /// Builds the adapter over a handler.
    /// </summary>
    /// <param name="handler">
    /// The message handler. Production passes a pooled one; a test passes a stub at the far end of the
    /// wire, which is why a scenario can drive every branch of the mapping without a network and
    /// without replacing the <see cref="HttpClient"/> that does the real work.
    /// </param>
    /// <param name="keys">Where the key for a credential comes from.</param>
    /// <param name="settings">Which models this deployment will accept.</param>
    /// <param name="logger">Where a refusal says which shape of answer it saw.</param>
    public OpenAIProvider(
        HttpMessageHandler handler,
        IOpenAIApiKeySource keys,
        OpenAIProviderSettings settings,
        ILogger<OpenAIProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(settings);

        _keys = keys;
        _settings = settings;
        _logger = logger ?? NullLogger<OpenAIProvider>.Instance;

        // A constant origin, and a client that will not follow a redirect even if something upstream
        // asked it to. Both are non-negotiable rather than configurable, and neither is a setting a
        // deployment can turn off: a "temporary" redirect allowance is how an authorized payload ends
        // up at a host nobody reviewed.
        _client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = null,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // The provider slot's own descriptor. Reported verbatim by the catalogue, which copies it
        // without adjustment - so this is where the claim that OpenAI enforces the response schema
        // itself is actually made. The per-model caveat is documented on
        // <see cref="OpenAIProviderSettings"/>: naming a model in the allowlist is the operator's
        // assertion that this model supports it.
        _capability = new ProviderCapability(
            OpenAIEndpoint.ProviderId,
            StructuredOutputSupport: true,
            NativeJsonSchema: true,
            LocalEndpoint: false,
            RequiresCredential: true);
    }

    /// <inheritdoc />
    public string ProviderId => OpenAIEndpoint.ProviderId;

    /// <inheritdoc />
    public ProviderCapability Capability => _capability;

    /// <inheritdoc />
    /// <remarks>
    /// True only for a model this deployment's allowlist names. Never true optimistically, and never
    /// true for a near miss: substituting a model the owner did not authorize would bill them for a
    /// different generation than the one they read and consented to.
    /// </remarks>
    public bool SupportsModel(string model) =>
        !string.IsNullOrWhiteSpace(model) && _settings.ModelAllowlist.Contains(model);

    /// <inheritdoc />
    public async Task<GenerationResult> GenerateAsync(
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(request.ProviderId, OpenAIEndpoint.ProviderId, StringComparison.Ordinal))
        {
            // The resolver only hands an adapter a request naming its own slot, so this is a wiring
            // mistake rather than an owner mistake. It is reported as unusable and logged, never
            // retried against another vendor, and never answered by quietly using the named provider.
            _logger.LogError(
                "Event=ai.openai_wrong_slot RequestedProvider={RequestedProvider} ExpectedProvider={ExpectedProvider}",
                request.ProviderId,
                OpenAIEndpoint.ProviderId);

            return GenerationResult.Invalid(GenerationFinishReasons.Error);
        }

        if (!SupportsModel(request.Model))
        {
            // Refused before a credential is leased and before a socket is opened. The caller checks
            // this too, so reaching here means the allowlist and the product disagreed - which is worth
            // a log line and is still not a reason to send the call.
            _logger.LogWarning(
                "Event=ai.openai_model_not_allowed Model={Model}",
                request.Model);

            return GenerationResult.Invalid(GenerationFinishReasons.Error);
        }

        if (request.CredentialRef is not { } credentialId || credentialId == Guid.Empty)
        {
            // This provider cannot be called without a stored key, and the design says an absent
            // credential is a refusal rather than an anonymous request. No header is built and nothing
            // is sent.
            _logger.LogWarning("Event=ai.openai_credential_absent");

            return GenerationResult.Failed(GenerationErrorCodes.CredentialRejected);
        }

        var body = OpenAIWire.BuildRequest(
            request.Model,
            request.SystemInstruction,
            request.UntrustedEvidenceJson,
            request.ResponseSchema,
            request.MaxOutputTokens);

        if (body is null)
        {
            _logger.LogError("Event=ai.openai_request_not_serializable Model={Model}", request.Model);

            return GenerationResult.Invalid(GenerationFinishReasons.Error);
        }

        using var lease = await AcquireKeyAsync(request.ScopeId, credentialId, cancellationToken)
            .ConfigureAwait(false);

        if (lease is null)
        {
            return GenerationResult.Failed(GenerationErrorCodes.CredentialRejected);
        }

        if (lease.ScopeId != request.ScopeId)
        {
            // A credential that belongs to another scope is not this owner's key, whatever the id
            // matched. The check happens before a byte is read, and the bytes are dropped unread.
            _logger.LogError(
                "Event=ai.openai_credential_scope_mismatch RequestScope={RequestScope} LeaseScope={LeaseScope}",
                request.ScopeId,
                lease.ScopeId);

            return GenerationResult.Failed(GenerationErrorCodes.CredentialRejected);
        }

        if (!string.Equals(lease.Provider, OpenAIEndpoint.ProviderId, StringComparison.Ordinal))
        {
            // A key stored for another provider is not this provider's key. Refusing is the whole
            // reason the vault's provider column exists.
            _logger.LogError(
                "Event=ai.openai_credential_provider_mismatch LeaseProvider={LeaseProvider}",
                lease.Provider);

            return GenerationResult.Failed(GenerationErrorCodes.CredentialRejected);
        }

        LogCall(request);

        return await SendAsync(body, lease, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Leases the key, turning a vault refusal into a result rather than an exception.
    /// </summary>
    /// <remarks>
    /// The contract allows an adapter to throw only for cancellation; every other outcome is a result.
    /// A missing credential, a revoked key version and an unreadable operator key file are all
    /// credential failures from here, and all of them are reported the same way: the owner learns the
    /// call did not happen, and nothing about the vault's internals reaches them. The exception's
    /// <i>type</i> is logged and its message is not, because a vault failure message can name an
    /// absolute key-file path.
    /// </remarks>
    private async Task<OpenAIApiKeyLease?> AcquireKeyAsync(
        Guid scopeId,
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _keys.AcquireAsync(scopeId, credentialId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            _logger.LogWarning(
                "Event=ai.openai_credential_unavailable Failure={Failure}",
                failure.GetType().Name);

            return null;
        }
    }

    /// <summary>
    /// Posts the request and maps the answer.
    /// </summary>
    /// <remarks>
    /// The response is read with a byte cap rather than buffered and measured afterwards, so an
    /// oversized answer is refused while it is still arriving. Everything below the status mapping
    /// disposes the body without materialising it: a provider error body is the one place a secret or
    /// a customer's text is most likely to appear, and this adapter has no reason to read it.
    /// </remarks>
    private async Task<GenerationResult> SendAsync(
        System.Text.Json.Nodes.JsonObject body,
        OpenAIApiKeyLease lease,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, OpenAIEndpoint.ResponsesUri)
        {
            Content = new StringContent(
                body.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                Encoding.UTF8,
                "application/json"),
        };

        // The one place the secret becomes a header. The value is written from a span over the leased
        // buffer rather than from a string, so no copy of the key exists that a log or a dump could
        // reach; the header itself is dropped with the request message.
        message.Headers.Authorization = new AuthenticationHeaderValue(OpenAIEndpoint.BearerScheme, KeyText(lease));

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller's token. Rethrown rather than converted, because the contract says a cancelled
            // call produces no result at all and a bounded retry is the caller's decision, not this
            // adapter's.
            throw;
        }
        catch (HttpRequestException failure)
        {
            // A transport failure before any status line: DNS, TLS, a refused connection, a reset. The
            // contract's caller retries exactly this case and no other, because it is the only one that
            // provably happened before the provider saw the request.
            _logger.LogWarning(
                "Event=ai.openai_transport_failure Failure={Failure}",
                failure.GetType().Name);

            return GenerationResult.Failed(GenerationErrorCodes.ProviderUnavailable);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return MapFailureStatus(response.StatusCode);
            }

            return await MapSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Maps a non-success status onto the contract's closed set of error codes.</summary>
    /// <remarks>
    /// Deliberately coarse, and the shape of the mapping is the point:
    /// <list type="bullet">
    /// <item>401 and 403 are the same answer to an owner - their key or their access was refused - and
    /// the two are not distinguished so a client cannot learn which from the code.</item>
    /// <item>429 is a throttle. It is never retried here, and never reported as a fault: it is the
    /// caller's quota.</item>
    /// <item>5xx is a provider that could not serve the request. The caller's bounded retry may repeat
    /// it.</item>
    /// <item>Everything else, <b>including 3xx</b>, is an unusable answer. A redirect is not followed
    /// and not reported as unavailable, because the provider did accept the request and then pointed
    /// somewhere else; that is an answer this product cannot use, not a service that was down.</item>
    /// </list>
    /// The response body is disposed unread in every branch.
    /// </remarks>
    private GenerationResult MapFailureStatus(HttpStatusCode status)
    {
        var code = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => GenerationErrorCodes.CredentialRejected,
            (HttpStatusCode)429 => GenerationErrorCodes.RateLimited,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => GenerationErrorCodes.ProviderUnavailable,
            _ when (int)status >= 500 => GenerationErrorCodes.ProviderUnavailable,
            _ => GenerationErrorCodes.ProviderInvalidResponse,
        };

        _logger.LogWarning(
            "Event=ai.openai_http_status Status={(int)Status} MappedCode={MappedCode}",
            (int)status,
            code);

        return GenerationResult.Failed(code);
    }

    /// <summary>Reads a success body within the cap and maps it.</summary>
    private async Task<GenerationResult> MapSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > OpenAIEndpoint.MaxResponseBytes)
        {
            // Refused from the declared length without reading a byte. A provider that says it sent too
            // much has not sent a draft.
            _logger.LogWarning(
                "Event=ai.openai_response_oversized DeclaredBytes={DeclaredBytes} Limit={Limit}",
                declared,
                OpenAIEndpoint.MaxResponseBytes);

            return GenerationResult.Invalid(GenerationFinishReasons.Error);
        }

        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (BoundedResponseTooLargeException)
        {
            // The provider sent more than it declared, or used chunked encoding. Either way the answer
            // is not a draft this product will act on.
            _logger.LogWarning(
                "Event=ai.openai_response_oversized Limit={Limit}",
                OpenAIEndpoint.MaxResponseBytes);

            return GenerationResult.Invalid(GenerationFinishReasons.Error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException failure)
        {
            _logger.LogWarning(
                "Event=ai.openai_response_read_failure Failure={Failure}",
                failure.GetType().Name);

            return GenerationResult.Failed(GenerationErrorCodes.ProviderUnavailable);
        }

        if (bytes.Length > OpenAIEndpoint.MaxResponseBytes)
        {
            return GenerationResult.Invalid(GenerationFinishReasons.Error);
        }

        JsonElement document;
        try
        {
            using var parsed = JsonDocument.Parse(bytes);
            document = parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A 200 that is not the JSON this transport reads. Not a draft, and not a failure to reach
            // the provider - it answered.
            _logger.LogWarning("Event=ai.openai_response_unparsable");

            return GenerationResult.Invalid(GenerationFinishReasons.Error);
        }

        if (document.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("Event=ai.openai_response_not_an_object");

            return GenerationResult.Invalid(GenerationFinishReasons.Error);
        }

        return MapDocument(document);
    }

    /// <summary>
    /// Maps one parsed response document onto a result.
    /// </summary>
    /// <remarks>
    /// The order of the checks is the order of the ways this can go wrong, and the first match wins:
    /// a non-message item (a tool call, or a type this adapter does not know) before any text is read,
    /// then a refusal, then a failure status, then a completed or truncated answer. Reading text first
    /// would mean a response carrying both a function call and some text could be published as prose.
    /// </remarks>
    private GenerationResult MapDocument(JsonElement document)
    {
        var providerRequestId = OpenAIWire.ReadResponseId(document);
        var status = OpenAIWire.ReadStatus(document);

        if (OpenAIWire.HasNonMessageOutput(document))
        {
            // Text-only contract, so a tool call is invalid with a name that says what happened.
            _logger.LogWarning("Event=ai.openai_non_message_output Status={Status}", status);

            return GenerationResult.Invalid(GenerationFinishReasons.ToolCall, providerRequestId);
        }

        if (OpenAIWire.HasRefusal(document))
        {
            // The model declined. A refusal's explanation is provider prose about the owner's content
            // and has no product meaning, so no text is kept even though the provider sent some.
            _logger.LogWarning("Event=ai.openai_refusal Status={Status}", status);

            return GenerationResult.Refused(providerRequestId);
        }

        if (status is "failed")
        {
            // The provider answered and reported that it failed. Its body carries the reason and this
            // adapter has no reason to read it, so the owner is told the answer was unusable.
            _logger.LogWarning("Event=ai.openai_response_failed");

            return GenerationResult.Invalid(GenerationFinishReasons.Error, providerRequestId);
        }

        if (status is null)
        {
            // A 200 with no status. Unusable rather than assumed complete: a response that does not say
            // it finished is not a finished response.
            _logger.LogWarning("Event=ai.openai_status_absent");

            return GenerationResult.Invalid(GenerationFinishReasons.Error, providerRequestId);
        }

        var (inputTokens, outputTokens) = OpenAIWire.ReadUsage(document);
        var incompleteReason = OpenAIWire.ReadIncompleteReason(document);

        if (status is "incomplete")
        {
            // Two different reasons, two different outcomes. A content filter is the provider declining,
            // and a token ceiling is a short answer: a truncated draft is still a draft, reported as
            // completed with the length reason so a caller can refuse a half story rather than publish
            // it as if it were whole.
            if (incompleteReason is "content_filter")
            {
                _logger.LogWarning("Event=ai.openai_incomplete_content_filter");

                return GenerationResult.Refused(providerRequestId);
            }

            var truncated = OpenAIWire.ReadOutputText(document);
            if (string.IsNullOrWhiteSpace(truncated))
            {
                // Incomplete with no text at all is not a short answer, it is nothing usable.
                _logger.LogWarning("Event=ai.openai_incomplete_without_text Reason={Reason}", incompleteReason);

                return GenerationResult.Invalid(GenerationFinishReasons.Error, providerRequestId);
            }

            _logger.LogWarning(
                "Event=ai.openai_incomplete_reason Reason={Reason} OutputTokens={OutputTokens}",
                incompleteReason,
                outputTokens);

            return GenerationResult.Completed(
                truncated,
                GenerationFinishReasons.Length,
                providerRequestId,
                inputTokens,
                outputTokens);
        }

        if (status is not "completed")
        {
            // "in_progress" on a nonstreaming call, or a status added after this was written. Neither is
            // something to present as a draft.
            _logger.LogWarning("Event=ai.openai_status_unexpected Status={Status}", status);

            return GenerationResult.Invalid(GenerationFinishReasons.Error, providerRequestId);
        }

        var text = OpenAIWire.ReadOutputText(document);
        if (string.IsNullOrWhiteSpace(text))
        {
            // Completed with nothing in it. The contract refuses a blank completed result, and an empty
            // answer is not a draft.
            _logger.LogWarning("Event=ai.openai_completed_without_text");

            return GenerationResult.Invalid(GenerationFinishReasons.Error, providerRequestId);
        }

        return GenerationResult.Completed(
            text,
            GenerationFinishReasons.Stop,
            providerRequestId,
            inputTokens,
            outputTokens);
    }

    /// <summary>
    /// Reads at most <see cref="OpenAIEndpoint.MaxResponseBytes"/> and refuses rather than truncating.
    /// </summary>
    /// <remarks>
    /// Truncating a JSON body would produce a parse error attributed to the provider rather than to the
    /// size, so the cap is enforced by refusing. The buffer is allocated at the cap rather than grown,
    /// so a hostile content length cannot make this allocate what it claims.
    /// </remarks>
    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[OpenAIEndpoint.MaxResponseBytes + 1];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > OpenAIEndpoint.MaxResponseBytes)
        {
            throw new BoundedResponseTooLargeException();
        }

        return buffer[..total];
    }

    /// <summary>
    /// The leased key as header text.
    /// </summary>
    /// <remarks>
    /// A span has to become a <see cref="string"/> to go in a header, so this is the one unavoidable
    /// copy in the class. It is scoped to the expression and dies with the request message, and it is
    /// never logged, never compared, never put in an exception and never stored - the only other thing
    /// that could hold it is the header, which the <c>using</c> above disposes with the message.
    /// </remarks>
    private static string KeyText(OpenAIApiKeyLease lease) => Encoding.UTF8.GetString(lease.Key);

    /// <summary>
    /// Records the wire facts of this call.
    /// </summary>
    /// <remarks>
    /// The model, the capability it was asked to enforce, and the request version - the three facts a
    /// provider-side support conversation needs, and the three the contract's
    /// <see cref="GenerationResult"/> has no members for. The provider's own request id comes back in
    /// the result, so these two together are enough to reconcile a call with OpenAI's logs. No
    /// evidence content, no instruction and no key is written here: this line is readable by anyone with
    /// log access, which is a wider group than the people who may read a snapshot.
    /// </remarks>
    private void LogCall(GenerationRequest request) =>
        _logger.LogInformation(
            "Event=ai.openai_call Provider={Provider} Model={Model} RequestVersion={RequestVersion} "
                + "NativeJsonSchema={NativeJsonSchema} MaxOutputTokens={MaxOutputTokens} RequestId={RequestId}",
            request.ProviderId,
            request.Model,
            OpenAIEndpoint.RequestVersion,
            _capability.NativeJsonSchema,
            request.MaxOutputTokens,
            request.RequestId);

    /// <summary>Disposes the client this adapter owns. The handler is not disposed: it was not created here.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }

    /// <summary>Raised internally when a response exceeds the cap; never escapes this class.</summary>
    private sealed class BoundedResponseTooLargeException : Exception;
}
