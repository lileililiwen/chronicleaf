using System.Net;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.GitHub.GraphQL;
using Chronicleaf.Infrastructure.GitHub;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real application with the real queue running, against a real disposable PostgreSQL,
/// with a fake enrichment transport in place of GitHub and nothing else faked.
/// </summary>
/// <remarks>
/// <para>
/// The host is the delivered <c>WebApplicationFactory</c> pattern: the connection string arrives
/// through the same configuration key production reads, the access configuration through its own
/// keys. The Hangfire storage, the server, the workers, the outbox dispatcher, the stores, the
/// vault, the evidence append and the routes are all the production ones, including the
/// github-evidence connection and sync the enrichment scenarios seed through.
/// </para>
/// <para>
/// <b>The only substitution is the transport.</b> The production registration builds one
/// <see cref="HttpClient"/> over the pinned handler; this host removes that client and adds one
/// over a <see cref="FakeEnrichmentTransport"/>. It does not reconfigure the production handler,
/// does not substitute <see cref="IGitHubRelationshipReader"/> and does not touch a host, a
/// timeout or a header - so a scenario exercises this package's real classification, cap
/// enforcement, SHA validation and revision binding, and only the bytes on the wire are the test's.
/// </para>
/// </remarks>
public sealed class GithubGraphqlEnrichmentApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly FakeEnrichmentTransport _transport;
    private readonly LogCapture _logs = new();

    private GithubGraphqlEnrichmentApiHost(
        string connectionString,
        TimeProvider clock,
        FakeEnrichmentTransport transport,
        string? keyFile)
    {
        _connectionString = connectionString;
        _clock = clock;
        _transport = transport;
        KeyFile = keyFile;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>The operator key file this run created, or <c>null</c> when this host has none.</summary>
    public string? KeyFile { get; }

    /// <summary>The transport the application is really using, for the assertions about what it read.</summary>
    public FakeEnrichmentTransport Transport => _transport;

    /// <summary>What the server logged, for the assertions that a refusal said why.</summary>
    public LogCapture Logs => _logs;

    public static GithubGraphqlEnrichmentApiHost Start(
        string connectionString,
        TimeProvider clock,
        FakeEnrichmentTransport transport,
        string? keyFile = null) =>
        new(connectionString, clock, transport, keyFile);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:SchedulePollingInterval", "00:00:00.200");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:HeartbeatInterval", "00:00:05");

        if (KeyFile is not null)
        {
            builder.UseSetting(SecretVaultSettings.EncryptionKeyFileSettingName, KeyFile);
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

            // The one substitution: the transport, and nothing else. The base address is still the
            // pinned host, so a scenario asserting where a request went is reading the real routing
            // rather than a host a test chose.
            services.RemoveAll<HttpClient>();
            services.AddSingleton(_ => new HttpClient(_transport, disposeHandler: false)
            {
                BaseAddress = new Uri(GitHubTransport.BaseAddress),
                Timeout = GitHubTransport.Timeout,
            });
        });
    }

    /// <summary>
    /// Runs one real outbox dispatch pass: the delivered dispatcher hands each pending event to the
    /// registered handler, which enqueues a real job through the real <c>IJobDispatcher</c>.
    /// </summary>
    public async Task<IReadOnlyList<OutboxDispatchResult>> DispatchAsync(CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        var stamper = scope.ServiceProvider.GetRequiredService<IOutboxDispatchStamper>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var results = await dispatcher.DispatchPendingAsync(100, cancellationToken);

        foreach (var result in results)
        {
            if (result.Outcome == OutboxDispatchOutcome.Handled)
            {
                await stamper.TryMarkDispatchedAsync(result.EventId, clock.GetUtcNow(), cancellationToken);
            }
        }

        return results;
    }
}

/// <summary>
/// A fake GitHub for enrichment scenarios, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="HttpMessageHandler"/> that records every request it is given and answers from
/// a script the test wrote. GET rules match a path suffix; GraphQL rules match the
/// <c>variables.oid</c> and <c>variables.after</c> of the posted body, so a scenario can answer one
/// commit with data and another with errors. A request nothing matches is answered 404, so an
/// unexpected request is a visible failure rather than a silent pass.
/// </para>
/// <para>
/// <see cref="BeforeGraphQlResponse"/> runs before a POST is answered. A scenario sets it to stage
/// the bundle moving mid-query: the job has already read the head and is inside the GraphQL read
/// when the gate holds it, so releasing the gate after a manual append proves the compare-and-set
/// append refuses rather than overwrites.
/// </para>
/// </remarks>
public sealed class FakeEnrichmentTransport : HttpMessageHandler
{
    private readonly List<GetRule> _getRules = [];
    private readonly List<GetRule> _getOneShot = [];
    private readonly List<GraphQlRule> _graphQlRules = [];
    private readonly List<GraphQlRule> _graphQlOneShot = [];
    private readonly List<RecordedRequest> _requests = [];
    private readonly object _gate = new();

    /// <summary>Every request the client made, in order, with the host it went to and the body it carried.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>Runs before a GraphQL POST is answered. Null unless a scenario stages a mid-query move.</summary>
    public Func<Task>? BeforeGraphQlResponse { get; set; }

    /// <summary>Adds a rule that answers a GET whose path ends with <paramref name="pathSuffix"/>.</summary>
    public FakeEnrichmentTransport OnGet(string pathSuffix, HttpStatusCode status, string body = "{}") =>
        OnGet(pathSuffix, status, body, new Dictionary<string, string>());

