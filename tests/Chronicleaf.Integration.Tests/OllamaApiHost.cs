using System.Net;
using System.Text;
using Chronicleaf.AI.Ollama;
using Chronicleaf.AI.Contracts;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.AI;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application against a real disposable PostgreSQL and a real
/// temporary key file, with the real <see cref="OllamaProvider"/> registered and a stub at the far end
/// of its wire (LLM-OLLAMA-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real here.</b> The composition root, the provider resolver, the generation service, the
/// consent handshake, the credential vault over a real key file, the routes, the access boundary, the
/// evidence store and the database. The <see cref="OllamaProvider"/> itself is production's, not a
/// double - so what a scenario proves about the mapping is a fact about the adapter that ships.
/// </para>
/// <para>
/// <b>What is substituted, and why it is the only thing that may be.</b> The
/// <see cref="HttpMessageHandler"/> underneath the adapter's <see cref="HttpClient"/>. Everything
/// above the socket is production: the request is serialised by the real adapter, the optional bearer
/// header is built by the real adapter from a lease taken out of the real vault, the status is mapped
/// by the real adapter, and the result is mapped onward by the real service. A stub at the far end of
/// the wire substitutes the Ollama daemon and nothing else - the same substitution the delivered
/// github-evidence host makes for GitHub, and the reason it is legitimate here is that the claim under
/// test is never "Ollama works".
/// </para>
/// <para>
/// <b>What no scenario in this file may be read as proving.</b> That any Ollama daemon exists, that
/// any named model is real or available to any particular owner, that a call is cheap, or that a
/// real response has any particular shape beyond the one fixture this project pinned from the
/// provider's own reference on 2026-09-30. The fixtures are the documented wire shape; they are not
/// evidence about the live service.
/// </para>
/// </remarks>
public sealed class OllamaApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string? _keyFile;
    private readonly RecordingOllamaHandler? _handler;
    private readonly OllamaProviderSettings? _settings;
    private readonly IOllamaApiKeySource? _keys;
    private readonly LogCapture _logs;

    private OllamaApiHost(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        RecordingOllamaHandler? handler,
        OllamaProviderSettings? settings,
        IOllamaApiKeySource? keys,
        LogCapture logs)
    {
        _connectionString = connectionString;
        _clock = clock;
        _keyFile = keyFile;
        _handler = handler;
        _settings = settings;
        _keys = keys;
        _logs = logs;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>Every log line this host has written, so a scenario can prove what did not reach it.</summary>
    public LogCapture Logs => _logs;

    /// <summary>
    /// The stub at the far end of the wire, so a scenario can read exactly what was sent. Null on a
    /// host started without one, which is the "no adapter" scenario's own condition.
    /// </summary>
    public RecordingOllamaHandler? Wire => _handler;

    /// <summary>
    /// Starts a host with the production <see cref="OllamaProvider"/> over a stubbed wire and the given
    /// accepted models and endpoints.
    /// </summary>
    public static OllamaApiHost Start(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        RecordingOllamaHandler handler,
        OllamaProviderSettings? settings = null,
        IOllamaApiKeySource? keys = null) =>
        new(
            connectionString,
            clock,
            keyFile,
            handler,
            settings ?? DefaultSettings(),
            keys,
            new LogCapture());

    /// <summary>
    /// Starts a host with the production composition root and no Ollama adapter at all.
    /// </summary>
    /// <remarks>
    /// This is what makes "the adapter is not reachable unless it was registered" a checked fact rather
    /// than a reviewer's assumption: the very same provider id, model and owner session answer
    /// <c>provider_not_installed</c> here, because the registration was removed.
    /// </remarks>
    public static OllamaApiHost StartWithoutAdapter(
        string connectionString,
        TimeProvider clock,
        string? keyFile) =>
        new(connectionString, clock, keyFile, handler: null, settings: null, keys: null, new LogCapture());

    /// <summary>The accepted models and endpoints a scenario gets unless it names its own.</summary>
    public static OllamaProviderSettings DefaultSettings() => new(
        [OllamaFixtures.KnownModel],
        [OllamaFixtures.AllowedEndpoint]);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        if (_keyFile is not null)
        {
            builder.UseSetting(SecretVaultSettings.EncryptionKeyFileSettingName, _keyFile);
        }

        if (_handler is not null)
        {
            // The accepted models and endpoints, bound the way an operator's configuration binds them
            // rather than by handing the adapter an instance, so the binder is exercised by every
            // scenario here. An empty allowlist binds no keys at all, which is the configuration an
            // operator who has named nothing actually has.
            var allowedModels = (_settings ?? DefaultSettings()).AllowedModels;

            for (var index = 0; index < allowedModels.Count; index++)
            {
                builder.UseSetting(
                    $"{OllamaProviderSettings.SectionName}:{nameof(OllamaProviderSettings.AllowedModels)}:{index}",
                    allowedModels[index]);
            }

            var allowedEndpoints = (_settings ?? DefaultSettings()).AllowedEndpoints;

            for (var index = 0; index < allowedEndpoints.Count; index++)
            {
                builder.UseSetting(
                    $"{OllamaProviderSettings.SectionName}:{nameof(OllamaProviderSettings.AllowedEndpoints)}:{index}",
                    allowedEndpoints[index]);
            }
        }

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(_logs);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);

            if (_handler is null)
            {
                // Every adapter registration this composition root carries is taken out together, so "no
                // adapter installed" is a real absence rather than a provider that answers from
                // somewhere else.
                services.RemoveAll<ILLMProvider>();
                services.RemoveAll<IOllamaApiKeySource>();
                services.RemoveAll<Chronicleaf.AI.OpenAI.IOpenAIApiKeySource>();
                services.RemoveAll<Chronicleaf.AI.Gemini.IGeminiApiKeySource>();
                services.RemoveAll<Chronicleaf.AI.Anthropic.IAnthropicApiKeySource>();
                services.RemoveAll<Chronicleaf.AI.OpenRouter.IOpenRouterApiKeySource>();
                return;
            }

            // The production adapter, rebuilt over the stubbed wire. The handler is the only
            // substitution; the adapter is the one that ships. It is registered as its own type as well
            // as against the interface, so a scenario can drive the adapter directly - which is the only
            // way to test a request the product would never build, such as one naming another slot.
            services.RemoveAll<ILLMProvider>();
            services.AddSingleton<OllamaProvider>(provider => new OllamaProvider(
                _handler,
                _keys ?? provider.GetRequiredService<IOllamaApiKeySource>(),
                _settings ?? DefaultSettings(),
                provider.GetRequiredService<ILogger<OllamaProvider>>()));
            services.AddSingleton<ILLMProvider>(provider => provider.GetRequiredService<OllamaProvider>());
        });
    }
}

