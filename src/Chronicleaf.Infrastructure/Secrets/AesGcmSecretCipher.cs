using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chronicleaf.Core;

namespace Chronicleaf.Infrastructure.Secrets;

/// <summary>
/// The one construction that seals and opens a credential: AES-256-GCM, a fresh 96-bit nonce per
/// seal, and associated data that binds the ciphertext to the scope, provider, credential id and key
/// version it belongs to (CREDENTIAL-VAULT-001).
/// </summary>
/// <remarks>
/// <para>
/// Static, and stateless by design. The key is a parameter of each call rather than a field, so
/// there is no long-lived key material sitting in a registered singleton: the vault reads the key,
/// uses it for one operation and zeroes it in a <c>finally</c>, which is the whole life of the key
/// in this process.
/// </para>
/// <para>
/// <b>Associated data.</b> <c>chronicleaf.credential.v1scopeIdprovider
/// credentialIdkeyVersion</c>, UTF-8. The prefix domain-separates this construction from
/// any other that might be added later, and the separator is U+001F UNIT SEPARATOR, which cannot
/// occur in a UUID, in a provider identifier from the closed set, or in a decimal number - so two
/// different bindings cannot produce the same associated data. GCM authenticates these bytes with
/// the ciphertext, so a row moved to another scope, another provider or another id, or rewritten to
/// claim a different key version, does not open: the tag does not verify and this type throws
/// rather than returning a value.
/// </para>
/// <para>
/// <b>Nonce.</b> 12 bytes from <see cref="RandomNumberGenerator"/>, generated per seal and stored
/// beside the ciphertext so the row can be reopened. A nonce is not secret - it is written to a
/// column - but reusing one under the same key is what would break GCM, so it is never derived from
/// the plaintext, the clock, the id or a counter. The odds of a repeat inside one key are
/// negligible, and the binding means even a repeat cannot be moved between scopes.
/// </para>
/// <para>
/// <b>Tag.</b> .NET's GCM appends the tag to the ciphertext, so the stored <c>ciphertext</c> column
/// is the sealed body with its tag. The frame's length is <see cref="Credential.TagSizeBytes"/>, and
/// every <c>AesGcm</c> instance here is constructed with it: the constructor accepts only a tag size
/// the runtime supports for the key, so a domain constant the runtime could not honour fails on the
/// first seal or open rather than silently changing the stored frame.
/// </para>
/// </remarks>
public static class AesGcmSecretCipher
{
    /// <summary>The separator between the associated-data parts: U+001F UNIT SEPARATOR.</summary>
    public const char PartSeparator = '\u001F';

    /// <summary>The domain separation prefix of the associated data.</summary>
    public const string AssociatedDataPrefix = "chronicleaf.credential.v1";

    /// <summary>
    /// Seals <paramref name="plaintext"/> under <paramref name="key"/>, bound to
    /// <paramref name="binding"/>.
    /// </summary>
    /// <param name="plaintext">The secret's UTF-8 bytes.</param>
    /// <param name="key">The operator's 32-byte key.</param>
    /// <param name="binding">The scope, provider, id and key version the ciphertext belongs to.</param>
    /// <returns>The sealed body and the nonce it was sealed with.</returns>
    public static SealedSecret Seal(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        CredentialBinding binding)
    {
        RequireKey(key);

        var nonce = RandomNumberGenerator.GetBytes(Credential.NonceSizeBytes);
        var associatedData = AssociatedDataFor(binding);
        var ciphertext = new byte[plaintext.Length + Credential.TagSizeBytes];

        using var aes = new AesGcm(key, Credential.TagSizeBytes);

        aes.Encrypt(
            nonce,
            plaintext,
            ciphertext.AsSpan(0, plaintext.Length),
            ciphertext.AsSpan(plaintext.Length, Credential.TagSizeBytes),
            associatedData);

        return new SealedSecret(ciphertext, nonce);
    }

