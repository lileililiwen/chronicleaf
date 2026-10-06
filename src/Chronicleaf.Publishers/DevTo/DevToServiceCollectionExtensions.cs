using Chronicleaf.Application.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Publishers.DevTo;

/// <summary>
/// Registers the Dev.to reviewed publishing adapter with a composition root
/// (PUBLISHER-DEVTO-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// One call, and it adds exactly one <see cref="IPublisherAdapter"/> for the <c>devto</c> slot,
/// its registry descriptor beside it, and the one transport the adapter owns. Everything else
/// about a publication - the approval, the preview hash, the attempt row, the operation, the
/// credential's revision, the audit row - belongs to the packages that already shipped it, and
/// none of it is reachable from here.
/// </para>
/// <para>
/// <b>This is the whole rollback lever, and demand is the switch.</b> An absent section
/// registers nothing: the target answers <c>publisher_unknown_target</c>, exactly as the
/// contract says an unclaimed target does, and stored attempts, receipts and credentials stay
/// where they are. Removing the registration un-installs the transport the same way. There is
/// no flag that un-installs an adapter at runtime, because a target that silently stops being
/// reachable in the middle of an owner's workflow is a worse failure than one that was never
/// there. A present-but-invalid section fails startup instead: a target that cannot validate
/// must not wait until a publish to say so.
/// </para>
/// <para>
/// The transport is registered as its own singleton rather than behind a bare
/// <see cref="HttpMessageHandler"/>: a bare handler is already taken by the GitHub Pages
/// transport, and a second one would make single-handler resolution order-dependent. The
/// transport owns its client, pinned to the Dev.to host with redirects refused.
/// </para>
/// </remarks>
public static class DevToServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Dev.to adapter for the <c>devto</c> slot when the operator configured an
    /// explicit target, and nothing when they did not.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// The configuration the explicit target binds from. It is the only configuration this
    /// package reads, and an absent section registers no adapter.
    /// </param>
    public static IServiceCollection AddDevToPublisher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read twice on purpose: the first read is the demand gate - and fails startup on a
        // present-but-invalid section - while Configure keeps the bound target reloadable, so a
        // reconfiguration after a preview changes what the next preview describes instead of
        // authorizing a write under tags the owner never confirmed.
        var initial = DevToOptions.Read(configuration);

        if (initial is null)
        {
            return services;
        }

        services.Configure<DevToOptions>(configuration.GetSection(DevToOptions.SectionName));
        services.AddSingleton<DevToTransport>();
        services.AddSingleton(new PublisherTargetDescriptor(
            DevToPublisher.TargetId,
            SupportsDraft: true,
            SupportsPublish: true,
            SupportsReconcile: false,
            ExportOnly: false));
        services.AddSingleton<IPublisherAdapter, DevToPublisher>();

        return services;
    }
}
