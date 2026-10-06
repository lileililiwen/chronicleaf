using Chronicleaf.Application.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chronicleaf.Infrastructure.Media.S3;

/// <summary>
/// Registers the S3-compatible media backend, and only in S3 storage mode
/// (CLOUD-OBJECT-STORAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The shape mirrors every other package's: scoped stores over the delivered
/// <c>ChronicleafDbContext</c>, one service instance per request, and no migration here - the
/// schema this package needs arrives with the forward migration whose id readiness requires.
/// </para>
/// <para>
/// In Local mode this method registers nothing at all: no S3 client is built, no vault credential
/// is leased, and no S3 network call can happen, which is what keeps a Community self-host free of
/// the S3 backend by construction rather than by configuration. In S3 mode it validates the
/// operator's endpoint, bucket and credential reference at start-up - a deployment that names S3
/// without pointing it anywhere fails before serving - and replaces <see cref="IMediaStore"/> with
/// the router that serves each record from the backend its own provider names. The Local store
/// stays registered underneath, because rows the migration has not reached yet still live on disk.
/// </para>
/// <para>
/// The credential source itself is registered by the composition root, next to the provider key
/// sources: it is wiring between the vault and this backend, and neither side should depend on it.
/// </para>
/// </remarks>
public static class ChronicleafCloudMediaServiceCollectionExtensions
{
    /// <summary>
    /// Adds the S3 backend when the installation selects it, and nothing otherwise.
    /// </summary>
    public static IServiceCollection AddChronicleafCloudMedia(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = S3MediaOptions.FromConfiguration(configuration);

        // Registered in every mode, validated only in S3 mode: the composition root's credential
        // bridge takes these options, and a Local host must still build. An unconfigured value here
        // is inert - nothing in Local mode reads it, and S3 mode refuses to start below.
        services.TryAddSingleton(options);

        if (!MediaSettings.FromConfiguration(configuration).IsS3Mode)
        {
            // The route still resolves in Local mode, and answers that presigns do not exist here
            // rather than failing to build. No client, no credential and no network call accompanies
            // this registration.
            services.TryAddSingleton<IS3MediaLinkService, UnconfiguredS3MediaLinkService>();

            return services;
        }

        options.Validate();

        services.TryAddScoped<IS3BackendFactory, S3BackendFactory>();
        services.TryAddScoped<S3MediaStore>();
        services.TryAddScoped<IMediaMigrationRepository, PostgresMediaMigrationRepository>();
        services.TryAddScoped<IMediaMigrationService, MediaMigrationService>();
        services.TryAddScoped<IS3MediaLinkService, S3MediaLinkService>();

        // Replaced, not wrapped: in S3 mode the router answers, and the Local store it routes to
        // is the same single instance the Local registration serves everywhere else.
        services.AddScoped<IMediaStore>(provider =>
            new ProviderRoutingMediaStore(
                provider.GetRequiredService<LocalMediaStore>(),
                provider.GetRequiredService<S3MediaStore>(),
                provider.GetRequiredService<IMediaRepository>(),
                provider.GetRequiredService<MediaSettings>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProviderRoutingMediaStore>>()));

        return services;
    }
}
