using Chronicleaf.Application.ContentVariants;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Content;

/// <summary>
/// Registers the content-variant service (CONTENT-ASSET-VARIANTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// One service over the delivered stores, unit of work and audit trail. Nothing here migrates and
/// nothing here substitutes a store: variants are new rows in the drafts package's own
/// <c>content_assets</c> and <c>content_revisions</c> tables, so a database without the
/// grounded-markdown-drafts migration reports <c>schema_mismatch</c> at readiness and every derive
/// fails, which is the honest outcome.
/// </para>
/// <para>
/// <b>Nothing here can generate, approve or publish anything.</b> There is no provider, no
/// credential and no publisher behind this registration: deriving re-renders an approved story's
/// accepted summary through a deterministic template, approval stays the review package's table,
/// and publication is a later package's. Registration is the whole of this package's switch.
/// </para>
/// </remarks>
public static class ContentVariantServiceCollectionExtensions
{
    public static IServiceCollection AddContentAssetVariants(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IContentVariantService, ContentVariantService>();

        return services;
    }
}
