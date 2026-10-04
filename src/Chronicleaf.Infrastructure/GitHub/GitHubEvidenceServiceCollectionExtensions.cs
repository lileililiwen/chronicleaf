using System.Net;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Application.Operations;
using Chronicleaf.GitHub.Evidence;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.GitHub;

/// <summary>
/// How the GitHub transport is built in production (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The host is pinned here, not by a request and not by configuration.</b> There is no setting,
/// no environment variable and no connection string that names a host, so there is no value an
/// operator - or a request body - could supply to send the next request somewhere else. The two
/// repository name segments are the only things a caller contributes to a URL.
/// </para>
/// <para>
/// <b>Redirects are not followed.</b> This is the one property of the handler that matters most: the
/// client attaches an <c>Authorization</c> header when a credential has been leased, and a redirect
/// to another host is the construct that would carry that header there. Automatic redirects are
/// therefore off, cookies are off so no state crosses a host either, and the client treats any 3xx as
/// a failure and reports it as one. A GitHub outage that answers with a redirect produces a
/// retryable refusal rather than a token posted somewhere new.
/// </para>
/// <para>
/// Thirty seconds is the per-request ceiling. A request that outlives it is a transport failure, and
/// the delivered job policy - not this value - decides whether it is retried.
/// </para>
/// </remarks>
public static class GitHubTransport
{
    /// <summary>How long one GitHub request may take before the transport gives up on it.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The pinned API host. A repository is addressed under it and nowhere else.</summary>
    public const string BaseAddress = "https://api.github.com";

    /// <summary>
    /// The real transport handler: no automatic redirects, no cookies, and a pinned API version on
    /// every request. Returns a new instance, so a test can hold one and read its settings rather
    /// than inferring them from a call it made.
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
}

/// <summary>
/// Registers the GitHub evidence stores, service and job handler (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// The same shape as every other package's: two scoped stores over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, one service for the request, and one handler
/// registered by its kind so the delivered registry finds it. Nothing here migrates and nothing here
/// substitutes a store, so a database without the github-evidence migration reports
/// <c>schema_mismatch</c> at readiness and every write fails, which is the honest outcome.
/// </remarks>
/// <remarks>
/// <b>The transport is a singleton built once from the pinned handler</b>, because an
/// <see cref="HttpClient"/> holds a connection pool rather than a scope and is required to be
/// thread-safe. A test replaces the whole client - never a header, a host or a timeout inside it - so
/// the only thing a double can stand in for is GitHub itself.
/// </remarks>
public static class GitHubEvidenceServiceCollectionExtensions
{
    public static IServiceCollection AddGitHubEvidence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IGitHubConnectionStore, PostgresGitHubConnectionStore>();
        services.AddScoped<ISyncSourceStore, PostgresSyncSourceStore>();
        services.AddScoped<IGitHubEvidenceService, GitHubEvidenceService>();
        services.AddScoped<IOperationHandler, GitHubEvidenceSyncHandler>();

        // The real transport, pinned to the API host with redirects refused. A test removes this and
        // adds its own HttpClient over a fake handler; it does not reconfigure this one, so what runs
        // in production and what a test reads about production are the same registration.
        services.AddSingleton(provider => new HttpClient(GitHubTransport.CreateHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri(GitHubTransport.BaseAddress),
            Timeout = GitHubTransport.Timeout,
        });

        services.AddSingleton<IGitEvidenceSource>(provider => new GitHubEvidenceClient(
            provider.GetRequiredService<HttpClient>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));

        return services;
    }
}
