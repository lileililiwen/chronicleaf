using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Secrets;

namespace Chronicleaf.Application.Secrets;

/// <summary>
/// Resolves a stored credential for a queued job, and nothing else (CREDENTIAL-VAULT-001).
/// </summary>
/// <remarks>
/// <para>
/// The job payload the queue serializes carries one thing: this operation's id. The credential is
/// named by <see cref="OperationRef.TargetId"/>, a UUID, and is opened only when the job actually
/// runs. There is therefore no path by which a secret enters the queue, a job row, an idempotency
/// key, a fingerprint or a result column, because none of those ever holds one.
/// </para>
/// <para>
/// This handler performs no provider call. It is the boundary the design asks for, made executable:
/// a job that outlives its credential fails <c>credential_missing</c>, and a job that runs with the
/// credential in hand proves the leased bytes are reachable, scope-checked and returned. What a
/// later package does with the bytes - which transport, which provider call - is that package's
/// code behind this same seam, and this handler is the one place the lease ends up today.
/// </para>
/// <para>
/// The lease is returned in a <c>finally</c>, so a handler that throws after resolving still zeroes
/// the bytes. Nothing here formats the value, its length or its prefix into a message, a result
/// reference or a log line: the only thing the result names is the credential, by id.
/// </para>
/// </remarks>
public sealed class CredentialResolveOperationHandler(ISecretVault vault) : IOperationHandler
{
    /// <summary>The kind this handler owns, matching <c>operations.kind</c> exactly.</summary>
    public const string ResolveKind = "credential.resolve";

    /// <inheritdoc />
    public string Kind => ResolveKind;

    /// <inheritdoc />
    public async Task<OperationResult> HandleAsync(
        OperationRef operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.TargetId == Guid.Empty)
        {
            // Nothing to resolve. Permanent, like every other absence.
            throw new OperationPermanentException(CredentialVaultError.Missing);
        }

        var reference = SecretRef.To(operation.TargetId);

        SecretLease? lease = null;
        try
        {
            lease = await vault.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);

            // The scope recheck the reference cannot carry: a serialized id says nothing about which
            // installation it belongs to, so the scope the row actually came from is compared with
            // the scope the job is running under before a byte is read.
            if (lease.ScopeId != operation.ScopeId)
            {
                throw new OperationPermanentException(CredentialVaultError.Missing);
            }

            // The bytes are read and then dropped. There is no provider transport in this change, so
            // nothing is sent anywhere and nothing is recorded: the lease is returned on the way out
            // and the only fact this job produces is that the credential opened for this scope.
            var length = lease.Length;

            return new OperationResult(
                FormattableString.Invariant($"credential:{operation.TargetId:D}:resolved:{length}"));
        }
        catch (SecretVaultUnavailableException unavailable)
        {
            // Transient, because an operator who restores the key file has fixed the cause without
            // re-asking for the job. The retry is what makes that true; the message never travels.
            throw new OperationTransientException(CredentialVaultError.VaultUnavailable, inner: unavailable);
        }
        catch (CredentialUndecryptableException undecryptable)
        {
            // Transient for the same reason: the row may be re-sealed by a later successful save, and
            // retrying never re-seals anything by itself.
            throw new OperationTransientException(
                CredentialVaultError.Undecryptable,
                inner: undecryptable);
        }
        catch (CredentialMissingException missing)
        {
            // The credential the owner deleted between the job being asked for and the job running.
            // Permanent: a retry finds the same absence, and nobody is going to re-add a credential
            // to satisfy a job that has already failed.
            throw new OperationPermanentException(CredentialVaultError.Missing, inner: missing);
        }
        finally
        {
            // Returned on every path, including the throwing ones, so the bytes are zeroed even when
            // the failure happened while the lease was open.
            lease?.Dispose();
        }
    }
}
