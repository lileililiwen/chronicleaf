using Chronicleaf.Application.Access;
using Chronicleaf.Application.Tenancy;
using Chronicleaf.Infrastructure.Access;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Tenancy;

/// <summary>
/// Registers the Hosted tenant boundary (CLOUD-TENANT-BOUNDARY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Registration reads the tenancy mode and refuses to build a Hosted boundary it cannot
/// enforce: in Hosted without an issuer and a client id the process fails at start-up with a
/// redacted message. In Community the registration still lands, so the schema, the decorator
/// and the routes exist in both modes - but every Hosted-only path refuses, and the owner
/// boundary behaves exactly as before.
/// </para>
/// <para>
/// The decorator is registered after the access package's own <see
/// cref="IOwnerAccessService"/>, so it wins for every consumer while the concrete
/// <see cref="PostgresOwnerAccessService"/> stays available for the throttle contract. The
/// transport is a singleton built once from its own handler, like every other transport in
/// this repository: a test replaces the whole client, never a header or a timeout inside it.
/// </para>
/// </remarks>
public static class TenancyServiceCollectionExtensions
{
    public static IServiceCollection AddCloudTenantBoundary(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = TenancySettings.Read(configuration);
        TenancySettings.EnsureUsable(settings);

        services.AddSingleton(settings);

        // The real OIDC transport, holding its own pooled client. The client is deliberately not
        // a shared registration: three delivered transports already resolve a bare HttpClient,
        // and a second one would make every consumer's resolution depend on registration order.
        // A test removes this transport and adds one over a fake handler; it does not reconfigure
        // this one, so what runs in production and what a test reads about production are the
        // same class over different bytes on the wire.
        services.AddSingleton<IOidcTransport>(_ => new OidcHttpTransport(
            new HttpClient(OidcHttpTransport.CreateHandler(), disposeHandler: true)
            {
                Timeout = OidcHttpTransport.Timeout,
            }));

        services.AddScoped<TenancyService>();
        services.AddScoped<ITenancyService>(provider => provider.GetRequiredService<TenancyService>());

        // Last registration wins: every consumer of the access contract - the boundary
        // middleware and every endpoint's scope resolution - now goes through the Hosted
        // decorator, which delegates everything in Community. The decorator is request-scoped
        // like the service it wraps, so the session it validated is the session it resolves.
        services.AddScoped<IOwnerAccessService>(provider => new HostedOwnerAccessDecorator(
            provider.GetRequiredService<PostgresOwnerAccessService>(),
            provider.GetRequiredService<TenancyService>(),
            provider.GetRequiredService<TenancySettings>()));

        return services;
    }
}
