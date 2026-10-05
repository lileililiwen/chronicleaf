using Chronicleaf.AI.Anthropic;
using Chronicleaf.Application.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Api.AI;

/// <summary>
/// Supplies the Anthropic adapter's key from the delivered credential vault
/// (LLM-ANTHROPIC-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is the only place in the repository that knows both sides of the seam.</b> The adapter
/// declares that it needs "a key, for a scope, briefly" and the vault knows how a sealed credential
/// becomes a leased plaintext buffer; neither knows about the other, so an adapter can be written,
/// tested and shipped without the vault and the vault can be replaced without touching a transport.
/// </para>
/// <para>
/// It lives in the composition root rather than in either project because that is the honest place for
/// a bridge: it is wiring, not behaviour, and neither side should depend on it.
/// </para>
/// <para>
/// <b>What crosses the boundary is bytes and a scope.</b> The vault's lease is read once, copied into
/// the adapter's own lease, and returned - so the vault's buffer is zeroed the moment the copy exists
/// and the adapter's buffer is zeroed when the call is over. Neither buffer is ever converted to a
/// string here: the value that reaches the HTTP header is built by the adapter from its own span, and
/// this type never sees a <c>string</c> containing a secret.
/// </para>
/// <para>
/// The scope and provider come from the lease the vault itself reports, not from the arguments. That is
/// deliberate: the adapter re-checks them before reading a byte, and a check that read its expectations
/// from the same place it is checking would not be a check.
/// </para>
/// <para>
/// <b>Why it opens a scope per call.</b> The adapter is a singleton - a transport holding one HTTP
/// client - while <see cref="ISecretVault"/> is scoped over the unit of work and the store. Taking the
/// vault as a constructor dependency would mean a singleton capturing a scoped service, which the
/// container refuses and which would be a lifetime bug even where it did not: the vault's DbContext would
/// belong to whatever request happened to resolve the adapter first. So the scope is created here, per
/// lease, and disposed with it - which is also what makes the lease short-lived by construction rather
/// than by convention.
/// </para>
/// </remarks>
public sealed class VaultAnthropicApiKeySource(IServiceScopeFactory scopes) : IAnthropicApiKeySource
{
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));

    /// <inheritdoc />
    /// <remarks>
    /// The provider recorded on the stored credential is the one that is carried across, so an adapter
    /// asked for a key that belongs to another provider is refused by its own check rather than by
    /// something this type decided to enforce on its behalf.
    /// </remarks>
    public async Task<AnthropicApiKeyLease> AcquireAsync(
        Guid scopeId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        // The scope exists for the length of the lease and no longer. Opening it inside the call rather
        // than at construction is what keeps a singleton from holding one.
        using var scope = _scopes.CreateScope();

        var vault = scope.ServiceProvider.GetRequiredService<ISecretVault>();
        using var lease = await vault
            .ResolveAsync(SecretRef.To(credentialId), cancellationToken)
            .ConfigureAwait(false);

        // The vault's own buffer is copied once and dropped with the lease above, so the plaintext
        // exists in exactly one live buffer by the time this method returns.
        var key = lease.Secret.ToArray();

        return new AnthropicApiKeyLease(lease.ScopeId, lease.Reference.Value, lease.Provider, key);
    }
}
