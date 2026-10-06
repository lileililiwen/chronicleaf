using Chronicleaf.Application.Medium;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Medium;

/// <summary>
/// Registers the Medium manual-import service (MEDIUM-IMPORT-PACKAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The service over the delivered review and asset contracts, unit of work and audit trail. It
/// owns no table - the package stores nothing, so there is no migration behind this
/// registration and a database without any earlier migration still reports
/// <c>schema_mismatch</c> at readiness through the contracts this service consumes. Nothing
/// here substitutes a store.
/// </para>
/// <para>
/// <b>Nothing here can generate, approve or publish anything.</b> There is no provider, no
/// credential and no transport behind this registration: export re-serves approved bytes
/// through the review package's own Markdown and HTML exports, approval stays the review
/// package's table, and the publish route is a refusal. Registration is the whole of this
/// package's switch, and disabling it is unregistering this one service.
/// </para>
/// </remarks>
public static class MediumImportServiceCollectionExtensions
{
    public static IServiceCollection AddMediumImport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IMediumImportService, MediumImportService>();

        return services;
    }
}
