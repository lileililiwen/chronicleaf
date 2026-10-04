namespace Chronicleaf.AI.OpenAI;

/// <summary>
/// Hands the adapter one plaintext API key, for the length of one call
/// (LLM-OPENAI-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the seam that keeps the vault out of a transport.</b> The delivered
/// <c>ISecretVault</c> is a Chronicleaf.Application type, and an adapter that referenced it would
/// have a project reference into the product - which is exactly the dependency
/// LLM-PROVIDER-CONTRACT-001 exists to prevent. So the adapter declares what it needs ("a key, for a
/// scope, briefly") and the composition root supplies it, and only the composition root knows that the
/// answer comes from a leased, sealed, scope-bound credential.
/// </para>
/// <para>
/// The interface is deliberately narrow: it takes a scope and a credential id rather than a
/// <c>GenerationRequest</c>, so an implementation cannot read anything else out of the request. There
/// is no "list the keys", no "peek" and no "verify": the only operation is acquire, and the thing it
/// returns must be returned.
/// </para>
/// </remarks>
public interface IOpenAIApiKeySource
{
    /// <summary>
    /// Leases the key for <paramref name="credentialId"/>, which must belong to
    /// <paramref name="scopeId"/>.
    /// </summary>
    /// <param name="scopeId">The scope the call is being made for, and the scope the key must belong to.</param>
    /// <param name="credentialId">The stored credential the consent named.</param>
    /// <param name="cancellationToken">The call's own budget, honoured while the vault is opened.</param>
    /// <returns>A lease the caller must dispose before it returns.</returns>
    Task<OpenAIApiKeyLease> AcquireAsync(
        Guid scopeId,
        Guid credentialId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A leased API key, held for as long as one HTTP call needs it and for no longer
/// (LLM-OPENAI-ADAPTER-002).
/// </summary>
/// <remarks>
/// <para>
/// This is a lease rather than a string for the reason the delivered vault's own lease is: a
/// <c>string</c> is immutable, lives until the garbage collector collects it and cannot be zeroed. A
/// caller that holds one in a local for the length of a call has put a credential somewhere it will
/// outlive. The bytes here live in one buffer this object owns, are readable only as a span, and are
/// zeroed on <see cref="Dispose"/>.
/// </para>
/// <para>
/// The lease also carries the scope and provider of what it opened, so the adapter can check that a
/// reference handed to it still names a credential of its own scope <i>before</i> it reads a byte.
/// That recheck is not paranoia: a credential id is an identifier, and an id that arrives from a
/// queued payload has to be re-verified against the scope acting on it.
/// </para>
/// </remarks>
public sealed class OpenAIApiKeyLease : IDisposable
{
    private byte[]? _buffer;

    /// <summary>
    /// Takes ownership of the decrypted key bytes. A composition root constructs this; nothing in the
    /// adapter does, because nothing in the adapter has a plaintext buffer to hand over.
    /// </summary>
    /// <param name="scopeId">The scope the credential belongs to.</param>
    /// <param name="credentialId">The credential this lease opened.</param>
    /// <param name="provider">The provider the credential is stored for.</param>
    /// <param name="buffer">The key bytes. Ownership passes to the lease; the caller must not keep a reference.</param>
    public OpenAIApiKeyLease(Guid scopeId, Guid credentialId, string provider, byte[] buffer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(buffer);

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A leased key belongs to a scope.", nameof(scopeId));
        }

        if (credentialId == Guid.Empty)
        {
            throw new ArgumentException("A leased key names a credential.", nameof(credentialId));
        }

        if (buffer.Length == 0)
        {
            throw new ArgumentException("A leased key cannot be empty.", nameof(buffer));
        }

        ScopeId = scopeId;
        CredentialId = credentialId;
        Provider = provider;
        _buffer = buffer;
    }

    /// <summary>The scope the stored credential belongs to, for the caller's recheck.</summary>
    public Guid ScopeId { get; }

    /// <summary>The credential this lease opened.</summary>
    public Guid CredentialId { get; }

    /// <summary>The provider the credential is stored for.</summary>
    public string Provider { get; }

    /// <summary>True once the lease has been returned and its bytes zeroed.</summary>
    public bool IsReturned { get; private set; }

    /// <summary>
    /// The key bytes. A span over the leased buffer: copying them is the caller's decision, and the
    /// copy is the caller's to protect.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The lease has been returned.</exception>
    public ReadOnlySpan<byte> Key => Live();

    /// <summary>How many bytes are leased, so a caller can size a buffer before it asks for one.</summary>
    /// <exception cref="ObjectDisposedException">The lease has been returned.</exception>
    public int Length => Live().Length;

    /// <summary>
    /// Returns the lease: the buffer is zeroed in place and dropped, and the lease refuses to hand out
    /// the span again. Calling this more than once is safe.
    /// </summary>
    public void Dispose()
    {
        if (IsReturned)
        {
            return;
        }

        IsReturned = true;

        var buffer = Interlocked.Exchange(ref _buffer, null);

        if (buffer is not null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private ReadOnlySpan<byte> Live() =>
        _buffer ??
        throw new ObjectDisposedException(
            nameof(OpenAIApiKeyLease),
            "The lease has been returned and its bytes zeroed.");
}
