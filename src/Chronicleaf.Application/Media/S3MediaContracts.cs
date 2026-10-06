using System.Security.Cryptography;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Application.Media;

/// <summary>
/// The operator's S3-compatible object-storage configuration (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// Everything here is installation-wide except the bytes' own scope: one bucket holds every
/// tenant's objects, and tenants are isolated by the <c>scopes/{scope}/</c> prefix the key shape
/// already carries rather than by per-tenant buckets or credentials. No tenant chooses an endpoint,
/// a bucket or a credential: those arrive here, from configuration, and a request cannot influence
/// any of them.
/// </para>
/// <para>
/// The secret itself is never in this object. <see cref="CredentialId"/> is a vault reference - the
/// id of the sealed <c>s3</c> credential the operator stored - and the plaintext is leased per
/// operation by <see cref="IS3CredentialSource"/> and never logged, never audited and never stored
/// in a row. The sealed document is one JSON object with exactly <c>accessKeyId</c> and
/// <c>secretAccessKey</c>; anything else is refused before a client is built.
/// </para>
/// </remarks>
public sealed class S3MediaOptions
{
    /// <summary>The provider this package leases its object-storage keys for.</summary>
    public const string CredentialProvider = "s3";

    /// <summary>The configuration section these values are read from.</summary>
    public const string SectionName = "Chronicleaf:S3";

    /// <summary>The longest endpoint, bucket, region or prefix this package accepts.</summary>
    public const int MaxFieldLength = 512;

    /// <summary>The longest presigned GET URL lifetime: five minutes, fixed by the design.</summary>
    public static readonly TimeSpan MaxPresignLifetime = TimeSpan.FromMinutes(5);

    /// <summary>S3-compatible endpoint, e.g. a MinIO host, an R2 endpoint or S3 itself.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>The bucket this installation owns. Never listed, never created here.</summary>
    public string BucketName { get; init; } = string.Empty;

    /// <summary>The signing region. Carried into every request; MinIO accepts any value.</summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>
    /// True unless the operator says otherwise. Path-style addressing is what MinIO and R2 speak,
    /// and virtual-hosted style would leak the bucket into DNS; the default is the compatible one.
    /// </summary>
    public bool ForcePathStyle { get; init; } = true;

    /// <summary>
    /// An optional installation prefix prepended to every key. Scoped keys stay scoped under it.
    /// </summary>
    public string KeyPrefix { get; init; } = string.Empty;

    /// <summary>The vault reference of the sealed <c>s3</c> credential document.</summary>
    public Guid CredentialId { get; init; } = Guid.Empty;

    /// <summary>
    /// True when the operator configured an endpoint, a bucket and a credential reference, which is
    /// the only state in which the S3 backend may be built. Anything else is Local mode, and Local
    /// mode never touches these values at all.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(BucketName)
        && CredentialId != Guid.Empty;

    /// <summary>
    /// Reads the options without validating them: validation is <see cref="Validate"/>, which fails
    /// the start rather than producing a backend that half-works.
    /// </summary>
    public static S3MediaOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new S3MediaOptions
        {
            Endpoint = configuration[$"{SectionName}:Endpoint"]?.Trim() ?? string.Empty,
            BucketName = configuration[$"{SectionName}:BucketName"]?.Trim() ?? string.Empty,
            Region = configuration[$"{SectionName}:Region"]?.Trim() is { Length: > 0 } region
                ? region
                : "us-east-1",
            ForcePathStyle = configuration[$"{SectionName}:ForcePathStyle"] is not string raw
                || !bool.TryParse(raw, out var parsed)
                || parsed,
            KeyPrefix = configuration[$"{SectionName}:KeyPrefix"]?.Trim() ?? string.Empty,
            CredentialId = Guid.TryParse(configuration[$"{SectionName}:CredentialId"], out var id)
                ? id
                : Guid.Empty,
        };
    }

    /// <summary>
    /// Refuses an incomplete or unbounded configuration. Called at start-up in S3 mode, so a
    /// deployment that names S3 without pointing it anywhere fails before serving rather than
    /// failing its first upload.
    /// </summary>
    /// <exception cref="InvalidOperationException">The S3 configuration is incomplete or unbounded.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint) || Endpoint.Length > MaxFieldLength)
        {
            throw new InvalidOperationException(
                "Chronicleaf:S3:Endpoint is required in S3 storage mode.");
        }

        if (string.IsNullOrWhiteSpace(BucketName)
            || BucketName.Length > MaxFieldLength
            || BucketName.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                "Chronicleaf:S3:BucketName is required in S3 storage mode.");
        }

        if (Region.Length > MaxFieldLength || Region.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("Chronicleaf:S3:Region is not a safe region name.");
        }

        if (KeyPrefix.Length > MaxFieldLength
            || KeyPrefix.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidOperationException("Chronicleaf:S3:KeyPrefix must contain no whitespace.");
        }

        if (CredentialId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Chronicleaf:S3:CredentialId is required in S3 storage mode: it names the sealed "
                + "s3 credential, never the keys themselves.");
        }
    }
}

