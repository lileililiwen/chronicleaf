using Chronicleaf.Core;

namespace Chronicleaf.Application.Secrets;

/// <summary>
/// What a stored credential looks like from the outside: when it exists, when it changed and which
/// revision replaces it (CREDENTIAL-VAULT-002).
/// </summary>
/// <remarks>
/// There is no property here that can hold a secret, and there is no field for a masked prefix, a
/// length, a hash or a last-four: a settings surface that showed any of those would be displaying
/// something derived from the secret, and "is it configured and when did it change" is the whole
/// question a caller has.
/// </remarks>
/// <param name="Id">The credential id, which is also the reference a job carries.</param>
/// <param name="Provider">The provider this credential is for.</param>
/// <param name="UpdatedAt">When the owner last saved it.</param>
/// <param name="Revision">The revision an <c>If-Match</c> must carry to replace it.</param>
/// <param name="KeyVersion">The key version the stored bytes are bound to.</param>
public sealed record CredentialMetadata(
    Guid Id,
    string Provider,
    DateTimeOffset UpdatedAt,
    long Revision,
    int KeyVersion)
{
    /// <summary>The reference a job or a delete names this credential by.</summary>
    public SecretRef Reference => SecretRef.To(Id);
}

/// <summary>
/// One page of the scope's configured providers, and the cursor that continues it
/// (CREDENTIAL-VAULT-002).
/// </summary>
/// <param name="Credentials">The page's metadata, ordered by provider.</param>
/// <param name="NextCursor">The cursor for the following page, or null at the end of the list.</param>
public sealed record CredentialPage(
    IReadOnlyList<CredentialMetadata> Credentials,
    string? NextCursor);

/// <summary>
/// Reads credential metadata, and nothing else, for one scope (CREDENTIAL-VAULT-002).
/// </summary>
/// <remarks>
/// Every method is scope-bound and every one of them is refused rather than widened when the scope
/// does not match: a credential in another scope reads as absent, not as a value with a different
/// owner attached. The store has no method that returns ciphertext and no method that returns a
/// plaintext, so nothing that is not metadata can leave it; the vault is the only thing that reads
/// the sealed bytes, and the vault returns them as a lease and not as a string.
/// </remarks>
public interface ICredentialStore
{
    /// <summary>The metadata for one provider, or null when this scope has none configured.</summary>
    Task<CredentialMetadata?> FindAsync(
        Guid scopeId,
        string provider,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The metadata for one credential id, or null when this scope has no such credential. A foreign
    /// id is absent here too, so a caller cannot learn that it exists elsewhere.
    /// </summary>
    Task<CredentialMetadata?> FindByIdAsync(
        Guid scopeId,
        Guid credentialId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of this scope's configured providers, ordered by provider, starting after the
    /// provider the previous page ended on.
    /// </summary>
    /// <param name="scopeId">The installation scope to read.</param>
    /// <param name="afterProvider">The provider the previous page ended on, or null to start.</param>
    /// <param name="limit">How many rows to return at most.</param>
    /// <param name="cancellationToken">Cancelled on owner request or shutdown.</param>
    Task<IReadOnlyList<CredentialMetadata>> ListAsync(
        Guid scopeId,
        string? afterProvider,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a credential that this scope does not have for that provider. Returns false when a row
    /// is already there, which is a lost race rather than a success.
    /// </summary>
    Task<bool> InsertAsync(Credential credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a credential's sealed bytes only when the stored revision is exactly
    /// <paramref name="expectedRevision"/>. Returns how many rows changed: 0 means the write lost a
    /// race or the credential is gone, and the caller has to re-read and say which. The comparison
    /// is in the statement rather than in application code, so two writers cannot both believe they
    /// replaced the same revision.
    /// </summary>
    Task<int> ReplaceAsync(
        Credential replacement,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a credential of this scope. Returns how many rows changed, so a caller that removed
    /// nothing is told so rather than told it succeeded.
    /// </summary>
    Task<int> DeleteAsync(Guid scopeId, Guid credentialId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the credential with this id, whatever scope it is in.
    /// </summary>
    /// <remarks>
    /// The vault's delete contract carries a reference and not a scope, so this is the statement that
    /// backs it. It is the one unscoped write in the package and it exists only because a reference is
    /// a capability the vault is holding on the owner's behalf: every caller is required to have
    /// resolved the id inside its own scope first, and a reference is only ever minted by a save in
    /// that scope. An HTTP-shaped caller never reaches it - the settings delete resolves the id
    /// scope-bound and answers 404 for anything else.
    /// </remarks>
    Task<int> DeleteStoredAsync(Guid credentialId, CancellationToken cancellationToken = default);
}
