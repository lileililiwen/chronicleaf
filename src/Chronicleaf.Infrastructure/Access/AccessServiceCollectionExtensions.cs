using Chronicleaf.Application.Access;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Access;

/// <summary>
/// Registers the single-user access boundary.
/// </summary>
/// <remarks>
/// Registration reads the operator configuration and refuses to build a boundary it cannot
/// enforce: outside Development a missing or unusable access token hash fails start-up with a
/// redacted message. The database is never migrated here, and no substitute store is ever
/// installed - a deployment that has not applied the access migration reports
/// <c>schema_mismatch</c> at <c>GET /api/ready</c> and cannot accept a login, because every
/// session write would fail.
/// </remarks>
public static class AccessServiceCollectionExtensions
{
    public static IServiceCollection AddChronicleafAccess(
        this IServiceCollection services,
        IConfiguration configuration,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = AccessSettings.Read(configuration, environmentName);
        AccessSettings.EnsureUsable(settings);

        services.AddSingleton(settings);

        // One scoped instance serves both contracts, so the session store and the throttle can
        // never see different connection state inside a single request.
        services.AddScoped<PostgresOwnerAccessService>();
        services.AddScoped<IOwnerAccessService>(provider => provider.GetRequiredService<PostgresOwnerAccessService>());
        services.AddScoped<ILoginThrottle>(provider => provider.GetRequiredService<PostgresOwnerAccessService>());

        // One guard instance serves every caller, so the start-up application and the login path
        // cannot disagree about whether the configured token has been applied.
        services.AddSingleton<AccessTokenRotationGuard>();
        services.AddSingleton<IAccessTokenRotationGuard>(provider => provider.GetRequiredService<AccessTokenRotationGuard>());

        return services;
    }
}
