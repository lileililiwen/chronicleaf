using System.Globalization;

namespace Chronicleaf.Core;

/// <summary>
/// The four states a stored media object may hold (LOCAL-MEDIA-STORAGE-001/002).
/// </summary>
/// <remarks>
/// The set is closed and only this package's store moves a record between them. The interesting
/// distinction is between <see cref="Ready"/> and <see cref="Pending"/>: a pending record is one an
/// upload staged but never published, so it has no reference, is never served and is reconcilable.
/// A record reaches <see cref="Ready"/> only after its bytes are verified present under the key the
/// row names, which is the invariant that makes "no ready row points at missing bytes" a property of
/// the sequence rather than a hope.
/// <para>
/// <see cref="Retained"/> and <see cref="Deleted"/> are both terminal for serving - neither is ever
/// served - and they differ in why. A retained record still has bytes because something references
/// it; a deleted record's bytes are gone. Both stop being readable, so a reader learns nothing about
/// whether the object is still physically present.
/// </para>
/// </remarks>
public static class MediaStatus
{
    /// <summary>
    /// Bytes are staged under a server-generated pending key and the promotion to
    /// <see cref="Ready"/> has not happened. Never served, never referenced.
    /// </summary>
    public const string Pending = "pending";

    /// <summary>Verified bytes under a promoted key, registered, and servable to its own scope.</summary>
    public const string Ready = "ready";

    /// <summary>Not servable, and its bytes must survive because something still references it.</summary>
    public const string Retained = "retained";

    /// <summary>Not servable, its bytes are gone, and the row is the record that they were.</summary>
    public const string Deleted = "deleted";

    public static IReadOnlyList<string> All { get; } = [Pending, Ready, Retained, Deleted];

    /// <summary>
    /// True only for <see cref="Ready"/>. A read, a serving and a promotion target all test this,
    /// so there is exactly one definition of "may be served" in the repository.
    /// </summary>
    public static bool IsServable(string status) => string.Equals(status, Ready, StringComparison.Ordinal);
}

/// <summary>
/// The one image type this package stores (LOCAL-MEDIA-STORAGE-001).
/// </summary>
/// <remarks>
/// The set is closed. A type is decided by decoding the bytes, never by what a caller declared in a
/// <c>Content-Type</c> header or a filename, so a request that lies about its content is refused by
/// the same check that accepts an honest one. The design's wider PNG/JPEG vocabulary is narrowed to
/// PNG for this package: the acceptance and its test are both stated in terms of a decoded PNG, and a
/// second decoder would be a second thing to get wrong for no outcome in scope.
/// </remarks>
public static class MediaTypes
{
    /// <summary>The only accepted type, stored and served as this exact string.</summary>
    public const string Png = "image/png";

    public static IReadOnlyList<string> All { get; } = [Png];
}

/// <summary>
/// The bounds one accepted image has to satisfy (LOCAL-MEDIA-STORAGE-001).
/// </summary>
/// <remarks>
/// Both bounds are structural rather than advisory: the byte bound is what makes a request that
/// cannot be buffered impossible, and the pixel bound is what stops an image whose declared
/// dimensions are small enough to store and large enough to be a denial of service for every later
/// reader that decodes it. Neither value is taken from a request.
/// </remarks>
public static class MediaLimits
{
    /// <summary>20 MiB, fixed by the design as the limit for one original image.</summary>
    public const long MaximumByteLength = 20L * 1024 * 1024;

    /// <summary>
    /// 40 megapixels, fixed by the design. Read from the decoded header, not from a declared value,
    /// so it is the image's real size that is bounded.
    /// </summary>
    public const long MaximumPixelCount = 40_000_000;

    /// <summary>
    /// A key this package generates is far shorter than the 1024 characters
    /// <c>StorageObjectKey</c> accepts. The bound is stated here so the headroom is visible: a scope
    /// and a project id are UUIDs, so the only variable part is the media id, also a UUID.
    /// </summary>
    public const int MaximumKeyLength = 1024;
}

