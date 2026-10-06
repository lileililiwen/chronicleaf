using Chronicleaf.Application.LinkedIn;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.LinkedIn;

/// <summary>
/// Registers the LinkedIn copy service (LINKEDIN-COPY-PACKAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The service over the delivered stores, unit of work and audit trail, plus this package's own
/// selection-row store. Nothing here migrates and nothing here substitutes a store: copies are
/// new rows in the drafts package's own <c>content_assets</c> and <c>content_revisions</c>
/// tables with their pictures in <c>linkedin_copy_media</c>, so a database without the
/// LinkedIn-copy migration reports <c>schema_mismatch</c> at readiness and every adaptation
/// fails, which is the honest outcome.
/// </para>
/// <para>
/// <b>Nothing here can generate, approve or publish anything.</b> There is no provider, no
/// credential and no transport behind this registration: adaptation re-renders an approved
/// story's accepted summary through a deterministic template, approval stays the review
/// package's table, and the publish route is a refusal. Registration is the whole of this
/// package's switch.
/// </para>
/// </remarks>
public static class LinkedInCopyServiceCollectionExtensions
{
    public static IServiceCollection AddLinkedInCopy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ILinkedInCopyMediaStore, PostgresLinkedInCopyMediaStore>();
        services.AddScoped<ILinkedInCopyService, LinkedInCopyService>();

        return services;
    }
}