/// <summary>
/// One leased S3 credential document, held for a single backend's lifetime and no longer
/// (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// A lease, not a value, in the same shape as the vault's own: the document buffer is owned here,
/// handed out as a span and zeroed on return with <see cref="CryptographicOperations.ZeroMemory"/>.
/// The factory parses the document once, copies the two key strings into the client's own
/// credentials, and returns this lease before the first request - so the sealed buffer below is
/// zeroed while the client's copies live only as long as the client, which the backend disposes.
/// </remarks>
public sealed class S3CredentialLease : IDisposable
{
    private byte[]? _document;
    private bool _returned;

    public S3CredentialLease(Guid scopeId, Guid credentialId, string provider, byte[] document)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        ArgumentNullException.ThrowIfNull(document);

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A leased S3 credential belongs to a scope.", nameof(scopeId));
        }

        ScopeId = scopeId;
        CredentialId = credentialId;
        Provider = provider;
        _document = document;
    }

    /// <summary>The scope the stored credential belongs to.</summary>
    public Guid ScopeId { get; }

    /// <summary>The vault credential this lease opened.</summary>
    public Guid CredentialId { get; }

    /// <summary>The provider the credential is stored for; always <c>s3</c> here.</summary>
    public string Provider { get; }

    /// <summary>True once the buffer has been zeroed.</summary>
    public bool IsReturned => _returned;

    /// <summary>The sealed document bytes. Throws once returned.</summary>
    public ReadOnlySpan<byte> Document =>
        _returned || _document is null
            ? throw new ObjectDisposedException(nameof(S3CredentialLease))
            : _document;

    public void Dispose()
    {
        if (_returned)
        {
            return;
        }

        _returned = true;

        if (_document is not null)
        {
            CryptographicOperations.ZeroMemory(_document);
            _document = null;
        }
    }
}