    /// <summary>Adds a GET rule with response headers, for the rate-limit headers GitHub sends.</summary>
    public FakeEnrichmentTransport OnGet(
        string pathSuffix,
        HttpStatusCode status,
        string body,
        IReadOnlyDictionary<string, string> headers)
    {
        lock (_gate)
        {
            _getRules.Add(new GetRule(pathSuffix, status, body, headers));
        }

        return this;
    }

    /// <summary>Adds a GET rule that answers a path suffix with a JSON body and 200.</summary>
    public FakeEnrichmentTransport OnGet(string pathSuffix, string json) =>
        OnGet(pathSuffix, HttpStatusCode.OK, json);

    /// <summary>
    /// Adds a GraphQL rule that answers a POST for <paramref name="oid"/> (or any commit when
    /// <c>null</c>) at cursor <paramref name="after"/> (first page when <c>null</c>).
    /// </summary>
    public FakeEnrichmentTransport OnGraphQl(
        string? oid,
        string? after,
        HttpStatusCode status,
        string body,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        lock (_gate)
        {
            _graphQlRules.Add(new GraphQlRule(oid, after, status, body, headers ?? new Dictionary<string, string>()));
        }

        return this;
    }

    /// <summary>Adds a GraphQL rule that answers the first matching POST and then removes itself.</summary>
    public FakeEnrichmentTransport OnGraphQlOnce(
        string? oid,
        string? after,
        HttpStatusCode status,
        string body,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        lock (_gate)
        {
            _graphQlOneShot.Add(new GraphQlRule(oid, after, status, body, headers ?? new Dictionary<string, string>()));
        }

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var host = request.RequestUri?.Host ?? string.Empty;
        var authorization = request.Headers.Authorization?.ToString();
        var method = request.Method.Method;
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Func<Task>? gate = null;
        lock (_gate)
        {
            _requests.Add(new RecordedRequest(method, host, path, authorization, body));

            if (string.Equals(method, "POST", StringComparison.Ordinal) &&
                path.EndsWith("/graphql", StringComparison.Ordinal))
            {
                gate = BeforeGraphQlResponse;
            }
        }

        if (gate is not null)
        {
            await gate().ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (string.Equals(method, "POST", StringComparison.Ordinal) &&
                path.EndsWith("/graphql", StringComparison.Ordinal))
            {
                var (oid, after) = ReadVariables(body);

                var oneShot = _graphQlOneShot.LastOrDefault(candidate =>
                    (candidate.Oid is null || string.Equals(candidate.Oid, oid, StringComparison.Ordinal)) &&
                    (candidate.After is null || string.Equals(candidate.After, after, StringComparison.Ordinal)));
                if (oneShot is not null)
                {
                    _graphQlOneShot.Remove(oneShot);
                }

                var rule = oneShot ?? _graphQlRules.LastOrDefault(candidate =>
                    (candidate.Oid is null || string.Equals(candidate.Oid, oid, StringComparison.Ordinal)) &&
                    (candidate.After is null || string.Equals(candidate.After, after, StringComparison.Ordinal)));

                if (rule is null)
                {
                    return NotFound();
                }

                return Answer(rule.Status, rule.Body, rule.Headers);
            }

            var getOneShot = _getOneShot.LastOrDefault(candidate =>
                path.EndsWith(candidate.PathSuffix, StringComparison.Ordinal));
            if (getOneShot is not null)
            {
                _getOneShot.Remove(getOneShot);
            }

            var get = getOneShot ?? _getRules.LastOrDefault(candidate =>
                path.EndsWith(candidate.PathSuffix, StringComparison.Ordinal));

            return get is null ? NotFound() : Answer(get.Status, get.Body, get.Headers);
        }
    }

    private static (string? Oid, string? After) ReadVariables(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var variables = document.RootElement.GetProperty("variables");
            var oid = variables.TryGetProperty("oid", out var oidNode) &&
                oidNode.ValueKind is JsonValueKind.String
                ? oidNode.GetString()
                : null;
            var after = variables.TryGetProperty("after", out var afterNode) &&
                afterNode.ValueKind is JsonValueKind.String
                ? afterNode.GetString()
                : null;

            return (oid, after);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"message":"Not Found","documentation_url":"https://docs.github.com"}""",
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage Answer(
        HttpStatusCode status,
        string body,
        IReadOnlyDictionary<string, string> headers)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        foreach (var header in headers)
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return response;
    }

    private sealed record GetRule(
        string PathSuffix,
        HttpStatusCode Status,
        string Body,
        IReadOnlyDictionary<string, string> Headers);

    private sealed record GraphQlRule(
        string? Oid,
        string? After,
        HttpStatusCode Status,
        string Body,
        IReadOnlyDictionary<string, string> Headers);

    /// <summary>One request the client made, kept whole so a scenario can assert on every part of it.</summary>
    /// <param name="Method">The HTTP method. Enrichment posts; the seeded sync gets.</param>
    /// <param name="Host">The host the request went to.</param>
    /// <param name="Path">The path, without the host.</param>
    /// <param name="Authorization">The Authorization header, or <c>null</c>. Never a real token.</param>
    /// <param name="Body">The posted body, or empty for a GET.</param>
    public sealed record RecordedRequest(
        string Method,
        string Host,
        string Path,
        string? Authorization,
        string Body);
}
