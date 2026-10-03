using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Application.Secrets;

/// <summary>
/// Where the operator keeps the encryption key, and which key version this build seals with
/// (CREDENTIAL-VAULT-001).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EncryptionKeyFilePath"/> is the one piece of configuration this package has, and it
/// names a <em>file</em>. The key is never configured as a value, never stored in a database, never
/// baked into an image and never committed to this repository: a key an operator can read out of a
/// versioned file or out of a container layer is not a key. That is also why there is no default and
/// no fallback - an unset path is a refused vault, not a generated key.
/// </para>
/// <para>
/// The file is read at the moment a credential is sealed or opened rather than once at start-up, so
/// an operator who restores or replaces the key file fixes a broken vault without restarting the
/// process, and an operator who takes the key file away stops credential use immediately instead of
/// at the next deployment.
/// </para>
/// </remarks>
public sealed class SecretVaultSettings
{
    /// <summary>The configuration section, and the environment-variable prefix an operator sets.</summary>
    public const string SectionName = "Chronicleaf";

    /// <summary>
    /// The configuration key the key file is read from, set by an operator as the environment
    /// variable <c>Chronicleaf__EncryptionKeyFile</c>.
    /// </summary>
    public const string EncryptionKeyFileSettingName = "Chronicleaf:EncryptionKeyFile";

    /// <summary>
    /// The key length AES-256 needs, in bytes. A file of any other length is refused: truncating a
    /// longer key or zero-padding a shorter one would make a length mistake look like a working key.
    /// </summary>
    public const int KeySizeBytes = 32;

    /// <summary>
    /// The only key version this build has. It is recorded in every sealed row and is part of the
    /// associated data, so a row written under a different version cannot be opened by this one and
    /// this one cannot be pointed at a version it does not hold.
    /// </summary>
    public const int CurrentKeyVersion = 1;

    /// <summary>
    /// The configured key file path, exactly as configured. Null when nothing is configured. It is
    /// never logged, echoed into an error envelope or written to the database.
    /// </summary>
    public string? EncryptionKeyFilePath { get; init; }

    /// <summary>True when an operator configured a key file for this process.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(EncryptionKeyFilePath);

    /// <summary>Reads the settings. An unset or blank path is left unset rather than defaulted.</summary>
    public static SecretVaultSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[EncryptionKeyFileSettingName];

        return new SecretVaultSettings
        {
            EncryptionKeyFilePath = string.IsNullOrWhiteSpace(configured) ? null : configured,
        };
    }
}
