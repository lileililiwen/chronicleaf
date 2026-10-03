using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Secrets;

/// <summary>
/// What an owner may do with the installation's provider credentials (CREDENTIAL-VAULT-001/002).
/// </summary>
/// <remarks>
/// This is the whole public surface of the package, and it returns metadata and references only.
/// There is no call on it that hands a secret to an HTTP-shaped caller, and no call that would
/// return a secret to a caller of any kind - a secret leaves the vault as a lease, and the only
/// component that asks for a lease is the one that is about to use it against a provider.
/// </remarks>
public interface ICredentialVaultService
{
    /// <summary>
    /// Stores a credential, replacing whatever this scope had for the provider.
    /// </summary>
    /// <param name="request">The owner, the provider, the value and the revision it replaces.</param>
    /// <param name="cancellationToken">Cancelled on owner request or shutdown.</param>
    /// <returns>The stored credential's metadata. Never the value.</returns>
    Task<CredentialMetadata> SaveAsync(
        CredentialSaveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The state of one provider in this scope: whether a credential is configured, and when it last
    /// changed. A provider with no credential is reported as unconfigured, not as a blank value.
    /// </summary>
    Task<CredentialStatus> DescribeAsync(
        Guid scopeId,
        string provider,
        CancellationToken cancellationToken = default);

    /// <summary>Removes one of this scope's credentials.</summary>
    /// <exception cref="CredentialNotFoundException">
    /// The id belongs to this scope's owner and exists there. A foreign id is not found either.
    /// </exception>
    Task<CredentialMetadata> DeleteAsync(
        CredentialDeleteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>One bounded page of this scope's configured providers.</summary>
    Task<CredentialPage> ListAsync(
        Guid scopeId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A save, with the revision it is conditional on. <see cref="ExpectedRevision"/> 0 means "there is
/// nothing stored yet", which is the same token the delivered evidence surface publishes for an empty
/// state; any other value is the revision the owner read.
/// </summary>
/// <param name="Owner">The session's scope and actor, from the access package.</param>
/// <param name="Provider">One of the supported provider identifiers.</param>
/// <param name="Value">The secret to seal and store. Never logged, never audited, never returned.</param>
/// <param name="ExpectedRevision">The revision this save replaces, or 0 for a first save.</param>
public sealed record CredentialSaveRequest(
    OwnerScope Owner,
    string Provider,
    string Value,
    long ExpectedRevision);

/// <param name="Owner">The session's scope and actor.</param>
/// <param name="CredentialId">The credential to remove.</param>
/// <param name="ExpectedRevision">
/// The revision the caller read. A delete is conditional in the same way a save is: a credential that
/// was replaced since the caller read it is not the credential it asked to remove, and removing it
/// would silently throw away a rotation.
/// </param>
public sealed record CredentialDeleteRequest(OwnerScope Owner, Guid CredentialId, long ExpectedRevision);

/// <summary>
/// What the owner may be told about one provider (CREDENTIAL-VAULT-002).
/// </summary>
/// <param name="Provider">The provider this describes.</param>
/// <param name="Configured">Whether this scope has a credential stored for it.</param>
/// <param name="Id">The credential id, or null when unconfigured.</param>
/// <param name="UpdatedAt">When it last changed, or null when unconfigured.</param>
/// <param name="Revision">The revision an <c>If-Match</c> must carry, or null when unconfigured.</param>
public sealed record CredentialStatus(
    string Provider,
    bool Configured,
    Guid? Id,
    DateTimeOffset? UpdatedAt,
    long? Revision);

/// <summary>
/// Generates the identity of a new credential (CREDENTIAL-VAULT-001).
/// </summary>
/// <remarks>
/// An interface rather than a direct <c>Guid.NewGuid()</c> so a test can state the identity it
/// expects to find in a row, in a key and in a reference, instead of reading back whatever the
/// process happened to produce. Production uses <see cref="NewCredentialIdSource"/>, which is the
/// only implementation that exists in this repository - the seam is for determinism, not for a
/// pluggable identity policy.
/// </remarks>
public interface ICredentialIdSource
{
    /// <summary>The id the next credential will be stored under.</summary>
    Guid NextCredentialId();
}

/// <summary>The production identity source: a fresh v4 UUID per credential.</summary>
public sealed class NewCredentialIdSource : ICredentialIdSource
{
    public Guid NextCredentialId() => Guid.NewGuid();
}

/// <summary>The bounds on a stored secret, and only on its stored form.</summary>
public static class CredentialLimits
{
    /// <summary>
    /// The largest secret this package accepts, in bytes of UTF-8. It is a bound on what an operator
    /// pastes into a settings field, not a licence to store a document: provider tokens are orders of
    /// magnitude smaller, and a request that would exceed this is refused rather than truncated,
    /// because a truncated token is a wrong token that looks like a working one.
    /// </summary>
    public const int MaximumSecretBytes = 8 * 1024;

    /// <summary>The largest provider identifier the column and the API accept.</summary>
    public const int MaximumProviderBytes = CredentialProviders.MaxLength;
}

/// <summary>
/// The owner's use cases over the vault (CREDENTIAL-VAULT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This service sees the owner's plaintext for exactly as long as the request takes, and hands it
/// straight to <see cref="ISecretVault.PutAsync"/>. It never holds the key, never seals anything
/// itself, never sees a ciphertext and never receives a secret back: reading a credential's metadata
/// goes through <see cref="ICredentialStore"/>, which cannot return a secret at all. That split is
/// the structural reason a value cannot reach a response from here - there is no value on any type in
/// this file except the request that carries it in and the lease no caller in this assembly opens.
/// </para>
/// <para>
/// Every mutation is conditional on a revision, checked here and again as a compare-and-set in the
/// database by the write itself, so a stale edit is a 409 with the current credential left exactly
/// as it was - not a last writer wins overwrite.
/// <para>
/// The id of a stored credential and the instant it was stored are decided by the vault, not here:
/// the component that seals the bytes is the one that chooses where they go, so this service holds
/// no clock and no identity source for a row it does not write. The row it reads back is the row
/// the vault reported.
/// </para>
/// </remarks>
public sealed class CredentialVaultService(
    ISecretVault vault,
    ICredentialStore credentials,
    IAuditWriter audit,
    IUnitOfWork unitOfWork) : ICredentialVaultService
{
    /// <summary>The audit action for a credential that was created or replaced.</summary>
    public const string SavedAction = "credential.saved";

    /// <summary>The audit action for a removed credential.</summary>
    public const string DeletedAction = "credential.deleted";

    /// <inheritdoc />
    public async Task<CredentialMetadata> SaveAsync(
        CredentialSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = SupportedProviders.Require(request.Provider);
        var value = RequireValue(request.Value);
        var scopeId = request.Owner.ScopeId;

        return await unitOfWork
            .ExecuteAsync(
                async ct =>
                {
                    // Read the head first so a stale If-Match is refused before a key is even asked
                    // for, and so the audit row and the credential row commit together.
                    var head = await credentials.FindAsync(scopeId, provider, ct).ConfigureAwait(false);
                    RequireExpectedRevision(head, request.ExpectedRevision);

                    // The vault is the only component that handles the value. It decides the stored
                    // id, seals, and writes conditionally: if another writer got there first, it
                    // refuses and nothing this call sealed is stored.
                    var reference = await vault
                        .PutAsync(scopeId, provider, value, ct)
                        .ConfigureAwait(false);

                    var stored = await credentials
                        .FindByIdAsync(scopeId, reference.Value, ct)
                        .ConfigureAwait(false) ??
                        throw new InternalFailureException(
                            "The stored credential could not be read back.");

                    // ids and action and revision only. The provider is a fixed vocabulary word; the
                    // value never reaches the trail, which is readable more widely than the vault.
                    audit.Record(new DomainAudit(
                        request.Owner.ActorId,
                        SavedAction,
                        stored.Id,
                        stored.Revision,
                        $"provider {provider}"));

                    return stored;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CredentialStatus> DescribeAsync(
        Guid scopeId,
        string provider,
        CancellationToken cancellationToken = default)
    {
        var normalized = SupportedProviders.Require(provider);
        var found = await credentials
            .FindAsync(scopeId, normalized, cancellationToken)
            .ConfigureAwait(false);

        // Unconfigured is its own answer with nulls, not a zero, not an empty string and not a
        // 404: the question "is this provider set up" has a true answer before anything is stored.
        return found is null
            ? new CredentialStatus(normalized, Configured: false, Id: null, UpdatedAt: null, Revision: null)
            : new CredentialStatus(found.Provider, Configured: true, found.Id, found.UpdatedAt, found.Revision);
    }

    /// <inheritdoc />
    public async Task<CredentialMetadata> DeleteAsync(
        CredentialDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scopeId = request.Owner.ScopeId;

        return await unitOfWork
            .ExecuteAsync(
                async ct =>
                {
                    // Scope-bound first: an id from another scope is not found here, so the delete
                    // never reaches the vault and nothing is disclosed about another installation.
                    var head = await credentials
                        .FindByIdAsync(scopeId, request.CredentialId, ct)
                        .ConfigureAwait(false) ??
                        throw new CredentialNotFoundException();

                    // Checked before the delete, in the same way a save checks it: a stale revision
                    // leaves the stored credential exactly as it is.
                    RequireExpectedRevision(head, request.ExpectedRevision);

                    await vault.DeleteAsync(head.Reference, ct).ConfigureAwait(false);

                    audit.Record(new DomainAudit(
                        request.Owner.ActorId,
                        DeletedAction,
                        head.Id,
                        head.Revision,
                        $"provider {head.Provider}"));

                    return head;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CredentialPage> ListAsync(
        Guid scopeId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        var limit = ListQueryPolicy.Clamp(pageSize);

        // The delivered opaque cursor, bound to this scope: a cursor from another installation is
        // refused here rather than paginating a list the caller may not read. The cursor's sort key
        // is the provider, which is the order the page is in.
        string? afterProvider = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            (afterProvider, _) = ScopeCursor.Decode(cursor, scopeId);
        }

        // One row beyond the page, so the cursor is issued exactly when there is a next page and not
        // when the last page happened to end on the last row.
        var rows = await credentials
            .ListAsync(scopeId, afterProvider, limit + 1, cancellationToken)
            .ConfigureAwait(false);

        var page = rows.Count > limit ? rows.Take(limit).ToArray() : rows.ToArray();
        var last = page.Length == 0 ? null : page[^1];

        return new CredentialPage(
            page,
            last is null || rows.Count <= limit
                ? null
                : ScopeCursor.Encode(scopeId, last.Provider, last.Id));
    }

    /// <summary>
    /// The revision contract, enforced before anything is written: 0 means this scope has nothing
    /// stored for the provider, and any other value means the stored revision has to be exactly it.
    /// </summary>
    private static void RequireExpectedRevision(CredentialMetadata? head, long expectedRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ValidationFailedException(
                "expectedRevision",
                "The expected revision is not a revision this API issues.");
        }

        var actual = head?.Revision ?? 0;

        if (actual != expectedRevision)
        {
            // The current revision is reported so the caller can re-read; the caller's stale value and
            // the credential's contents are not echoed. Nothing has been written, so the stored
            // credential is still exactly what it was.
            throw new RevisionConflictException(actual);
        }
    }

    /// <summary>
    /// The value a save carries, checked for emptiness and length before it reaches the vault. The
    /// messages name the field and not the value, because a <c>fieldErrors</c> entry is rendered to
    /// the owner and written to a log line.
    /// </summary>
    private static string RequireValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationFailedException(
                "value",
                "A credential value is required, and an empty one is not a credential.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(value) > CredentialLimits.MaximumSecretBytes)
        {
            throw new ValidationFailedException(
                "value",
                $"A credential value may be at most {CredentialLimits.MaximumSecretBytes} bytes.");
        }

        return value;
    }
}
