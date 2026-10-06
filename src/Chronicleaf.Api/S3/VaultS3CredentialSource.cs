using Chronicleaf.Application.Media;
using Chronicleaf.Application.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Api.S3;

/// <summary>
/// Supplies the S3 backend's key pair from the delivered credential vault
/// (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is the only place in the repository that knows both sides of the seam.</b> The
/// backend declares that it needs "keys, briefly" and the vault knows how a sealed credential
/// becomes a leased plaintext buffer; neither knows about the other, so the backend can be built
/// and tested without the vault and the vault can be replaced without touching object storage.
/// </para>
/// <para>
/// It lives in the composition root rather than in either project because that is the honest place
/// for a bridge: it is wiring, not behaviour, and neither side should depend on it.
/// </para>
/// <para>
/// <b>What crosses the boundary is bytes, not strings.</b> The vault's lease is read once, copied
/// into this package's own lease, and returned - so the vault's buffer is zeroed the moment the
/// copy exists and this package's buffer is zeroed when the backend is disposed. Neither buffer is
/// ever converted to a string here: the key strings the AWS client needs are built by the backend
/// from its own span, and this type never sees one.
/// </para>
/// <para>
/// The scope and provider come from the lease the vault itself reports, not from the arguments.
/// The reference comes from the operator's configuration rather than from a row, so there is no
/// per-request scope to recheck against - the configuration is trusted - but the stored provider is
/// still the vault's own answer, and a reference that names another provider's credential is
/// refused by the backend's own check rather than by something this type decided.
/// </para>
/// <para>
/// <b>Why it opens a scope per call.</b> Backends are scoped but short-lived, while
/// <see cref="ISecretVault"/> is scoped over the unit of work and the store. Taking the vault as a
/// constructor dependency would capture whatever scope happened to resolve the backend first, which
/// would be a lifetime bug even where the container allowed it. So the scope is created here, per
/// lease, and disposed with it - which is also what makes the lease short-lived by construction
/// rather than by convention.
/// </para>
/// </remarks>
public sealed class VaultS3CredentialSource(
    IServiceScopeFactory scopes,
    S3MediaOptions options) : IS3CredentialSource
{
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly S3MediaOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task<S3CredentialLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            throw new MediaStorageUnavailableException(
                "The S3 object storage backend is not configured for this installation.");
        }

        // The scope exists for the length of the lease and no longer. Opening it inside the call
        // rather than at construction is what keeps a backend from holding one.
        using var scope = _scopes.CreateScope();

        var vault = scope.ServiceProvider.GetRequiredService<ISecretVault>();
        using var lease = await vault
            .ResolveAsync(SecretRef.To(_options.CredentialId), cancellationToken)
            .ConfigureAwait(false);

        // The vault's own buffer is copied once and dropped with the lease above, so the plaintext
        // exists in exactly one live buffer by the time this method returns.
        var document = lease.Secret.ToArray();

        return new S3CredentialLease(lease.ScopeId, lease.Reference.Value, lease.Provider, document);
    }
}
