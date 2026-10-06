using Chronicleaf.Application.Content;
using Chronicleaf.Application.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Content;

/// <summary>
/// Registers the static-blog-export stores, service and job handler
/// (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as every other package's: one request store over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, one service for the request, and one handler
/// registered by its kind so the delivered registry finds it. Nothing here migrates and nothing
/// here substitutes a store, so a database without the static-blog-export migration reports
/// <c>schema_mismatch</c> at readiness and every write fails, which is the honest outcome.
/// </para>
/// <para>
/// <b>Nothing here can generate, approve or publish anything.</b> There is no provider, no
/// credential and no publisher behind this registration: the archive re-renders approved bytes
/// through the HTML export package's own sanitizing adapter, approval stays the review package's
/// table, and publication is a later package's. Registration is the whole of this package's
/// switch: a build that includes it exports, and a build that does not answers 404.
/// </para>
/// </remarks>
public static class StaticBlogExportServiceCollectionExtensions
{
    public static IServiceCollection AddStaticBlogExport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IStaticBlogExportRequestStore, PostgresStaticBlogExportRequestStore>();
        services.AddScoped<IStaticBlogExportService, StaticBlogExportService>();
        services.AddScoped<IOperationHandler, StaticBlogExportHandler>();

        return services;
    }
}
