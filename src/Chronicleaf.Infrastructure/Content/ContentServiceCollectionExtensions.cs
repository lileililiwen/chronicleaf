using Chronicleaf.Application.Content;
using Chronicleaf.Application.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Content;

/// <summary>
/// Registers the grounded-draft stores, service and job handler
/// (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as every other package's: two stores over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, one service for the request, and one handler registered
/// by its kind so the delivered registry finds it. Nothing here migrates and nothing here substitutes a
/// store, so a database without the grounded-markdown-drafts migration reports <c>schema_mismatch</c> at
/// readiness and every write fails, which is the honest outcome.
/// </para>
/// <para>
/// <b>No provider is registered here, and nothing about a provider is configured.</b> The adapter
/// packages own that, through the same <c>ILLMProvider</c> seam the delivered resolver already reads, and
/// this registration adds no key, no model list and no setting. A preview naming a slot with no adapter
/// is refused by the delivered contract before a job is ever created, and the job's own resolver
/// reports the same absence as a permanent failure rather than retrying it. So an untouched
/// installation lists the slots honestly and cannot reach any of them.
/// </para>
/// <para>
/// <b>Nothing here can approve anything.</b> There is no approval registration to remove and no
/// configuration that would install one, because approval is the review package's table bound to a
/// revision and a body hash. Registration is the whole of this package's switch: a build that includes
/// it drafts, and a build that does not answers 404.
/// </para>
/// </remarks>
public static class ContentServiceCollectionExtensions
{
    public static IServiceCollection AddGroundedMarkdownDrafts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IContentAssetStore, PostgresContentAssetStore>();
        services.AddScoped<IDraftRequestStore, PostgresDraftRequestStore>();
        services.AddScoped<IContentDraftService, ContentDraftService>();
        services.AddScoped<IOperationHandler, ContentDraftHandler>();

        return services;
    }
}
