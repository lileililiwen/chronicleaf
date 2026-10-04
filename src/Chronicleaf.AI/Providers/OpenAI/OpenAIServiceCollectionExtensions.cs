using System.Net;
using Chronicleaf.AI.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.AI.OpenAI;

/// <summary>
/// Registers the OpenAI BYOK transport with a composition root
/// (LLM-OPENAI-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// One call, and it adds exactly one <see cref="ILLMProvider"/>. Everything else about a generation -
/// the consent, the bounds, the evidence, the credential's revision, the audit row - belongs to the
/// packages that already shipped it, and none of it is reachable from here.
/// </para>
/// <para>
/// <b>This is the whole rollback lever, and it is deliberately not a setting.</b> Removing the
/// registration disables the transport and leaves stored previews, stored credentials and stored audit
/// rows exactly where they are; there is no flag that un-installs an adapter at runtime, because a
/// flag would make "which providers can this deployment reach" depend on a value rather than on what
/// was built and deployed - and a provider that silently stops being reachable in the middle of an
/// owner's workflow is a worse failure than one that was never there.
/// </para>
/// <para>
/// The <see cref="HttpMessageHandler"/> is built here rather than taken from a caller so the two
/// properties that are safety properties are not negotiable: no redirect is ever followed, and a
/// redirect that arrives anyway is a refusal rather than a second hop. The timeout is left at
/// <see cref="HttpClient.Timeout"/>'s infinite default on purpose - the per-call budget is the
/// caller's cancellation token, and a second clock here would be a way for a call to outlive the
/// authorization that paid for it.
/// </para>
/// </remarks>
public static class OpenAIServiceCollectionExtensions
{
    /// <summary>
    /// Adds the OpenAI adapter for the <c>openai</c> provider slot.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// The configuration the accepted-model allowlist binds from. It is the only configuration this
    /// package reads, and an absent section is an empty allowlist, which refuses every model.
    /// </param>
    public static IServiceCollection AddOpenAIProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(OpenAIProviderSettings.SectionName);
        var settings = new OpenAIProviderSettings(
            section.GetSection(nameof(OpenAIProviderSettings.AllowedModels)).Get<string[]>());

        services.TryAddSingleton(settings);

        // Registered as itself rather than through a factory so a host that also needs the settings -
        // a test asserting the allowlist, or a future settings surface - resolves the same instance
        // the adapter was built with.
        services.TryAddSingleton<ILLMProvider>(provider => new OpenAIProvider(
            BuildHandler(),
            provider.GetRequiredService<IOpenAIApiKeySource>(),
            provider.GetRequiredService<OpenAIProviderSettings>(),
            provider.GetRequiredService<ILogger<OpenAIProvider>>()));

        return services;
    }

    /// <summary>The pooled handler every call goes through.</summary>
    private static HttpMessageHandler BuildHandler() =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
        };
}
