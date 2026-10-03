namespace Chronicleaf.Core;

/// <summary>
/// One provider credential, stored only as ciphertext (CREDENTIAL-VAULT-001).
/// </summary>
/// <remarks>
/// <para>
/// The record deliberately has no property that can hold a plaintext secret: what is persisted and
/// what is read back is the sealed <see cref="Ciphertext"/>, the <see cref="Nonce"/> it was sealed
/// with, and the <see cref="KeyVersion"/> it is bound to. There is therefore nothing for a caller to
/// log, a projection to expose or an export to carry, because the value never enters this type.
/// </para>
/// <para>
/// <see cref="Ciphertext"/> carries the GCM authentication tag in its trailing <see cref="TagSizeBytes"/>
/// bytes, which is the frame <c>AesGcm</c> produces. The tag is what makes a row that was moved to
/// another scope, another provider, another id or an older key version fail to open rather than open
/// as garbage.
/// </para>
/// <para>
/// <see cref="Revision"/> is the optimistic concurrency token: the first stored version is 1, every
/// accepted replacement adds one, and a caller replaces a version by presenting the revision it read
/// through <c>If-Match</c>.
/// </para>
/// </remarks>
public sealed class Credential
{
    /// <summary>The length of the random nonce the cipher generates per seal, in bytes.</summary>
    public const int NonceSizeBytes = 12;

    /// <summary>
    /// The length of the authentication tag at the tail of a sealed body, in bytes. This is the GCM tag
    /// length, declared here so the domain owns the frame the cipher has to produce; the cipher
    /// asserts that <c>AesGcm.AuthenticationTagSizeBytes</c> agrees before it seals anything.
    /// </summary>
    public const int TagSizeBytes = 16;

    /// <summary>
    /// The smallest sealed body that can still be authentic: one tag and nothing else. A credential
    /// with an empty secret is refused upstream, so a real row is always longer than this.
    /// </summary>
    public const int MinimumCiphertextSizeBytes = TagSizeBytes + 1;

    public Credential(
        Guid id,
        Guid scopeId,
        string provider,
        byte[] ciphertext,
        byte[] nonce,
        int keyVersion,
        DateTimeOffset updatedAt,
        long revision)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(nonce);

        if (!CredentialProviders.IsSupported(provider))
        {
            throw new ArgumentException(
                "The provider is not one of the supported credential identifiers.",
                nameof(provider));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A credential needs a non-empty id.", nameof(id));
        }

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A credential needs a non-empty scope.", nameof(scopeId));
        }

        if (nonce.Length != NonceSizeBytes)
        {
            throw new ArgumentException($"A credential nonce is {NonceSizeBytes} bytes.", nameof(nonce));
        }

        if (ciphertext.Length < MinimumCiphertextSizeBytes)
        {
            throw new ArgumentException(
                "A sealed credential is too short to carry an authentication tag.",
                nameof(ciphertext));
        }

        if (keyVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(keyVersion),
                keyVersion,
                "A credential records the key version it was sealed with, which starts at 1.");
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "The first stored revision is 1.");
        }

        Id = id;
        ScopeId = scopeId;
        Provider = provider;
        Ciphertext = ciphertext;
        Nonce = nonce;
        KeyVersion = keyVersion;
        UpdatedAt = updatedAt;
        Revision = revision;
    }

    /// <summary>The credential's own identity, and the only thing a queued job ever carries.</summary>
    public Guid Id { get; }

    /// <summary>The installation scope the credential belongs to and is readable from.</summary>
    public Guid ScopeId { get; }

    /// <summary>One of the supported provider identifiers.</summary>
    public string Provider { get; }

    /// <summary>The sealed secret, with the GCM tag in its trailing bytes. Never a plaintext.</summary>
    public byte[] Ciphertext { get; }

    /// <summary>The one-time nonce this ciphertext was sealed with.</summary>
    public byte[] Nonce { get; }

    /// <summary>The key version the ciphertext is bound to; opening needs the same version.</summary>
    public int KeyVersion { get; }

    /// <summary>When the owner last saved this credential.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>The optimistic revision an <c>If-Match</c> must carry to replace this credential.</summary>
    public long Revision { get; }

    /// <summary>
    /// The first version of a credential: sealed bytes, this instant, revision 1.
    /// </summary>
    public static Credential Create(
        Guid id,
        Guid scopeId,
        string provider,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> nonce,
        int keyVersion,
        DateTimeOffset updatedAt) =>
        new(id, scopeId, provider, ciphertext.ToArray(), nonce.ToArray(), keyVersion, updatedAt, revision: 1);
}

/// <summary>
/// The closed set of provider identifiers a credential may be stored for (CREDENTIAL-VAULT-002).
/// Anything outside it is refused, so the set of stored values is knowable and a mistyped identifier
/// cannot create a row nothing will ever read.
/// </summary>
public static class CredentialProviders
{
    /// <summary>The identifiers, in the order a settings surface lists them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "github",
        "openai",
        "gemini",
        "anthropic",
        "openrouter",
        "ollama",
        "publisher",
    ];

    /// <summary>The widest stored value, so the column and the API are bounded by the same number.</summary>
    public const int MaxLength = 32;

    /// <summary>True when the identifier is one of <see cref="All"/>.</summary>
    public static bool IsSupported(string? provider) =>
        provider is not null && All.Contains(provider, StringComparer.Ordinal);

    /// <summary>
    /// The identifier as stored, or null when it is not supported. Compared ordinally: provider names
    /// are not case-insensitive, so <c>GitHub</c> is a different string and is refused rather than
    /// silently folded into a provider the owner did not name.
    /// </summary>
    public static string? Normalize(string? provider) => IsSupported(provider) ? provider : null;
}
