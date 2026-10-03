using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Media;
using Chronicleaf.Infrastructure.Evidence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chronicleaf.Infrastructure.Evidence;

/// <summary>
/// Wires the milestone evidence services (MILESTONE-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The order of the two registrations below is the point of this file. The media package registers
/// its reference checker with <c>TryAdd</c> and a default that answers "nothing references this",
/// because at the time it shipped no reference table existed. This package is what creates that
/// table, so it replaces the default outright - <c>RemoveAll</c> first, then the real one - which
/// makes the composition order irrelevant: the checker here is the one in the container whether the
/// media package is registered before or after it.
/// </para>
/// <para>
/// Replacing rather than decorating is what keeps the answer honest. A checker that asked the
/// default and then also asked the evidence table would be two answers to one question, and the
/// default's "no" would have to be interpreted rather than believed. There is one implementation of
/// this seam, and it reads the database.
/// </para>
/// <para>
/// Unregistering the two evidence services is the rollback this design asks for: the tables and the
/// append-only triggers stay, no route reaches them, and the media package falls back to asking the
/// seam it registered itself. Nothing else in the application is changed.
/// </para>
/// </remarks>
public static class MilestoneEvidenceServiceCollectionExtensions
{
    public static IServiceCollection AddMilestoneEvidence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IEvidenceStore, PostgresEvidenceStore>();
        services.AddScoped<MilestoneEvidenceService>();
        services.AddScoped<IAppendEvidence>(provider => provider.GetRequiredService<MilestoneEvidenceService>());
        services.AddScoped<IReadEvidence>(provider => provider.GetRequiredService<MilestoneEvidenceService>());

        // The seam the media package left open, now answered by the table this package owns.
        services.RemoveAll<IMediaReferenceChecker>();
        services.AddScoped<IMediaReferenceChecker, PostgresEvidenceMediaReferenceChecker>();

        return services;
    }
}
