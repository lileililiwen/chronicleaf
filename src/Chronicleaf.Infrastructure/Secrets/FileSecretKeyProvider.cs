using System.Security.Cryptography;
using Chronicleaf.Application.Secrets;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Infrastructure.Secrets;

/// <summary>
/// Reads the operator's key file, and refuses to guess (CREDENTIAL-VAULT-001).
/// </summary>
public interface ISecretKeyProvider
{
    /// <summary>
    /// The 32 bytes of the operator's key, for one operation. The caller owns the returned array
    /// and must zero it when it is finished with it.
    /// </summary>
    /// <param name="keyVersion">The key version the caller is sealing under or opening with.</param>
    /// <param name="cancellationToken">Cancelled on owner request or shutdown.</param>
    /// <exception cref="SecretVaultUnavailableException">
    /// No key file is configured, it is not there, it cannot be read, or it is not 32 bytes.
    /// </exception>
    /// <exception cref="SecretVaultUnavailableException">
    /// The row names a key version this build does not have.
    /// </exception>
    byte[] ReadKey(int keyVersion, CancellationToken cancellationToken = default);
}

/// <summary>
/// The key, from a file the operator chose, read at the moment it is needed
/// (CREDENTIAL-VAULT-001).
/// </summary>
/// <remarks>
/// <para>
/// The file is read per operation rather than cached at start-up, and that is the whole fail-closed
/// story. An operator who mounts the key late, replaces it, or takes it away changes the behaviour of
/// the next credential operation immediately, with no restart and no stale key copy held in the
/// process. A 32-byte read is not worth the staleness.
/// </para>
/// <para>
/// Nothing is generated, padded, truncated or defaulted. A missing file, an unreadable file, a file
/// of the wrong length and a key version this build does not have are all the same answer to the
/// caller - <see cref="SecretVaultUnavailableException"/> - and in every one of those cases no
/// secret is sealed, no secret is opened, no row is written and no upstream call is attempted. There
/// is no code path that stores a secret in the clear or reports success without a sealed row.
/// </para>
/// <para>
/// <b>What is not logged.</b> Not the path, not the length, not the bytes, not the contents of the
/// file. The logger is here for one line at debug level saying that a key was obtained, keyed by
/// nothing that identifies a credential; the failure paths raise exceptions whose messages name the
/// condition in the closed vocabulary of <see cref="SecretKeyFault"/>. An absolute key-file host
/// path must not appear in any log line, error envelope or field error, because a log line is exactly
/// the kind of place a path outlives its usefulness.
/// </para>
/// </remarks>
public sealed class FileSecretKeyProvider(SecretVaultSettings settings, ILogger<FileSecretKeyProvider> logger)
    : ISecretKeyProvider
{
    /// <inheritdoc />
    public byte[] ReadKey(int keyVersion, CancellationToken cancellationToken = default)
    {
        if (keyVersion != SecretVaultSettings.CurrentKeyVersion)
        {
            // Refused before the file is touched: a row that claims a version this build does not
            // hold cannot be opened, whatever the file says.
            throw new SecretVaultUnavailableException(SecretKeyFault.UnknownKeyVersion);
        }

        var path = settings.EncryptionKeyFilePath;
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(path))
        {
            throw new SecretVaultUnavailableException(SecretKeyFault.NotConfigured);
        }

        byte[] key;
        try
        {
            // The path is passed to the runtime and never kept: an exception from the filesystem would
            // carry it, so every filesystem failure below is turned into a fixed condition first.
            key = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SecretVaultUnavailableException(SecretKeyFault.Missing);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            throw new SecretVaultUnavailableException(SecretKeyFault.Unreadable);
        }

        if (key.Length != SecretKeySize.Bytes)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new SecretVaultUnavailableException(SecretKeyFault.WrongLength);
        }

        // One line, no path, no length, no bytes: the fact that a key was obtained, nothing about
        // which one. The line is debug level so a default Information deployment does not even carry
        // the fact.
        logger.LogDebug("Encryption key {KeyVersion} read from the operator's key file.", keyVersion);

        return key;
    }
}
