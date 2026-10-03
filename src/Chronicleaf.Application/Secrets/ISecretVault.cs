namespace Chronicleaf.Application.Secrets;

/// <summary>
/// The whole of what leaves the vault: an opaque reference to a stored secret
/// (CREDENTIAL-VAULT-001).
/// </summary>
/// <remarks>
/// The reference is the credential's own id and nothing else. It is what a queue payload, an
/// operation row, a log line and a job handoff carry, so a reader of any of those learns that a
/// credential exists for a provider and nothing about its value.
/// </remarks>
/// <param name="Value">The credential id.</param>
public sealed record SecretRef(Guid Value)
{
    /// <summary>A reference to a credential that does not exist. Never carried across a call.</summary>
    public static SecretRef None { get; } = new(Guid.Empty);

    /// <summary>True when this reference names something.</summary>
    public bool IsPresent => Value != Guid.Empty;

    /// <summary>A reference to the given credential.</summary>
    /// <exception cref="ArgumentException">The id is empty.</exception>
    public static SecretRef To(Guid credentialId)
    {
        if (credentialId == Guid.Empty)
        {
            throw new ArgumentException("A secret reference names a credential.", nameof(credentialId));
        }

        return new SecretRef(credentialId);
    }
}

/// <summary>
/// Stores, opens and removes provider credentials (CREDENTIAL-VAULT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Three operations, deliberately: an owner may put a secret, the application may resolve one, and an
/// owner may delete one. There is no read that returns a <c>string</c>, no peek, no "verify" and no
/// export - the only way to obtain a secret is to lease its bytes and return the lease, which is
/// what keeps a secret from being copied into a log, a queue payload, an error or a result column.
/// </para>
/// <para>
/// Nothing here takes a key, a path, an algorithm or a tag from a caller. The key comes from the
/// operator's key file and every failure to obtain it is a refusal, never a fallback: there is no
/// code path that stores a secret in the clear, that substitutes a sentinel, or that reports success
/// without having sealed the bytes.
/// </para>
/// <para>
/// Scope is not a parameter of this contract, so a caller that holds only a <see cref="SecretRef"/>
/// learns nothing about which scope the credential belongs to until the lease says so. Callers
/// compare <see cref="SecretLease.ScopeId"/> with the scope they are acting under before reading a
/// byte, which is the recheck a queued job has to perform after the ref was serialized.
/// </para>
/// </remarks>
public interface ISecretVault
{
    /// <summary>
    /// Seals <paramref name="secret"/> under the operator's current key and stores it for
    /// <paramref name="scopeId"/>, replacing whatever was stored for that scope and provider.
    /// </summary>
    /// <param name="scopeId">The installation scope the credential belongs to.</param>
    /// <param name="provider">One of the supported provider identifiers.</param>
    /// <param name="secret">The value to store. Not logged, not returned, not persisted in the clear.</param>
    /// <param name="cancellationToken">Cancelled on owner request or shutdown.</param>
    /// <returns>The reference a later resolve or delete names.</returns>
    /// <exception cref="Common.ValidationFailedException">
    /// The provider is not supported, or the secret is empty or over the accepted length.
    /// </exception>
    /// <exception cref="SecretVaultUnavailableException">The operator's key could not be obtained.</exception>
    /// <exception cref="Common.RevisionConflictException">
    /// The stored credential moved to another revision first, so nothing was written.
    /// </exception>
    Task<SecretRef> PutAsync(
        Guid scopeId,
        string provider,
        string secret,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the credential named by <paramref name="secretRef"/> and leases its bytes.
    /// </summary>
    /// <param name="secretRef">The stored reference.</param>
    /// <param name="cancellationToken">Cancelled on owner request or shutdown.</param>
    /// <returns>A lease the caller must return. Disposing it zeroes the bytes.</returns>
    /// <exception cref="SecretVaultUnavailableException">
    /// The operator's key could not be obtained, or the stored bytes no longer authenticate against
    /// the key, the scope, the provider, the id and the key version they were sealed with.
    /// </exception>
    /// <exception cref="CredentialMissingException">There is no such credential.</exception>
    Task<SecretLease> ResolveAsync(SecretRef secretRef, CancellationToken cancellationToken = default);

    /// <summary>Removes the credential named by <paramref name="secretRef"/>.</summary>
    /// <param name="secretRef">The stored reference.</param>
    /// <param name="cancellationToken">Cancelled on owner request or shutdown.</param>
    /// <exception cref="CredentialMissingException">There is no such credential.</exception>
    Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default);
}
