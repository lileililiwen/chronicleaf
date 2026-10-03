using Chronicleaf.Application.Common;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Secrets;

/// <summary>
/// The codes this package adds to the delivered error envelope. The envelope itself is unchanged
/// (CREDENTIAL-VAULT-002).
/// </summary>
public static class CredentialVaultError
{
    /// <summary>
    /// A queued job asked for a credential that is not there, because the owner deleted it. The
    /// operation's <c>error_code</c> says this, and it is permanent: a retry would find the same
    /// absence.
    /// </summary>
    public const string Missing = "credential_missing";

    /// <summary>
    /// The operator's encryption key could not be obtained: the key file is not configured, is not
    /// readable, is not the accepted length, or names a key version this build does not have. 503,
    /// because the request is answerable once the operator fixes the file.
    /// </summary>
    public const string VaultUnavailable = "credential_vault_unavailable";

    /// <summary>
    /// The stored bytes are present but do not authenticate under the current key, scope, provider,
    /// id and key version. 503 and fail-closed: a row that has been moved, tampered with or
    /// downgraded is never opened as if it were intact, and never re-sealed to make it usable.
    /// </summary>
    public const string Undecryptable = "credential_undecryptable";
}

/// <summary>
/// The operator's encryption key is not usable right now (503), so no credential is sealed, opened
/// or kept in the clear as a result.
/// </summary>
/// <remarks>
/// The message names the condition in words the caller can act on and never the file's path, its
/// length, its permissions or a byte of a key: the middleware writes <see cref="Exception.Message"/>
/// verbatim into the error envelope and into the log line beside it, so a path here would be an
/// absolute key-file host path in both.
/// </remarks>
public sealed class SecretVaultUnavailableException : ApiException
{
    /// <remarks>
    /// There is no inner-exception parameter on purpose. The delivered base takes a code, a message
    /// and field errors, and every failure this package raises comes from a filesystem call whose
    /// own exception text carries the absolute path of the key file. Attaching it here would put that
    /// path into a stack trace, and a stack trace ends up in a log. The condition is carried in
    /// <see cref="Reason"/> instead, which is one of a closed set of words.
    /// </remarks>
    public SecretVaultUnavailableException(string reason)
        : base(
            CredentialVaultError.VaultUnavailable,
            $"The credential vault is unavailable: {reason}.")
    {
        Reason = reason;
    }

    /// <summary>
    /// The condition in words, safe to log: one of the constants in <see cref="SecretKeyFault"/>.
    /// </summary>
    public string Reason { get; }
}

/// <summary>
/// A stored credential's bytes do not authenticate (503). The row is left exactly as it is.
/// </summary>
public sealed class CredentialUndecryptableException : ApiException
{
    public CredentialUndecryptableException(SecretRef secretRef)
        : base(
            CredentialVaultError.Undecryptable,
            "The stored credential could not be opened and was not changed.")
    {
        // Only the reference is recorded, never the bytes and never the scope or provider that were
        // part of the failed opening.
        Reference = secretRef;
    }

    /// <summary>The credential that could not be opened.</summary>
    public SecretRef Reference { get; }
}

/// <summary>
/// There is no credential with that id in this scope (404), so nothing is disclosed about whether it
/// exists elsewhere. This is the answer at the API boundary, where a caller asked for a resource.
/// </summary>
public sealed class CredentialNotFoundException : ApiException
{
    public CredentialNotFoundException()
        : base(ApiError.NotFound, "That credential does not exist in this installation.") { }
}

/// <summary>
/// A reference names no stored credential (CREDENTIAL-VAULT-001).
/// </summary>
/// <remarks>
/// This is the vault's answer, not the API's: it is what a queued job gets when the owner deleted
/// the credential between the job being asked for and the job running, and the handler turns it into
/// the <c>credential_missing</c> the operation records. It has no status mapping of its own because
/// it never becomes an envelope - no route resolves a secret - and the default 500 is the honest
/// outcome for a failure that should not be reachable over HTTP at all.
/// </remarks>
public sealed class CredentialMissingException : ApiException
{
    public CredentialMissingException(SecretRef secretRef)
        : base(CredentialVaultError.Missing, "The referenced credential no longer exists.")
    {
        Reference = secretRef;
    }

    /// <summary>The reference that named nothing.</summary>
    public SecretRef Reference { get; }
}

/// <summary>The conditions the key file can be in, named without the path.</summary>
public static class SecretKeyFault
{
    /// <summary>No key file is configured, so there is no key to use.</summary>
    public const string NotConfigured = "no key file is configured";

    /// <summary>The key file is not there to be read.</summary>
    public const string Missing = "the key file is not present";

    /// <summary>The key file exists but cannot be read by this process.</summary>
    public const string Unreadable = "the key file cannot be read";

    /// <summary>The key file is not the accepted number of bytes.</summary>
    public const string WrongLength = "the key file is not the accepted length";

    /// <summary>The row names a key version this build does not have.</summary>
    public const string UnknownKeyVersion = "the stored key version is not one this build has";
}

/// <summary>
/// The provider identifiers this package accepts, re-exported so a caller does not have to reach
/// into the domain to validate a route value.
/// </summary>
public static class SupportedProviders
{
    /// <summary>The identifiers, in the order a settings surface lists them.</summary>
    public static IReadOnlyList<string> All => CredentialProviders.All;

    /// <summary>
    /// The identifier as stored, or a 422 naming <paramref name="field"/> when it is not one of them.
    /// </summary>
    /// <remarks>
    /// An unrecognised identifier is refused rather than stored under its own name, and the
    /// <c>fieldErrors</c> value repeats the name the caller sent and nothing else: a provider name is
    /// not a secret, but a caller that sent a token in this field should not have it echoed back
    /// either, so the message is fixed text and the sent value is left out of it.
    /// </remarks>
    public static string Require(string? provider, string field = "provider")
    {
        var normalized = CredentialProviders.Normalize(provider);
        if (normalized is null)
        {
            throw new ValidationFailedException(
                field,
                "The provider is not one Chronicleaf stores credentials for.");
        }

        return normalized;
    }
}
