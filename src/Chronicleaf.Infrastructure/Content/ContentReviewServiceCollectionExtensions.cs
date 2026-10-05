using Chronicleaf.Application.Content;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Content;

/// <summary>
/// Registers the review stores and service (MARKDOWN-REVIEW-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as every other package's: one store over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, one service for the use cases. Nothing here
/// generates, reaches a provider or migrates: a database without the review migration reports
/// <c>schema_mismatch</c> at readiness and every write fails, which is the honest outcome.
/// </para>
/// <para>
/// <b>Nothing here can publish anything.</b> There is no publisher registration and no webhook,
/// because an export serves bytes to the owner that asked for them. Publication is a later
/// package's reviewed adapter, bound through this same approval table.
/// </para>
/// </remarks>
public static class ContentReviewServiceCollectionExtensions
{
    public static IServiceCollection AddMarkdownReviewExport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IContentReviewStore, PostgresContentReviewStore>();
        services.AddSingleton<IContentExportAdapter, HtmlExportAdapter>();
        services.AddScoped<IContentReviewService, ContentReviewService>();

        return services;
    }
}