/// <summary>
/// The far end of the wire: records what was sent and answers from a script the scenario wrote
/// (LLM-OLLAMA-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This is a message handler, not an <see cref="ILLMProvider"/>, and that distinction is the whole
/// point. A test double implementing the provider interface could only prove that the product maps a
/// result it was handed - which the delivered contract suite already does. Replacing the handler
/// instead means the request the adapter serialised, the header it built from a real vault lease, the
/// redirect it refused to follow and the byte cap it enforced are all the shipped code's, and the
/// only thing faked is a socket that would otherwise leave the machine.
/// </para>
/// <para>
/// <b>What it records.</b> The absolute URI, the method, the headers and the exact request body, so a
/// scenario can assert that only the consented payload was sent, that the evidence is the authorized
/// snapshot and nothing else, and that no member asking for tools is present. Reading the headers back
/// is also how a scenario proves a refusal leaked no secret: the recorded bearer value is compared
/// against the sealed credential's plaintext, which must appear in the outbound header and must not
/// appear in anything the owner can read.
/// </para>
/// <para>
/// A rule that never matches raises rather than answering, so a scenario that expected a call and did
/// not get one fails with the reason the adapter refused rather than silently passing.
/// </para>
/// </remarks>
public sealed class RecordingOllamaHandler : HttpMessageHandler
{
    private readonly List<RecordedCall> _calls = [];
    private readonly List<(Func<RecordedCall, bool> Match, Func<RecordedCall, HttpResponseMessage> Answer)> _rules = [];
    private readonly object _gate = new();