    /// <summary>
    /// Opens a sealed body under <paramref name="key"/> and the binding the row claims.
    /// </summary>
    /// <param name="ciphertext">The stored body, with its tag in the trailing bytes.</param>
    /// <param name="nonce">The stored nonce.</param>
    /// <param name="key">The operator's 32-byte key.</param>
    /// <param name="binding">The binding the row claims, and the only one that can open it.</param>
    /// <returns>The plaintext bytes, which the caller leases and then zeroes.</returns>
    /// <exception cref="CryptographicException">
    /// The bytes do not authenticate under this key and this binding: a moved row, a rewritten key
    /// version, the wrong key file, or a tampered body. The buffer is zeroed before the throw, so
    /// unauthenticated plaintext never leaves this method.
    /// </exception>
    public static byte[] Open(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> key,
        CredentialBinding binding)
    {
        RequireKey(key);

        if (nonce.Length != Credential.NonceSizeBytes)
        {
            throw new CryptographicException("The stored nonce is not the length this cipher produces.");
        }

        if (ciphertext.Length < Credential.MinimumCiphertextSizeBytes)
        {
            throw new CryptographicException("The stored credential is too short to be authentic.");
        }

        var payloadLength = ciphertext.Length - Credential.TagSizeBytes;
        var plaintext = new byte[payloadLength];
        var associatedData = AssociatedDataFor(binding);

        try
        {
            using var aes = new AesGcm(key, Credential.TagSizeBytes);

            // The span overload takes the tag before the plaintext; the tag is the trailing
            // TagSizeBytes of the stored body.
            aes.Decrypt(
                nonce,
                ciphertext[..payloadLength],
                ciphertext[payloadLength..],
                plaintext,
                associatedData);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }

        return plaintext;
    }

    /// <summary>
    /// The exact bytes authenticated alongside the ciphertext. Exposed so a test can prove the
    /// binding itself, by showing which change makes the tag stop verifying.
    /// </summary>
    public static byte[] AssociatedDataFor(CredentialBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (binding.ScopeId == Guid.Empty ||
            binding.CredentialId == Guid.Empty ||
            string.IsNullOrEmpty(binding.Provider))
        {
            throw new ArgumentException(
                "A binding needs a scope, a provider, a credential id and a key version.",
                nameof(binding));
        }

        var text = string.Concat(
            AssociatedDataPrefix,
            PartSeparator,
            binding.ScopeId.ToString("D"),
            PartSeparator,
            binding.Provider,
            PartSeparator,
            binding.CredentialId.ToString("D"),
            PartSeparator,
            binding.KeyVersion.ToString(CultureInfo.InvariantCulture));

        return Encoding.UTF8.GetBytes(text);
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != SecretKeySize.Bytes)
        {
            throw new ArgumentException("An AES-256-GCM key is 32 bytes.", nameof(key));
        }
    }
}

/// <summary>The four values a sealed credential is bound to (CREDENTIAL-VAULT-001).</summary>
/// <param name="ScopeId">The installation scope the credential belongs to.</param>
/// <param name="Provider">The provider it is stored for.</param>
/// <param name="CredentialId">The credential's own id.</param>
/// <param name="KeyVersion">The key version it was sealed with.</param>
public sealed record CredentialBinding(Guid ScopeId, string Provider, Guid CredentialId, int KeyVersion)
{
    /// <summary>
    /// The binding a stored row claims, which is the only binding its ciphertext can open under.
    /// </summary>
    public static CredentialBinding From(Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        return new CredentialBinding(credential.ScopeId, credential.Provider, credential.Id, credential.KeyVersion);
    }
}

/// <summary>What one seal produced: the sealed body and the nonce it was sealed with.</summary>
/// <param name="Ciphertext">The sealed body, with the tag in its trailing bytes.</param>
/// <param name="Nonce">The one-time nonce.</param>
public sealed record SealedSecret(byte[] Ciphertext, byte[] Nonce);

/// <summary>The AES-256 key length, named once so every layer refuses the same value.</summary>
public static class SecretKeySize
{
    /// <summary>32 bytes, which is AES-256.</summary>
    public const int Bytes = 32;
}
