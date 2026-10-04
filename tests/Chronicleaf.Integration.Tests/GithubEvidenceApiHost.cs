using System.Net;
using System.Text;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.GitHub.Evidence;
using Chronicleaf.Infrastructure.GitHub;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real application with the real queue running, against a real disposable PostgreSQL, with
/// a fake GitHub in place of GitHub and nothing else faked.
/// </summary>
/// <remarks>
/// <para>
/// The host is the delivered <c>WebApplicationFactory</c> pattern: the connection string arrives
/// through the same configuration key production reads, the access configuration through its own
/// keys. The Hangfire storage, the server, the workers, the outbox dispatcher, the stores, the vault,
/// the evidence append and the routes are all the production ones.
/// </para>
/// <para>
/// <b>The only substitution is the transport.</b> The production registration builds one
/// <see cref="HttpClient"/> over the pinned handler; this host removes that client and adds one over
/// a <see cref="FakeGitHubHandler"/>. It does not reconfigure the production handler, does not
/// substitute <see cref="IGitEvidenceSource"/> and does not touch a host, a timeout or a header - so
/// a scenario exercises this package's real classification, cap enforcement and ref resolution, and
/// only the bytes on the wire are the test's.
/// </para>
/// <para>
/// The automated dispatch loop is off and the polling is fast, exactly as the background-jobs host
/// does it: the loop is the seam between "the request committed" and "the queue knows", and a
/// scenario has to be able to place the collection on a chosen side of it. A scenario that needs the
/// loop runs <see cref="DispatchAsync"/>, which is the loop's own body called by hand.
/// </para>
/// </remarks>
public sealed class GithubEvidenceApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly FakeGitHubHandler _github;
    private readonly LogCapture _logs = new();

    private GithubEvidenceApiHost(
        string connectionString,
        TimeProvider clock,
        FakeGitHubHandler github,
        string? keyFile)
    {
        _connectionString = connectionString;
        _clock = clock;
        _github = github;
        KeyFile = keyFile;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>The operator key file this run created, or <c>null</c> when this host has none.</summary>
    public string? KeyFile { get; }

    /// <summary>The transport the application is really using, for the assertions about what it read.</summary>
    public FakeGitHubHandler GitHub => _github;

    /// <summary>What the server logged, for the assertions that a refusal said why.</summary>
    public LogCapture Logs => _logs;

    public static GithubEvidenceApiHost Start(
        string connectionString,
        TimeProvider clock,
        FakeGitHubHandler github,
        string? keyFile = null) =>
        new(connectionString, clock, github, keyFile);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        // The dispatch loop runs only when a scenario asks it to, so a collection can be staged either
        // side of the seam between "the request committed" and "the queue knows".
        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");

        // A poll interval, not a behaviour: the queue, the workers and the claiming are production's.
        builder.UseSetting($"{OperationQueueOptions.SectionName}:SchedulePollingInterval", "00:00:00.200");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:HeartbeatInterval", "00:00:05");

        if (KeyFile is not null)
        {
            builder.UseSetting(SecretVaultSettings.EncryptionKeyFileSettingName, KeyFile);
        }

        // The server's own log is captured so a scenario can read what the server said when the
        // envelope alone is opaque. It is the same capture the credential-vault host uses.
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
            services.AddSingleton(_ => new HttpClient(_github, disposeHandler: false)
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
/// A fake GitHub, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="HttpMessageHandler"/> that records every request it is given and answers from a
/// script the test wrote. It is the seam an HTTP client has, and it is the only thing in this
/// assembly that stands in for anything: the API host, the operation queue, the Hangfire server, the
/// stores, the vault and the evidence append are all the real ones running against a real PostgreSQL.
/// </para>
/// <para>
/// The script is a list of rules rather than a fixed conversation, because the client's own order of
/// requests is part of what a scenario asserts: resolving the base ref before the head ref, resolving
/// both before the comparison, and never asking for a second host. A rule that never matches turns
/// into a 404, so an unexpected request is a visible failure rather than a silent pass.
/// </para>
/// <para>
/// <b>What this fake is not.</b> It does not stand in for a credential, a repository that really
/// exists, or a GitHub answer. A scenario that needs those is the opt-in public-repository run, which
/// talks to the real API and is reported as prerequisite-unavailable when it cannot.
/// </para>
/// </remarks>
public sealed class FakeGitHubHandler : HttpMessageHandler
{
    private readonly List<Rule> _rules = [];
    private readonly List<Rule> _oneShot = [];
    private readonly List<RecordedRequest> _requests = [];
    private readonly object _gate = new();

    /// <summary>Every request the client made, in order, with the host it went to.</summary>
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

    /// <summary>Adds a rule that answers a request whose path ends with <paramref name="pathSuffix"/>.</summary>
    public FakeGitHubHandler OnGet(string pathSuffix, HttpStatusCode status, string body = "{}") =>
        OnGet(pathSuffix, status, body, new Dictionary<string, string>());

    /// <summary>Adds a rule with response headers, for the rate-limit headers GitHub sends.</summary>
    public FakeGitHubHandler OnGet(
        string pathSuffix,
        HttpStatusCode status,
        string body,
        IReadOnlyDictionary<string, string> headers)
    {
        lock (_gate)
        {
            _rules.Add(new Rule(pathSuffix, status, body, headers));
        }

        return this;
    }

    /// <summary>Adds a rule that answers a path suffix with a JSON body and 200.</summary>
    public FakeGitHubHandler OnGet(string pathSuffix, string json) =>
        OnGet(pathSuffix, HttpStatusCode.OK, json);

    /// <summary>
    /// Adds a rule that answers the first matching request and then removes itself.
    /// </summary>
    /// <remarks>
    /// This is how a scenario says "the first attempt is throttled and the second is not" without
    /// writing a stateful fake: the rule is consumed by the request it answered, so a retry reads the
    /// script the scenario wrote for it rather than a value the fake invented mid-run.
    /// </remarks>
    public FakeGitHubHandler OnGetOnce(
        string pathSuffix,
        HttpStatusCode status,
        string body = "{}",
        IReadOnlyDictionary<string, string>? headers = null)
    {
        lock (_gate)
        {
            _oneShot.Add(new Rule(pathSuffix, status, body, headers ?? new Dictionary<string, string>()));
        }

        return this;
    }

    /// <summary>
    /// Forgets every rule and every recorded request, so a scenario can re-script the transport
    /// between two phases. The connection read and the collection are two different moments and a
    /// scenario sometimes needs the repository to answer for one and not the other.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _rules.Clear();
            _oneShot.Clear();
            _requests.Clear();
        }
    }

    /// <summary>Forgets every request recorded so far, so a scenario can assert on one phase alone.</summary>
    public void ClearRequests()
    {
        lock (_gate)
        {
            _requests.Clear();
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var host = request.RequestUri?.Host ?? string.Empty;
        var authorization = request.Headers.Authorization?.ToString();
        var userAgent = string.Join(' ', request.Headers.UserAgent.Select(agent => agent.ToString()));

        lock (_gate)
        {
            _requests.Add(new RecordedRequest(
                request.Method.Method,
                $"https://{host}{path}",
                path,
                host,
                authorization,
                userAgent,
                request.Headers.Accept.Select(accept => accept.ToString()).ToArray(),
                request.Headers.TryGetValues("X-GitHub-Api-Version", out var apiVersion)
                    ? apiVersion.First()
                    : null));

            // A one-shot rule is consumed by the request it answers, and a standing rule still wins
            // over it, so a scenario can say "the first attempt only" without a stateful fake.
            var oneShot = _oneShot.LastOrDefault(candidate => path.EndsWith(candidate.PathSuffix, StringComparison.Ordinal));
            if (oneShot is not null)
            {
                _oneShot.Remove(oneShot);
            }

            var rule = oneShot ?? _rules.LastOrDefault(candidate => path.EndsWith(candidate.PathSuffix, StringComparison.Ordinal));

            if (rule is null)
            {
                // A request the script did not expect is answered the way GitHub answers something
                // that is not there, and the recorded request is what the scenario's assertion fails on.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{"message":"Not Found","documentation_url":"https://docs.github.com"}""",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            var response = new HttpResponseMessage(rule.Status)
            {
                Content = new StringContent(rule.Body, Encoding.UTF8, "application/json"),
            };

            foreach (var header in rule.Headers)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return Task.FromResult(response);
        }
    }

    private sealed record Rule(
        string PathSuffix,
        HttpStatusCode Status,
        string Body,
        IReadOnlyDictionary<string, string> Headers);

    /// <summary>One request the client made, kept whole so a scenario can assert on every part of it.</summary>
    /// <param name="Method">The HTTP method. This package only ever issues GET; a scenario asserts that.</param>
    /// <param name="Url">The full URL the request went to, host included.</param>
    /// <param name="Path">The path, without the host.</param>
    /// <param name="Host">The host the request went to, which is the property the redirect test turns on.</param>
    /// <param name="Authorization">The Authorization header, or <c>null</c>. Never a real token.</param>
    /// <param name="UserAgent">The user agent the client sent.</param>
    /// <param name="Accept">The Accept values the client sent.</param>
    /// <param name="ApiVersion">The API version header, or <c>null</c>.</param>
    public sealed record RecordedRequest(
        string Method,
        string Url,
        string Path,
        string Host,
        string? Authorization,
        string UserAgent,
        IReadOnlyList<string> Accept,
        string? ApiVersion);
}
