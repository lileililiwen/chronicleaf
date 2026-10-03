using Chronicleaf.AI.Contracts;

namespace Chronicleaf.Application.AI;

/// <summary>
/// Finds the adapter for a provider slot, if this deployment has one
/// (LLM-PROVIDER-CONTRACT-001).
/// </summary>
/// <remarks>
/// <para>
/// The registry is the seam the five adapter packages plug into, and in this package it is
/// deliberately empty. Production registers no <see cref="ILLMProvider"/> at all, so
/// <see cref="Installed"/> is empty, every slot reports no adapter, and a generation request for any
/// provider is refused with <see cref="ProviderNotInstalledException"/> before a lease, a socket or a
/// token is involved. That is the honest state of a contract with no transports, and a test asserts
/// it rather than trusting it.
/// </para>
/// <para>
/// A test double is registered here and only in test DI. Because the resolver reads whatever the
/// composition root provided, "the double is not reachable in production" is a property of the
/// production registration and therefore something a test can state: a host built without it answers
/// 422 for the very same provider id the double-serving host answers 200 for.
/// </para>
/// </remarks>
public interface ILLMProviderResolver
{
    /// <summary>
    /// The adapter for a slot, or <c>null</c> when this deployment has none for it. A slot name that
    /// is not one of <see cref="GenerationProviderIds"/> always answers <c>null</c>.
    /// </summary>
    ILLMProvider? Find(string? providerId);

    /// <summary>The slot names this deployment has an adapter for.</summary>
    IReadOnlyList<string> Installed { get; }
}

/// <summary>
/// The resolver over the adapters the composition root registered.
/// </summary>
/// <remarks>
/// Built once from the service collection, and it holds the adapters for the lifetime of the
/// process. An adapter is a transport, not a per-request object: it holds a client, not a scope, and
/// building a new one per request would mean a new HTTP client per request. It is also required to
/// be thread-safe, which is why the interface says so.
/// </remarks>
public sealed class LLMProviderResolver : ILLMProviderResolver
{
    private readonly IReadOnlyDictionary<string, ILLMProvider> _byProvider;

    public LLMProviderResolver(IEnumerable<ILLMProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var byProvider = new Dictionary<string, ILLMProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (provider is null)
            {
                continue;
            }

            if (!GenerationProviderIds.IsSupported(provider.ProviderId))
            {
                // An adapter that claims a slot this contract does not have is a wiring mistake that
                // would otherwise sit in the registry until a request happened to name it.
                throw new InvalidOperationException(
                    $"An adapter reported the provider slot '{provider.ProviderId}', which is not a GenerationProviderIds value.");
            }

            // First registration wins. Two adapters for one slot is ambiguous, and silently letting
            // the last one overwrite would make which provider a scope actually talks to depend on
            // DI ordering.
            byProvider.TryAdd(provider.ProviderId, provider);
        }

        _byProvider = byProvider;
    }

    public IReadOnlyList<string> Installed => [.. _byProvider.Keys.Order(StringComparer.Ordinal)];

    public ILLMProvider? Find(string? providerId) =>
        providerId is not null && _byProvider.TryGetValue(providerId, out var provider) ? provider : null;
}
