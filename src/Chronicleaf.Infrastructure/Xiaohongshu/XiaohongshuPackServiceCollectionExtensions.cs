using Chronicleaf.Application.Xiaohongshu;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Xiaohongshu;

/// <summary>
/// Registers the Xiaohongshu pack service (XIAOHONGSHU-CONTENT-PACKAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The service over the delivered stores, unit of work and audit trail, plus this package's own
/// selection-row store. Nothing here migrates and nothing here substitutes a store: packs are
/// new rows in the drafts package's own <c>content_assets</c> and <c>content_revisions</c>
/// tables with their pictures in <c>xiaohongshu_pack_media</c>, so a database without the
/// Xiaohongshu-pack migration reports <c>schema_mismatch</c> at readiness and every adaptation
/// fails, which is the honest outcome.
/// </para>
/// <para>
/// <b>Nothing here can generate, approve or publish anything.</b> There is no provider, no
/// credential, no account and no transport behind this registration: adaptation re-renders an
/// approved story's accepted summary through a deterministic template, approval stays the review
/// package's table, and the publish route is a refusal. Registration is the whole of this
/// package's switch.
/// </para>
/// </remarks>
public static class XiaohongshuPackServiceCollectionExtensions
{
    public static IServiceCollection AddXiaohongshuPack(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IXiaohongshuPackMediaStore, PostgresXiaohongshuPackMediaStore>();
        services.AddScoped<IXiaohongshuPackService, XiaohongshuPackService>();

        return services;
    }
}
