using Chronicleaf.Application.Media;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Platform.Storage.Contracts;
using Platform.Storage.Local;

namespace Chronicleaf.Infrastructure.Media;

/// <summary>
/// Registers the media store, the storage provider and the MediaRoot
/// (LOCAL-MEDIA-STORAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The shape is the same as every other package's: scoped stores over the delivered
/// <c>ChronicleafDbContext</c>, the delivered unit of work and audit writer, and one service instance
/// per request. Nothing here migrates, nothing here substitutes a store, and there is no in-memory
/// provider anywhere - a database that has not been migrated reports <c>schema_mismatch</c> at
/// readiness, which is the honest outcome.
/// </para>
/// <para>
/// The storage provider is registered through the adopted platform's own
/// <c>AddPlatformStorage</c> seam, so <see cref="IObjectStorage"/> is the platform's interface and not
/// one declared here. It is registered as a factory rather than by type because the Local adapter
/// takes its root as a constructor argument, and that root is the single resolved
/// <see cref="MediaRoot"/> - never a value from a request. Because the platform registers it with
/// <c>TryAddScoped</c>, a deployment that later supplies a different adapter replaces this one
/// without editing this file.
/// </para>
/// <para>
/// <see cref="MediaRoot"/> is resolved once, at start-up, and fails the process when it is
/// unusable. That is the whole reason the root is not a per-request option: there is no code path on
/// which a request, a header or a body can influence where bytes are read from or written to.
/// </para>
/// </remarks>
public static class ChronicleafMediaServiceCollectionExtensions
{
    /// <summary>
    /// Adds the media store over the Local filesystem adapter and the shared storage contract.
    /// </summary>
    public static IServiceCollection AddChronicleafMedia(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath,
        string webRootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var settings = MediaSettings.FromConfiguration(configuration);
        services.TryAddSingleton(settings);

        // Resolved eagerly and exactly once. A root that is inside the published wwwroot, or that
        // cannot be created, fails the start rather than producing a store that quietly serves every
        // stored image as a static asset.
        services.TryAddSingleton(provider => MediaRoot.Resolve(
            settings,
            contentRootPath,
            webRootPath,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<MediaRoot>()));

        services.TryAddSingleton<IMediaIdSource, NewMediaIdSource>();

        // The platform's own interface, registered directly as a factory because the Local adapter
        // takes its root as a constructor argument and that root is the single resolved MediaRoot -
        // never a value from a request. StorageOptions carries this package's byte limit into the
        // provider, which is what makes the provider refuse an oversized object itself rather than
        // only being checked afterwards. A deployment that later supplies a different adapter
        // replaces this registration without editing this file.
        services.AddScoped<IObjectStorage>(provider => new LocalFileStorage(
            provider.GetRequiredService<MediaRoot>().Root,
            new StorageOptions { MaximumObjectBytes = settings.MaximumByteLength }));

        // Replaced, not wrapped, by whichever package owns references. See IMediaReferenceChecker.
        services.TryAddScoped<IMediaReferenceChecker, NoMediaReferenceChecker>();

        services.AddScoped<IMediaRepository, PostgresMediaRepository>();

        // Registered as itself as well as behind the store contract, so the S3-mode router routes
        // to the same single instance rather than to a second one. Serving IMediaStore still
        // answers this instance in Local mode, exactly as before.
        services.AddScoped<IMediaStore>(provider => provider.GetRequiredService<LocalMediaStore>());
        services.TryAddScoped<LocalMediaStore>();

        return services;
    }
}
