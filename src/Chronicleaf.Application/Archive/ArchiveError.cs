using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Archive;

/// <summary>
/// The hosted-archive-operations envelope codes. They extend the shared envelope rather than
/// replacing it: the body shape stays <c>{code,message,traceId,fieldErrors?}</c> and the messages
/// are authored here, never taken from a backup, a key file or a database message.
/// </summary>
public static class ArchiveError
{
    /// <summary>The backup was sealed under a key version this deployment no longer verifies (409).</summary>
    public const string KeyMismatch = "archive_key_mismatch";

    /// <summary>The live scope's media inventory no longer matches the recorded backup (409).</summary>
    public const string MediaMismatch = "archive_media_mismatch";

    /// <summary>The backup was taken under a schema this deployment no longer verifies (409).</summary>
    public const string SchemaMismatch = "archive_schema_mismatch";

    /// <summary>The project already holds a live archive tombstone (409).</summary>
    public const string AlreadyDeleted = "archive_already_deleted";

    /// <summary>The recovery window has lapsed, so this tombstone restores nothing (410).</summary>
    public const string RestoreExpired = "archive_restore_expired";
}

/// <summary>
/// The backup was sealed under a key version the operator's current key no longer verifies.
/// The rehearsal is refused and nothing is promoted: a restore that cannot open its own secrets
/// is not a restore. The remedy is a backup under the current key, never a retry.
/// </summary>
public sealed class ArchiveKeyMismatchException(int expected, int recorded)
    : ApiException(
        ArchiveError.KeyMismatch,
        "This backup was sealed under a key version this deployment no longer verifies.")
{
    /// <summary>The key version the deployment verifies. The recorded one is never echoed.</summary>
    public int Expected { get; } = expected;

    /// <summary>The key version the backup was sealed under, for the operator's log.</summary>
    public int Recorded { get; } = recorded;
}

/// <summary>
/// The live scope's media inventory or content digest no longer matches the recorded backup -
/// an object is missing, added or changed since the manifest was written. The rehearsal is
/// refused and nothing is promoted: a recovery that is not byte-identical is a failure, loudly.
/// </summary>
public sealed class ArchiveMediaMismatchException(string inventory)
    : ApiException(
        ArchiveError.MediaMismatch,
        "The live archive no longer matches this backup: a media object or content record is missing or changed.")
{
    /// <summary>Which inventory drifted: <c>media</c> or <c>content</c>. No id is named.</summary>
    public string Inventory { get; } = inventory;
}

/// <summary>
/// The backup was taken under a schema version this deployment no longer verifies. The rehearsal
/// is refused and nothing is promoted: restoring rows this code cannot read is not a recovery.
/// The remedy is a backup under the current schema, never a retry.
/// </summary>
public sealed class ArchiveSchemaMismatchException(int expected, int recorded)
    : ApiException(
        ArchiveError.SchemaMismatch,
        "This backup was taken under a schema version this deployment no longer verifies.")
{
    /// <summary>The schema version the deployment verifies. The recorded one is never echoed.</summary>
    public int Expected { get; } = expected;

    /// <summary>The schema version the backup was taken under, for the operator's log.</summary>
    public int Recorded { get; } = recorded;
}

/// <summary>
/// The project already holds a live archive tombstone. A deletion that raced another deletion
/// loses loudly rather than merging two windows: the remedy is to read the stored tombstone.
/// </summary>
public sealed class ArchiveAlreadyDeletedException()
    : ApiException(
        ArchiveError.AlreadyDeleted,
        "This project already holds a live archive tombstone.");
/// <summary>
/// The recovery window has lapsed, so this tombstone restores nothing. The remedy is the
/// operator purge, not a retry: thirty days is the window the delete disclosed.
/// </summary>
public sealed class ArchiveRestoreExpiredException()
    : ApiException(
        ArchiveError.RestoreExpired,
        "The recovery window for this project has lapsed, so it restores nothing.");
