using Chronicleaf.Application.Agency;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Agency;

/// <summary>
/// Registers the agency-client-projects store and service (AGENCY-CLIENT-PROJECTS-001/002).
/// </summary>
/// <remarks>
/// The same shape as every other package's: one client store over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, and one service for clients, assignments and
/// report contexts. Nothing here migrates and nothing here substitutes a store, so a database
/// without the agency-client-projects migration reports <c>schema_mismatch</c> at readiness and
/// every write fails, which is the honest outcome.
/// <para>
/// <b>Nothing here is a portal, an invoice or a login.</b> There is no credential, no team
/// membership and no outbound mail behind this registration: a client is organizational context
/// over the owner's own records, the single-user owner boundary stays the only identity, and
/// clients never widen what the owner may read. Registration is the whole of this package's
/// switch: a build that includes it serves clients, and a build that does not answers 404.
/// </para>
/// </remarks>
public static class AgencyClientServiceCollectionExtensions
{
    public static IServiceCollection AddAgencyClients(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IClientStore, PostgresClientStore>();
        services.AddScoped<IAgencyClientService, AgencyClientService>();

        return services;
    }
}
