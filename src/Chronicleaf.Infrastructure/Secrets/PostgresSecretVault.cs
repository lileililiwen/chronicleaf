using System.Security.Cryptography;
using System.Text;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Secrets;

/// <summary>
/// The vault itself: seal, store, open, delete (CREDENTIAL-VAULT-001).
/// </summary>
/// <remarks>
/// <para>
/// This is the only component in the repository that holds a plaintext secret. A value arrives as a
/// <c>string</c> from the owner's request, is sealed, and the string goes out of scope; coming back
/// the other way it is opened into a <see cref="SecretLease"/> the caller must return. There is no
/// method here that returns a secret as a value, no method that writes one, and no log call that
/// mentions one - the two log-adjacent decisions in this file are the "a key was obtained" line in
/// <see cref="FileSecretKeyProvider"/> and the deliberate absence of one here.
/// </para>
/// <para>
/// <b>Scope.</b> A save is scoped, because the contract carries a scope: the row is written with that
/// scope in its WHERE clause. A resolve and a delete are not, because their contract carries only a
/// reference. That asymmetry is deliberate and is why the two are not interchangeable: the caller of
/// <see cref="ResolveAsync"/> is told the scope the row actually came from and must check it against
/// the scope it is acting under before it reads a byte, and the caller of
/// <see cref="DeleteAsync"/> is required to have resolved the id within its own scope first, because
/// a reference is a capability and not an authority.
/// </para>
/// <para>
/// <b>The key.</b> It is read from the operator's file for each operation and zeroed in a
/// <c>finally</c>, so no key material is cached in this process. Every failure to obtain it stops the
/// operation before a byte is written, which is what makes an unavailable vault a refusal rather
/// than a downgrade: nothing is sealed under a substitute key, nothing is stored in the clear and
/// nothing is reported as a success.
/// </para>
/// </remarks>
public sealed class PostgresSecretVault(
    ChronicleafDbContext dbContext,
    ICredentialStore credentials,
    ISecretKeyProvider keys,
    ICredentialIdSource identities,
    TimeProvider clock) : ISecretVault
{
    /// <inheritdoc />
    public async Task<SecretRef> PutAsync(
        Guid scopeId,
        string provider,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var normalized = SupportedProviders.Require(provider);

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A credential belongs to a scope.", nameof(scopeId));
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ValidationFailedException(
                "value",
                "A credential value is required, and an empty one is not a credential.");
        }

        if (Encoding.UTF8.GetByteCount(secret) > CredentialLimits.MaximumSecretBytes)
        {
            throw new ValidationFailedException(
                "value",
                $"A credential value may be at most {CredentialLimits.MaximumSecretBytes} bytes.");
        }

        // Read the head so a replacement keeps the stored id: a credential that is replaced stays the
        // same reference, which is what lets a job that already holds one keep resolving it.
        var head = await credentials
            .FindAsync(scopeId, normalized, cancellationToken)
            .ConfigureAwait(false);

        var credentialId = head?.Id ?? identities.NextCredentialId();
        var revision = head?.Revision ?? 0;
        var now = clock.GetUtcNow();

        // Before the plaintext is turned into anything. A vault with no usable key raises here, and
        // the table is left exactly as it was.
        var key = keys.ReadKey(SecretVaultSettings.CurrentKeyVersion, cancellationToken);

        try
        {
            var sealedSecret = AesGcmSecretCipher.Seal(
                Encoding.UTF8.GetBytes(secret),
                key,
                new CredentialBinding(scopeId, normalized, credentialId, SecretVaultSettings.CurrentKeyVersion));

            if (head is null)
            {
                var created = Credential.Create(
                    credentialId,
                    scopeId,
                    normalized,
                    sealedSecret.Ciphertext,
                    sealedSecret.Nonce,
                    SecretVaultSettings.CurrentKeyVersion,
                    now);

                // The unique (scope_id, provider) index decides the race: no returned id means another
                // writer created this scope's credential for this provider first, and the bytes sealed
                // here are dropped with the local record rather than written over theirs.
                var inserted = await credentials
                    .InsertAsync(created, cancellationToken)
                    .ConfigureAwait(false);

                if (!inserted)
                {
                    throw new RevisionConflictException(1);
                }

                return SecretRef.To(created.Id);
            }

            var replacement = new Credential(
                head.Id,
                scopeId,
                head.Provider,
                sealedSecret.Ciphertext,
                sealedSecret.Nonce,
                SecretVaultSettings.CurrentKeyVersion,
                now,
                head.Revision + 1);

            var changed = await credentials
                .ReplaceAsync(replacement, revision, cancellationToken)
                .ConfigureAwait(false);

            if (changed == 0)
            {
                // The stored revision moved between the read and the write. The current credential
                // is untouched, and this call's sealed bytes never reach the table.
                throw new RevisionConflictException(revision + 1);
            }

            return SecretRef.To(replacement.Id);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc />
    public async Task<SecretLease> ResolveAsync(
        SecretRef secretRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);

        if (!secretRef.IsPresent)
        {
            throw new CredentialMissingException(secretRef);
        }

        var row = await ReadSealedAsync(secretRef.Value, cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            // The owner deleted it. Nothing is created in its place and no value is invented.
            throw new CredentialMissingException(secretRef);
        }

        var key = keys.ReadKey(row.KeyVersion, cancellationToken);

        try
        {
            var plaintext = AesGcmSecretCipher.Open(
                row.Ciphertext,
                row.Nonce,
                key,
                new CredentialBinding(row.ScopeId, row.Provider, row.Id, row.KeyVersion));

            // The lease takes ownership of the buffer. Nothing else holds a reference to it, the caller
            // has to return the lease to get at it again, and returning it zeroes the bytes.
            return new SecretLease(secretRef, row.ScopeId, row.Provider, row.KeyVersion, plaintext);
        }
        catch (CryptographicException)
        {
            // The bytes do not authenticate under this key and this binding: a moved row, a rewritten
            // key version, a different key file, or a tampered body. Fail closed - the row is left
            // exactly as it is and is never re-sealed to make it usable - and the answer to the caller
            // says nothing about the row's contents.
            throw new CredentialUndecryptableException(secretRef);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);

        if (!secretRef.IsPresent)
        {
            throw new CredentialMissingException(secretRef);
        }

        var removed = await credentials
            .DeleteStoredAsync(secretRef.Value, cancellationToken)
            .ConfigureAwait(false);

        if (removed == 0)
        {
            throw new CredentialMissingException(secretRef);
        }
    }

    /// <summary>
    /// The sealed row, read by id alone. It carries the scope it actually belongs to, which is the
    /// point: a reference knows nothing about its installation, so the scope has to be read out of the
    /// row and then checked by the caller.
    /// </summary>
    private async Task<SealedCredentialRow?> ReadSealedAsync(
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        var rows = await ChronicleafSql.QueryAsync<SealedCredentialRow>(
            dbContext,
            """
            SELECT scope_id, id, provider, ciphertext, nonce, key_version
            FROM credentials
            WHERE id = @p0
            """,
            [new("@p0", NpgsqlDbType.Uuid, credentialId)],
            CredentialRowReader.ReadSealed,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }
}