/// <summary>
/// Opens the installation's S3 key pair from the delivered credential vault
/// (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// The seam between the vault and the S3 backend, mirroring the provider adapters' own key
/// sources: the backend declares that it needs "keys, briefly" and only the composition root knows
/// the answer comes from a sealed vault document. The reference comes from the operator's
/// configuration rather than from a row, so there is no per-request scope to recheck against - the
/// configuration is trusted - but the stored provider is rechecked all the same, so a reference
/// that names another provider's credential cannot become S3 keys.
/// </remarks>
public interface IS3CredentialSource
{
    /// <summary>
    /// Leases the configured S3 key pair. The caller must return the lease; it owns the buffers
    /// from this call until it does.
    /// </summary>
    /// <exception cref="MediaStorageUnavailableException">
    /// No credential is configured, the reference names nothing, the stored provider is not
    /// <c>s3</c>, or the sealed document is not the expected key-pair shape (503).
    /// </exception>
    Task<S3CredentialLease> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The scoped, owner-private read of one stored image through a presigned URL
/// (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// Streaming stays the default read path: a presign is minted only by this service, only for a
/// <see cref="MediaStatus.Ready"/> record in the caller's own scope, and only for at most five
/// minutes. A foreign, missing or unservable id is the same 404 the streaming read answers, asked
/// before the provider is touched, so a caller cannot use a presign to probe another scope's
/// media. Expiry is structural rather than advisory: the URL carries the bound the provider
/// enforces, and this service never mints a longer one.
/// </remarks>
public interface IS3MediaLinkService
{
    /// <summary>
    /// Mints a scoped GET URL for one of this scope's ready images.
    /// </summary>
    /// <param name="owner">The scope the image must belong to.</param>
    /// <param name="mediaId">The image to authorize.</param>
    /// <param name="lifetime">How long the URL authorizes the read: positive, at most five minutes.</param>
    /// <exception cref="MediaNotFoundException">There is nothing here this caller may read (404).</exception>
    /// <exception cref="ValidationFailedException">The lifetime is not positive or is over five minutes (422).</exception>
    /// <exception cref="MediaStorageUnavailableException">The provider could not mint the URL (503).</exception>
    Task<PresignedMediaLink> PresignGetAsync(
        OwnerScope owner,
        Guid mediaId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A minted, bounded read of one stored image.
/// </summary>
/// <param name="MediaId">The image this URL authorizes.</param>
/// <param name="Url">The presigned GET URL. Opaque; safe to hand to the owner that asked.</param>
/// <param name="ExpiresAt">When the provider stops authorizing it, always UTC.</param>
public sealed record PresignedMediaLink(Guid MediaId, Uri Url, DateTimeOffset ExpiresAt);

/// <summary>
/// Copies one installation's verified media from one backend to another without ever unpublishing
/// the source (CLOUD-OBJECT-STORAGE-002).
/// </summary>
/// <remarks>
/// The tool the design calls for, and deliberately not a route and not a schedule: it moves bytes,
/// verifies them and flips the row's provider only after the target proves itself, and it never
/// deletes a source object - retention stays an explicit operator decision. A run is resumable
/// because every object carries its ledger entry: verified copies are re-verified and skipped,
/// everything else is copied again.
/// </remarks>
public interface IMediaMigrationService
{
    /// <summary>
    /// Copies every ready record of <paramref name="owner"/>'s scope that still names
    /// <paramref name="sourceProvider"/>, verifies each copy and flips the verified rows to
    /// <paramref name="targetProvider"/>.
    /// </summary>
    /// <param name="owner">The scope whose media moves. Only that scope's rows are read or written.</param>
    /// <param name="sourceProvider">Where the authoritative bytes are read from.</param>
    /// <param name="targetProvider">Where the verified bytes are written to.</param>
    /// <param name="limit">How many records to attempt, so a run stays bounded.</param>
    /// <returns>What was copied, what was already verified, and what still needs the operator.</returns>
    Task<MediaMigrationResult> MigrateAsync(
        OwnerScope owner,
        string sourceProvider,
        string targetProvider,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>One bounded migration run, told as counts rather than as bytes.</summary>
/// <param name="Copied">Records copied, verified and flipped to the target provider by this run.</param>
/// <param name="AlreadyVerified">Records whose target copy already verified, flipped without copying.</param>
/// <param name="Failures">Records that still name the source provider, and why each one does.</param>
public sealed record MediaMigrationResult(
    int Copied,
    int AlreadyVerified,
    IReadOnlyList<MediaCopyFailure> Failures);

/// <summary>One record a migration run left on its source provider, and the reason it is still there.</summary>
/// <param name="MediaId">The record that still names the source provider.</param>
/// <param name="Reason">A fixed, log-safe reason: no key, no digest and no secret.</param>
public sealed record MediaCopyFailure(Guid MediaId, string Reason);

/// <summary>
/// The migration ledger's stored side (CLOUD-OBJECT-STORAGE-002).
/// </summary>
/// <remarks>
/// Separate from <see cref="IMediaRepository"/> because the ledger is this package's working state
/// rather than the media record: a test or an operator can read what a run did without acquiring a
/// storage backend, and a ledger entry is never reachable by any path that resolves an object key.
/// Every method is scoped, and the scope is in the WHERE clause rather than in a filter applied
/// afterwards.
/// </remarks>
public interface IMediaMigrationRepository
{
    /// <summary>Reads one scope's ledger entry for a media record, or null when no run recorded one.</summary>
    Task<MediaMigrationCopy?> FindAsync(
        Guid scopeId,
        Guid mediaId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a copy's state, replacing whatever this scope recorded for the record before.
    /// The replace is by primary key, so two writers recording the same object converge rather than
    /// diverging.
    /// </summary>
    Task UpsertAsync(MediaMigrationCopy copy, CancellationToken cancellationToken = default);
}
