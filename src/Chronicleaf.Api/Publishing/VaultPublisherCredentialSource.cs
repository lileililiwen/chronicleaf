using Chronicleaf.Application.Publishing;
using Chronicleaf.Application.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Api.Publishing;

/// <summary>
/// Supplies the publisher's credential from the delivered credential vault
/// (PUBLISHER-CONTRACT-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is the only place in the repository that knows both sides of the seam.</b> The
/// publisher contract declares that it needs "a credential, for a scope, briefly" and the vault
/// knows how a sealed credential becomes a leased plaintext buffer; neither knows about the other,
/// so a transport can be written, tested and shipped without the vault and the vault can be
/// replaced without touching a publisher.
/// </para>
/// <para>
/// It lives in the composition root rather than in either project because that is the honest place for
/// a bridge: it is wiring, not behaviour, and neither side should depend on it.
/// </para>
/// <para>
/// <b>What crosses the boundary is bytes and a scope.</b> The credential id is resolved fresh from
/// the vault's own metadata on every call - never carried in an attempt row, a queue payload or a
/// log line - and the lease is held for the one adapter call only. The lease is never converted to a
/// string here: whatever the adapter builds from its span is the adapter's own concern, and this
/// type never sees a <c>string</c> containing a secret.
/// </para>
/// <para>
/// <b>Why it opens a scope per call.</b> This source is a singleton, while <see cref="ISecretVault"/>
/// is scoped over the unit of work and the store. Taking the vault as a constructor dependency would
/// mean a singleton capturing a scoped service, which the container refuses and which would be a
/// lifetime bug even where it did not. So the scope is created here, per lease, and disposed with
/// the call - which is also what makes the lease short-lived by construction rather than by
/// convention. The returned lease owns its own buffer and outlives the scope; disposing the scope
/// releases the store, not the bytes.
/// </para>
/// </remarks>
public sealed class VaultPublisherCredentialSource(IServiceScopeFactory scopes) : IPublisherCredentialSource
{
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));

    /// <inheritdoc />
    public async Task<SecretLease> AcquireAsync(Guid scopeId, CancellationToken cancellationToken = default)
    {
        // The scope exists for the length of the acquisition and no longer. Opening it inside the
        // call rather than at construction is what keeps a singleton from holding one.
        using var scope = _scopes.CreateScope();

        var vault = scope.ServiceProvider.GetRequiredService<ICredentialVaultService>();

        var status = await vault
            .DescribeAsync(scopeId, PublisherService.CredentialProvider, cancellationToken)
            .ConfigureAwait(false);

        if (!status.Configured || status.Id is null)
        {
            throw new PublisherCredentialMissingException();
        }

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretVault>();

        var lease = await secrets
            .ResolveAsync(SecretRef.To(status.Id.Value), cancellationToken)
            .ConfigureAwait(false);

        // The id came from this scope's own metadata, so a lease for another scope or another
        // provider here means the row moved under the read - and a credential that moved is not
        // opened, it is refused.
        if (lease.ScopeId != scopeId ||
            !string.Equals(lease.Provider, PublisherService.CredentialProvider, StringComparison.Ordinal))
        {
            lease.Dispose();
            throw new PublisherCredentialMissingException();
        }

        return lease;
    }
}
