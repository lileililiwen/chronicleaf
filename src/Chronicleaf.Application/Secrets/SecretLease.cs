using System.Security.Cryptography;

namespace Chronicleaf.Application.Secrets;

/// <summary>
/// A resolved secret, held for as long as the caller needs it and for no longer
/// (CREDENTIAL-VAULT-001).
/// </summary>
/// <remarks>
/// <para>
/// This is a lease, not a value. The bytes live in one buffer that this object owns; the only way to
/// read them is <see cref="Secret"/>, which hands out a span over that buffer and never a copy and
/// never a string. Returning the lease zeroes the buffer with
/// <see cref="CryptographicOperations.ZeroMemory"/>, which a plain assignment would let the
/// garbage collector leave behind for the rest of the process's life.
/// </para>
/// <para>
/// After the lease is returned the span is gone: <see cref="Secret"/> throws rather than handing out
/// bytes that happen to still be in the array, so a disposed lease cannot be read by a caller that
/// kept it. Disposing twice is a no-op, and <see cref="IsReturned"/> says which state the lease is in
/// so a caller can release it in a <c>finally</c> without tracking it by hand.
/// </para>
/// <para>
/// The lease also carries the scope, provider and key version of what it opened, so a caller can
/// check that a reference handed to it still names a credential of its own scope before it reads a
/// single byte. That is the recheck a queued job performs on a reference it read out of a payload.
/// </para>
/// </remarks>
public sealed class SecretLease : IDisposable
{
    private byte[]? _buffer;

    /// <summary>
    /// Takes ownership of the decrypted bytes. The vault constructs the lease; nothing else does,
    /// because nothing else has a plaintext buffer to hand over.
    /// </summary>
    /// <param name="reference">The credential this lease opened.</param>
    /// <param name="scopeId">The scope the stored credential belongs to.</param>
    /// <param name="provider">The provider the credential is stored for.</param>
    /// <param name="keyVersion">The key version the stored bytes were sealed with.</param>
    /// <param name="buffer">
    /// The decrypted bytes. Ownership passes to the lease: the caller must not keep a reference to
    /// the array, or the zeroing below would be undone by that reference.
    /// </param>
    public SecretLease(
        SecretRef reference,
        Guid scopeId,
        string provider,
        int keyVersion,
        byte[] buffer)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        ArgumentNullException.ThrowIfNull(buffer);

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A leased secret belongs to a scope.", nameof(scopeId));
        }

        if (keyVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(keyVersion),
                keyVersion,
                "A leased secret records the key version it was sealed with, which starts at 1.");
        }

        Reference = reference;
        ScopeId = scopeId;
        Provider = provider;
        KeyVersion = keyVersion;
        _buffer = buffer;
    }

    /// <summary>The credential this lease opened.</summary>
    public SecretRef Reference { get; }

    /// <summary>The scope the stored credential belongs to, for the caller's recheck.</summary>
    public Guid ScopeId { get; }

    /// <summary>The provider the stored credential is for.</summary>
    public string Provider { get; }

    /// <summary>The key version the stored bytes were sealed with.</summary>
    public int KeyVersion { get; }

    /// <summary>True once the lease has been returned and its bytes zeroed.</summary>
    public bool IsReturned { get; private set; }

    /// <summary>
    /// The leased bytes. A span over the leased buffer: copying it is the caller's decision, and the
    /// copy is the caller's to protect.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The lease has been returned.</exception>
    public ReadOnlySpan<byte> Secret => Live();

    /// <summary>How many bytes are leased, so a caller can size a buffer before it asks for one.</summary>
    /// <exception cref="ObjectDisposedException">The lease has been returned.</exception>
    public int Length => Live().Length;

    /// <summary>
    /// Returns the lease: the buffer is zeroed in place and then dropped, and the lease refuses to
    /// hand out the span again. Calling this more than once is safe and does nothing after the first.
    /// </summary>
    public void Dispose()
    {
        if (IsReturned)
        {
            return;
        }

        IsReturned = true;

        // Exchange rather than clear the field: a lease returned on one thread while another is
        // mid-read is a bug the caller has, but it must not leave the bytes readable afterwards.
        var buffer = Interlocked.Exchange(ref _buffer, null);

        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private ReadOnlySpan<byte> Live() =>
        _buffer ??
        throw new ObjectDisposedException(
            nameof(SecretLease),
            "The lease has been returned and its bytes zeroed.");
}