/// <summary>
/// The backends a stored media object may live on (CLOUD-OBJECT-STORAGE-001/002).
/// </summary>
/// <remarks>
/// The set is closed and only this package's stores move a record between them. The value is a
/// record of where the verified bytes are, not a request: a row says <see cref="S3"/> only after
/// the migration has copied its bytes to the configured bucket and verified the digest there, and
/// it says <see cref="Local"/> otherwise. Serving reads the value rather than the installation's
/// configured mode, which is what lets a migration move one installation's rows gradually while
/// every row keeps pointing at bytes that exist.
/// </remarks>
public static class StorageModes
{
    /// <summary>The filesystem adapter the Community media package is scoped to.</summary>
    public const string Local = "local";

    /// <summary>The S3-compatible adapter this package is scoped to.</summary>
    public const string S3 = "s3";

    public static IReadOnlyList<string> All { get; } = [Local, S3];

    /// <summary>True only for one of the two fixed values, compared ordinally.</summary>
    public static bool IsSupported(string? mode) =>
        mode is not null && All.Contains(mode, StringComparer.Ordinal);
}

/// <summary>
/// One stored image, and the only description of it that exists (LOCAL-MEDIA-STORAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StorageKey"/> is a portable object key, not a path. It is generated here and never
/// accepted from a caller, it is the only thing that reaches the storage provider, and the row holds
/// no absolute filesystem location at all - so a disclosure of this table leaks a scoped opaque
/// string and not where the bytes live on the operator's disk.
/// </para>
/// <para>
/// <see cref="ScopeId"/> is the installation that owns the image and is what every read is filtered
/// by, so an id from another scope is not found rather than refused. <see cref="ProjectId"/> is the
/// project the image belongs to and is part of the key, which is what keeps one project's media from
/// being reachable through another project's lookup.
/// </para>
/// <para>
/// <see cref="Sha256"/> is the lower-case hex digest of the exact accepted bytes, computed once
/// before the first write and compared again after promotion, so "the stored hash" and "the served
/// bytes" are the same fact checked rather than assumed. <see cref="ByteLength"/> and
/// <see cref="MediaType"/> are decoded facts too: the length is the number of bytes actually
/// written, and the type is the type the bytes are.
/// </para>
/// <para>
/// <see cref="OwnerOperationId"/> names the background operation that produced this image, when one
/// did. It is nullable because a media object the owner uploaded by hand has no operation behind it,
/// and it is an id rather than a payload: it records who decided the bytes exist without copying
/// anything about them.
/// </para>
/// <para>
/// <see cref="Revision"/> is the optimistic concurrency token every other Chronicleaf record
/// carries, so a client states which version of a media record it is acting on. A media record's
/// state is not editable, so the token in practice only ever advances when the record is retired, and
/// its presence is what stops a later writer from treating a retired record as live.
/// </para>
/// </remarks>
public sealed class Media
{
    /// <summary>64 lower-case hex characters, the length of a SHA-256 digest.</summary>
    public const int Sha256HexLength = 64;

    /// <summary>The longest <see cref="MediaStatus"/> value; see the <c>media.status</c> column.</summary>
    public const int StatusMaxLength = 16;

    /// <summary>The longest storage-mode value; see the <c>media.provider_id</c> column.</summary>
    public const int ProviderIdMaxLength = 32;

    /// <summary>The longest accepted media type; see the <c>media.media_type</c> column.</summary>
    public const int MediaTypeMaxLength = 100;

    public Media(
        Guid id,
        Guid scopeId,
        Guid projectId,
        string storageKey,
        string mediaType,
        long byteLength,
        string sha256,
        string status,
        DateTimeOffset createdAt,
        Guid? ownerOperationId,
        long revision,
        string providerId)
    {
        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        StorageKey = storageKey;
        MediaType = mediaType;
        ByteLength = byteLength;
        Sha256 = sha256;
        Status = status;
        CreatedAt = createdAt;
        OwnerOperationId = ownerOperationId;
        Revision = revision;
        ProviderId = providerId;
    }

    /// <summary>This record's identity, and the id a caller addresses it by.</summary>
    public Guid Id { get; }

    /// <summary>The installation that owns the image; every read is filtered by it.</summary>
    public Guid ScopeId { get; }

    /// <summary>The project the image belongs to, and the middle segment of its key.</summary>
    public Guid ProjectId { get; }

    /// <summary>
    /// The server-generated object key the bytes are stored under. Portable, scoped, and never a
    /// filesystem path.
    /// </summary>
    public string StorageKey { get; }

