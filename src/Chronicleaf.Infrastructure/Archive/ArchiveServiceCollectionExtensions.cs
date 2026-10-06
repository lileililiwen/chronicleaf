using Chronicleaf.Application.Archive;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Archive;

/// <summary>
/// Registers the hosted-archive-operations stores and services (HOSTED-ARCHIVE-OPERATIONS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here migrates and nothing here substitutes a store.</b> A database without the
/// hosted-archive-operations migration reports <c>schema_mismatch</c> at readiness and every
/// write fails, which is the honest outcome rather than a silently unrecoverable archive.
/// </para>
/// <para>
/// <b>No name here is taken.</b> The two stores, the digest reader and the two services are all
/// new registrations under names of their own: the journey revocation, the schedule disable and
/// the media retirements all go through the delivered services, and this package adds no
/// transport, no hosted loop and no second copy of a delivered contract. The operator's backup
/// runner and purge pass are methods the operator invokes, not loops this process runs.
/// </para>
/// </remarks>
public static class ArchiveServiceCollectionExtensions
{
    public static IServiceCollection AddHostedArchiveOperations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The key version is read once here rather than through IOptions in the service, so the
        // Application project takes no new package: the rehearsal compares against this value,
        // and an operator value below one fails the deployment at start-up rather than the first
        // rehearsal at an owner's expense.
        var backupOptions = new ArchiveBackupOptions();
        configuration.GetSection(ArchiveBackupOptions.SectionName).Bind(backupOptions);
        if (backupOptions.CurrentEncryptionKeyVersion < 1)
        {
            throw new InvalidOperationException(
                "Archive:CurrentEncryptionKeyVersion is one or greater.");
        }

        services.AddSingleton(backupOptions);

        services.AddScoped<IArchiveTombstoneStore, PostgresArchiveTombstoneStore>();
        services.AddScoped<IArchiveBackupManifestStore, PostgresArchiveBackupManifestStore>();
        services.AddScoped<IArchiveDigestReader, PostgresArchiveDigestReader>();
        services.AddScoped<IArchiveBackupService, ArchiveBackupService>();
        services.AddScoped<IArchiveRetentionService, ArchiveRetentionService>();

        return services;
    }
}
