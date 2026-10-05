using System.Net;
using System.Text;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.GitHubCandidates;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
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
/// Hosts the real application with the real queue and the real outbox dispatcher, against a real
/// disposable PostgreSQL, with a fake GitHub in place of GitHub and nothing else faked.
/// </summary>
/// <remarks>
/// <para>
/// The host is the delivered <c>WebApplicationFactory</c> pattern used by every other package here:
/// the connection string arrives through the same configuration key production reads, the access
/// configuration through its own keys. The Hangfire storage, the server, the workers, the outbox
/// dispatcher, the four candidate stores, the vault, the operation queue and the routes are all the
/// production ones, and the committed migrations are what created the tables.
/// </para>
/// <para>
/// <b>The only substitution is the transport, and it is the same seam every other package here
/// substitutes.</b> The production registration builds one <see cref="HttpClient"/> over the pinned
/// handler; this host removes that client and adds one over the shared
/// <see cref="FakeGitHubHandler"/>. It does not substitute <c>IGitHubCandidateSource</c>, does not
/// substitute <c>IGitEvidenceSource</c>, and does not touch a host, a header, a timeout or a cap - so a
/// scenario exercises this package's real routing table, real signature comparison, real key builder
/// and real status mapping, and only the bytes on the wire belong to the test.
/// </para>
/// <para>
/// The automated dispatch loop is off and the polling is fast, exactly as the delivered GitHub host
/// does it. A scenario that needs the outbox to move runs <see cref="DispatchAsync"/>, which is the
/// loop's own body called by hand, so a delivery can be placed on a chosen side of the seam between
/// "the route committed" and "the consumer has run".
/// </para>
/// </remarks>
public sealed class GithubReleaseCandidatesApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly FakeGitHubHandler _github;
    private readonly LogCapture _logs = new();

    private GithubReleaseCandidatesApiHost(
        string connectionString,
        TimeProvider clock,
        FakeGitHubHandler github,
        string keyFile)
    {
        _connectionString = connectionString;
        _clock = clock;
        _github = github;
        KeyFile = keyFile;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>The operator key file this run created, over which the webhook secret is really sealed.</summary>
    public string KeyFile { get; }

    /// <summary>The transport the application is really using, for the assertions about what it read.</summary>
    public FakeGitHubHandler GitHub => _github;

    /// <summary>What the server logged, for the assertions that a refusal said why and a secret did not leak.</summary>
    public LogCapture Logs => _logs;

    public static GithubReleaseCandidatesApiHost Start(
        string connectionString,
        TimeProvider clock,
        FakeGitHubHandler github,
        string keyFile) =>
        new(connectionString, clock, github, keyFile);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        // The dispatch loop runs only when a scenario asks it to, so a delivery can be staged either
        // side of the seam between "the route committed" and "the consumer has run".
        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:SchedulePollingInterval", "00:00:00.200");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:HeartbeatInterval", "00:00:05");

        // The real operator key file, so a webhook secret is really sealed and really opened. A test
        // that simulated the vault would prove nothing about the claim that no plaintext is stored.
        builder.UseSetting(SecretVaultSettings.EncryptionKeyFileSettingName, KeyFile);

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
            // pinned host, so a scenario asserting where a request went is reading the real routing.
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
    /// registered handler, and the candidate inbox consumer is one of them.
    /// </summary>
    /// <remarks>
    /// This is the seam that makes the two halves of the delivery route separable. A scenario that
    /// asserted only the end state could not tell a consumer that wrote candidates from a route that
    /// wrote them inline; asserting the pending outbox row after the route and the recorded receipt
    /// after the dispatch is what makes "the route answers 202 and the consumer does the work" a fact
    /// about this implementation rather than a claim.
    /// </remarks>
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
/// A signed delivery as a scenario posts it, and the machinery to post one that really is signed.
/// </summary>
/// <remarks>
/// <para>
/// The signature is produced by the <em>production</em> <c>GitHubCandidatePolicy.Sign</c>, not by a
/// helper written here. That is deliberate and it is the reason this fixture can be trusted: a
/// scenario that hard-coded an expected digest would go stale the moment the algorithm the route
/// verifies with changed, and would keep passing while the route stopped verifying anything. A fixture
/// that computed the digest a second, independent way would be a second implementation of HMAC, and
/// this suite's job is to test the one the product ships.
/// </para>
/// <para>
/// <b>Nothing here bypasses the access boundary.</b> A delivery is posted with no session cookie, no
/// <c>Origin</c> and no antiforgery value, which is exactly what GitHub sends, and the scenario that
/// uses it asserts the route's own authentication rather than the delivered one.
/// </para>
/// </remarks>
public sealed record Delivery(
    string Event,
    string DeliveryId,
    string Body,
    string? Secret = null,
    string? Signature = null,
    string? Action = null)
{
    /// <summary>
    /// A delivery GitHub would send: an event header, a delivery id, a body and a correct signature.
    /// </summary>
    public static Delivery Signed(string @event, string deliveryId, string body, string secret, string? action = null) =>
        new(@event, deliveryId, body, secret, GitHubCandidatePolicy.Sign(Encoding.UTF8.GetBytes(body), secret), action);

    /// <summary>
    /// A delivery with a signature that is well formed and wrong, which is the shape of an attempt
    /// that has the format right and the secret wrong.
    /// </summary>
    public static Delivery WronglySigned(
        string @event,
        string deliveryId,
        string body,
        string secret,
        string wrongSecret) =>
        new(
            @event,
            deliveryId,
            body,
            secret,
            GitHubCandidatePolicy.Sign(Encoding.UTF8.GetBytes(body), wrongSecret));

    /// <summary>The request as an HTTP message, with every header GitHub's own delivery carries.</summary>
    public HttpRequestMessage ToRequest(Guid connectionId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/webhooks/github/{connectionId:D}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Body)),
        };

        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", Event);
        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", DeliveryId);

        if (Signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", Signature);
        }

        return request;
    }

    /// <summary>
    /// The same delivery with its body changed after the signature was computed.
    /// </summary>
    /// <remarks>
    /// The scenario for a tampered body: a valid signature over the bytes GitHub sent, and a body
    /// somebody edited in flight. If the route re-serialised the payload before comparing - or parsed
    /// it and compared a model - this would authenticate, and the recorded candidate would be a fact
    /// about a document nobody signed.
    /// </remarks>
    public Delivery WithBodyAfterSigning(string newBody) =>
        this with { Body = newBody };
}