    /// <summary>The decoded media type; one of the fixed <see cref="MediaTypes"/> values.</summary>
    public string MediaType { get; }

    /// <summary>How many bytes were verified and stored; always positive.</summary>
    public long ByteLength { get; }

    /// <summary>Lower-case hex SHA-256 of exactly those bytes.</summary>
    public string Sha256 { get; }

    /// <summary>One of the fixed <see cref="MediaStatus"/> values.</summary>
    public string Status { get; }

    /// <summary>When the record was created, always in UTC and always from the injected clock.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>The operation that produced this image, or <c>null</c> when the owner uploaded it.</summary>
    public Guid? OwnerOperationId { get; }

    /// <summary>The optimistic revision; the first committed version is 1.</summary>
    public long Revision { get; }

    /// <summary>
    /// The backend the verified bytes live on: one of the fixed <see cref="StorageModes"/> values.
    /// A row names <c>s3</c> only after the migration has verified its bytes under the same scoped
    /// key in the configured bucket, so serving can trust the value rather than re-proving it.
    /// </summary>
    public string ProviderId { get; }

    /// <summary>
    /// True only while this record may be served. It is
    /// <see cref="MediaStatus.IsServable"/> applied to this record, so a read path that forgot to
    /// test a status cannot accidentally agree with this by accident.
    /// </summary>
    public bool IsServable => MediaStatus.IsServable(Status);
}

/// <summary>
/// The states one copied media object may hold in the migration ledger
/// (CLOUD-OBJECT-STORAGE-002).
/// </summary>
/// <remarks>
/// The set is closed. A copy is <see cref="Started"/> from the moment its bytes are being moved and
/// <see cref="Verified"/> only after the target's own bytes hash to the source row's digest, at
/// which point the media row may name the target provider. <see cref="Failed"/> records a copy
/// whose bytes did not verify or could not be moved; the source row is untouched in both cases, so
/// a retry resumes rather than repairs.
/// </remarks>
public static class MediaCopyStates
{
    /// <summary>The copy is in flight or was interrupted before verification.</summary>
    public const string Started = "started";

    /// <summary>The target bytes hash to the source digest; the row may switch providers.</summary>
    public const string Verified = "verified";

    /// <summary>The copy could not be moved or did not verify; the source row is still authoritative.</summary>
    public const string Failed = "failed";

    public static IReadOnlyList<string> All { get; } = [Started, Verified, Failed];
}

/// <summary>
/// One media object's migration ledger entry (CLOUD-OBJECT-STORAGE-002).
/// </summary>
/// <remarks>
/// <para>
/// This is the restart record, not the bytes: it names the source and target keys, the digest the
/// source row carries and the digest that was actually read back from the target, and the state that
/// decides whether a later run copies again or skips. A second run that finds
/// <see cref="MediaCopyStates.Verified"/> re-verifies the target rather than trusting this row, so
/// the ledger can resume work but can never declare bytes good that are not.
/// </para>
/// <para>
/// The two keys are usually the same scoped string on two backends - the key shape is portable by
/// design - and both are recorded anyway, so a future backend whose physical layout differs is
/// described rather than assumed.
/// </para>
/// </remarks>
public sealed class MediaMigrationCopy
{
    /// <summary>The longest copy-state value; see the <c>media_migration_copies.state</c> column.</summary>
    public const int StateMaxLength = 16;

    public MediaMigrationCopy(
        Guid mediaId,
        Guid scopeId,
        string sourceProvider,
        string targetProvider,
        string sourceKey,
        string targetKey,
        string sourceSha256,
        string? copiedSha256,
        string state,
        DateTimeOffset updatedAt)
    {
        MediaId = mediaId;
        ScopeId = scopeId;
        SourceProvider = sourceProvider;
        TargetProvider = targetProvider;
        SourceKey = sourceKey;
        TargetKey = targetKey;
        SourceSha256 = sourceSha256;
        CopiedSha256 = copiedSha256;
        State = state;
        UpdatedAt = updatedAt;
    }

    /// <summary>The media record this copy is about; also the ledger's primary key.</summary>
    public Guid MediaId { get; }

    /// <summary>The installation that owns the image; every ledger read is filtered by it.</summary>
    public Guid ScopeId { get; }