    /// <summary>Every call the adapter made, in order.</summary>
    public IReadOnlyList<RecordedCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>How many times the wire was reached. Zero means the call never left the process.</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    /// <summary>Answers every call with one official-shaped completed response.</summary>
    public RecordingOllamaHandler AnswerCompleted(
        string text,
        int? inputTokens = 512,
        int? outputTokens = 128) =>
        AnswerInOrder(
        [
            _ => Json(
                HttpStatusCode.OK,
                OllamaFixtures.CompletedBody(text, inputTokens, outputTokens)),
        ]);

    /// <summary>Answers every call with an unfinished generation carrying partial text.</summary>
    public RecordingOllamaHandler AnswerIncomplete(string partialText) =>
        AnswerInOrder([_ => Json(HttpStatusCode.OK, OllamaFixtures.IncompleteBody(partialText))]);

    /// <summary>Answers every call with a given status and an official-shaped error body.</summary>
    /// <remarks>
    /// The body is the documented error shape, not an empty response: an adapter that only classified
    /// statuses would pass this, and one that parsed an error body for a message would risk putting
    /// provider prose - which is where a key or a customer's text most often appears - into a result.
    /// </remarks>
    public RecordingOllamaHandler AnswerStatus(HttpStatusCode status, string body = OllamaFixtures.ErrorBody) =>
        AnswerInOrder([_ => Json(status, body)]);

    /// <summary>Answers the first call with one status and each later call with the next status given.</summary>
    /// <remarks>
    /// A scenario that scripts several statuses in one handler needs this rather than repeated
    /// <see cref="AnswerStatus"/> calls: the first catch-all rule would otherwise answer every call and
    /// the rest would be unreachable. A bounded retry can also consume more than one answer per
    /// generation call, so the answer is chosen by wire call index rather than by scenario iteration.
    /// </remarks>
    public RecordingOllamaHandler AnswerStatusesInOrder(params HttpStatusCode[] statuses) =>
        AnswerInOrder([.. statuses.Select(status => (Func<RecordedCall, HttpResponseMessage>)(_ => Json(status, OllamaFixtures.ErrorBody)))]);

