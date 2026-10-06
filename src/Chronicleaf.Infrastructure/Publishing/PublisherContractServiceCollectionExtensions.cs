using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Publishing;

/// <summary>
/// Registers the publisher contract: the adapter registry, the attempt store, the reviewed
/// publication service and its durable job handler (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// One service over the delivered asset, approval, evidence, vault and queue contracts, plus
/// this package's own attempt-row store. Nothing here migrates and nothing here substitutes a
/// store: attempts live in the <c>publication_attempts</c> table this package's own migration
/// adds, so a database without it reports <c>schema_mismatch</c> at readiness and every
/// preview fails, which is the honest outcome.
/// </para>
/// <para>
/// <b>Nothing here can reach a remote side.</b> The registry is a plain singleton of
/// descriptors - the two export-only targets and whatever each transport package contributes
/// beside its adapter - and there is no provider, no HttpClient and no transport behind this
/// registration: the six transport adapters arrive in their own packages, and until one does
/// its target answers that nothing is registered. Registration is the whole of this package's
/// switch.
/// </para>
/// </remarks>
public static class PublisherContractServiceCollectionExtensions
{
    public static IServiceCollection AddPublisherContract(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(PublisherTargetDescriptor.Medium);
        services.AddSingleton(PublisherTargetDescriptor.LinkedIn);
        services.AddSingleton<IPublisherRegistry, PublisherRegistry>();
        services.AddScoped<IPublicationAttemptStore, PostgresPublicationAttemptStore>();
        services.AddScoped<IPublisherService, PublisherService>();
        services.AddScoped<IOperationHandler, PublisherPublicationHandler>();

        return services;
    }
}