    /// <summary>Where the authoritative bytes were read from; one of <see cref="StorageModes"/>.</summary>
    public string SourceProvider { get; }

    /// <summary>Where the bytes were written to; one of <see cref="StorageModes"/>.</summary>
    public string TargetProvider { get; }

    /// <summary>The scoped key the bytes were read from.</summary>
    public string SourceKey { get; }

    /// <summary>The scoped key the bytes were written to.</summary>
    public string TargetKey { get; }

    /// <summary>The digest the source media row carries.</summary>
    public string SourceSha256 { get; }

    /// <summary>The digest read back from the target, or null before anything verified.</summary>
    public string? CopiedSha256 { get; }

    /// <summary>One of the fixed <see cref="MediaCopyStates"/> values.</summary>
    public string State { get; }

    /// <summary>When this entry was last written, always UTC and always from the injected clock.</summary>
    public DateTimeOffset UpdatedAt { get; }
}

/// <summary>
/// The storage keys and digests this package generates and checks (LOCAL-MEDIA-STORAGE-001/002).
/// </summary>
/// <remarks>
/// Key construction lives here, in the domain, and nowhere else: one method produces every key, so
/// "the key is server-generated" is a statement about a single code path rather than a convention
/// several paths are trusted to follow. The shape is
/// <c>scopes/{scopeId}/projects/{projectId}/media/{stage}/{mediaId}.png</c>, where
/// <see cref="MediaStatus.Pending"/> and <see cref="MediaStatus.Ready"/> are the two stages, so a
/// staged object and a published one can never collide and an interrupted upload is identifiable
/// from its key alone without a database read.
/// <para>
/// The trailing extension is deliberate. The Local adapter reports an object's content type from its
/// file extension, and the key has to be extensionless-free of magic numbers for that report to be
/// <c>image/png</c> rather than a generic octet stream. The media type a caller is served is still
/// the decoded one from the row, so this only keeps the provider's own metadata honest.
/// </para>
/// <para>
/// Nothing here interprets a caller-supplied string, because nothing here is given one. A key is
/// always built from three UUIDs and two fixed words, which is why
/// <c>StorageObjectKey</c>'s own validation - no empty segment, no <c>.</c> or <c>..</c>, no control
/// character, at most 1024 characters - can never be the thing standing between a request and a
/// traversal: the shape is simply not reachable from outside.
/// </para>
/// </remarks>
public static class MediaValues
{
    private const string ScopesSegment = "scopes";
    private const string ProjectsSegment = "projects";
    private const string MediaSegment = "media";

    /// <summary>The revision every newly registered media record starts at.</summary>
    public const long InitialRevision = 1;

    /// <summary>
    /// The key staged bytes are written to before promotion. A record that still names this key was
    /// never promoted, so it is unservable and reconcilable.
    /// </summary>
    public static string PendingStorageKey(Guid scopeId, Guid projectId, Guid mediaId) =>
        Build(scopeId, projectId, mediaId, MediaStatus.Pending);

    /// <summary>
    /// The key a promoted object is served from. This is the key a <see cref="MediaStatus.Ready"/>
    /// row names, and it exists only after the bytes under it have been verified.
    /// </summary>
    public static string ReadyStorageKey(Guid scopeId, Guid projectId, Guid mediaId) =>
        Build(scopeId, projectId, mediaId, MediaStatus.Ready);

    /// <summary>
    /// True when <paramref name="key"/> is the key a pending upload stages under, which is how an
    /// orphan is recognised from its key alone.
    /// </summary>
    public static bool IsPendingStorageKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key.Contains(
            $"/{MediaSegment}/{MediaStatus.Pending}/",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// True for a lower-case hex SHA-256 digest. Used to check a computed digest before it is
    /// stored and a row's stored digest before it is trusted, so a malformed one is refused rather
    /// than compared.
    /// </summary>
    public static bool IsSha256Hex(string value) =>
        value is { Length: Media.Sha256HexLength }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Build(Guid scopeId, Guid projectId, Guid mediaId, string stage) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ScopesSegment}/{scopeId:D}/{ProjectsSegment}/{projectId:D}/{MediaSegment}/{stage}/{mediaId:D}.png");
}