    /// <summary>Answers every call with a body the scenario supplies verbatim.</summary>
    public RecordingOllamaHandler AnswerRaw(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        AnswerInOrder([_ => Json(status, body)]);

    /// <summary>Answers successive calls with successive raw bodies.</summary>
    /// <remarks>
    /// Each body gets its own call, so three unanswerable shapes in one handler are three separate
    /// generations rather than one answer shadowing the other two.
    /// </remarks>
    public RecordingOllamaHandler AnswerRawInOrder(params string[] bodies) =>
        AnswerInOrder([.. bodies.Select(body => (Func<RecordedCall, HttpResponseMessage>)(_ => Json(HttpStatusCode.OK, body)))]);

    /// <summary>Answers every call with a body larger than the adapter's cap.</summary>
    /// <remarks>
    /// Written as a real oversized document rather than a declared content length, because a length
    /// header is a claim and a body is the fact: this asserts the cap holds while bytes are arriving.
    /// </remarks>
    public RecordingOllamaHandler AnswerOversized()
    {
        var padding = new string('x', OllamaEndpoint.MaxResponseBytes + 1024);
        var body = OllamaFixtures.CompletedBody(
            $$"""{"padding":"{{padding}}"}""");

        return AnswerRaw(body);
    }

    private bool _blockUntilCancelled;

    /// <summary>
    /// Makes every call block until its own cancellation token fires, and then fail with
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <remarks>
    /// The block is on the handler's send rather than on the response body, because that is where a real
    /// provider spends its time: the request is on the wire and nothing has come back. A body that never
    /// streams would prove something slightly different - that the response headers did arrive - and it
    /// is also harder to unwind, because a body task that observes no token leaves the host holding an
    /// unfinished response after the scenario has finished with it. Blocking on the token the caller passed
    /// in is both the truer simulation and the one that unwinds: when the caller's budget expires, the
    /// cancellation travels the same path it would on a real socket.
    /// </remarks>
    public RecordingOllamaHandler BlockUntilCancelled()
    {
        lock (_gate)
        {
            _blockUntilCancelled = true;
        }

        return this;
    }

    /// <summary>Answers calls that match with one response and everything else with another.</summary>
    /// <remarks>
    /// Rules are tried in the order they were added and the first match wins, so a catch-all registered
    /// first would shadow every rule after it. A scenario that scripts several different answers in one
    /// handler therefore registers a single rule that varies by call index - which is what
    /// <c>AnswerInOrder</c> is for - rather than several rules that all match everything.
    /// </remarks>
    public RecordingOllamaHandler Answer(
        Func<RecordedCall, bool> matches,
        Func<RecordedCall, HttpResponseMessage> answer)
    {
        lock (_gate)
        {
            _rules.Add((matches, answer));
        }

        return this;
    }

    /// <summary>Answers the first call with the first answer, and so on; the last answer repeats.</summary>
    /// <remarks>
    /// Necessary because a bounded retry can consume more than one answer per call, and a rule list
    /// cannot express "the next call gets the next answer" - several catch-all rules would all match the
    /// first call and only the first would ever be used.
    /// </remarks>
    public RecordingOllamaHandler AnswerInOrder(
        IReadOnlyList<Func<RecordedCall, HttpResponseMessage>> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        if (answers.Count == 0)
        {
            throw new ArgumentException("At least one answer is required.", nameof(answers));
        }

        return Answer(_ => true, call =>
        {
            var index = Math.Min(call.Index, answers.Count) - 1;
            return answers[index](call);
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // The body is read before the lock, because reading it awaits and a lock cannot span an await.
        // The index is taken under the same lock that records the call, so two concurrent calls cannot
        // both be told they are the first.
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync().ConfigureAwait(false);

        RecordedCall recorded;
        Func<RecordedCall, HttpResponseMessage>? answer;
        bool block;

        lock (_gate)
        {
            recorded = RecordedCall.Capture(request, _calls.Count + 1, body);
            _calls.Add(recorded);
            block = _blockUntilCancelled;

            answer = null;
            foreach (var rule in _rules)
            {
                if (rule.Match(recorded))
                {
                    answer = rule.Answer;
                    break;
                }
            }
        }

        if (block)
        {
            // Wait for the caller's own budget, and unwind when it expires. Awaiting the delay on the
            // passed token rather than on a timer is what makes this a proof that the token reached the
            // transport: the assertion is a cancellation, not a timeout this fixture invented.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        if (answer is null)
        {
            throw new InvalidOperationException(
                $"No rule answered the call to {recorded.Method} {recorded.Uri}. "
                + "A scenario that expected a call and did not get one fails here rather than silently.");
        }

        var response = answer(recorded);

        // A stub that produced nothing is a test bug, and an empty 200 would look like a completed
        // answer with no text - which the adapter rightly refuses. Saying so here keeps the two apart.
        if (response.Content is null)
        {
            throw new InvalidOperationException("A rule produced a response with no content.");
        }

        return response;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>One call, exactly as it left the process.</summary>
    /// <param name="Index">Which call this is, counting from 1. A bounded retry is a second attempt at one request, and this is how a scenario tells the two apart.</param>
    /// <param name="Method">The HTTP method. A GET here would be a defect worth failing on.</param>
    /// <param name="Uri">The absolute URI, which is the allowlisted origin's chat resource this adapter is allowed to use.</param>
    /// <param name="Headers">Every header, so a scenario can read the credential that was sent.</param>
    /// <param name="Body">The exact bytes of the request body.</param>
    public sealed record RecordedCall(
        int Index,
        string Method,
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers,
        string Body)
    {
        /// <summary>The parsed body, so a scenario asserts on members rather than on a substring.</summary>
        public System.Text.Json.JsonElement Json() => System.Text.Json.JsonDocument.Parse(Body).RootElement.Clone();

        /// <summary>The top-level member names the request carried.</summary>
        public IReadOnlyList<string> Members =>
            [.. Json().EnumerateObject().Select(property => property.Name)];

        /// <summary>The bearer credential as it was sent, or null when there was none.</summary>
        public string? BearerKey => Headers.TryGetValue("Authorization", out var value) ? value : null;

        internal static RecordedCall Capture(HttpRequestMessage request, int index, string body)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value);
                }
            }

            return new RecordedCall(
                index,
                request.Method.Method,
                request.RequestUri ?? throw new InvalidOperationException("A call with no URI."),
                headers,
                body);
        }
    }
}
