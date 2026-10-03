using System.Data.Common;
using Chronicleaf.Application.Voice;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Voice;

/// <summary>
/// The voice tables in PostgreSQL (VOICE-PROFILES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Every statement is parameterized and every one of them carries the scope in its WHERE clause, so
/// a forged parameter cannot read or write across installations. A read for an id that exists in
/// another scope returns nothing, and a write for one changes nothing - which is what makes a
/// foreign-scoped profile a 404 rather than a leak.
/// </para>
/// <para>
/// The writes are statements rather than tracked entities, for the reason the delivered stores use:
/// the replace is a revision compare-and-set the database decides, so two writers cannot both
/// believe they replaced the same revision, and a write that lost leaves the stored row byte for
/// byte as it was.
/// </para>
/// <para>
/// <see cref="ReplaceDraftTextAsync"/> has no <c>voice_snapshot</c> in its SET list. That omission
/// is the requirement's boundary scenario expressed as SQL: an edit replaces an owner's words and
/// can never reach the voice those words were written under.
/// </para>
/// </remarks>
public sealed class PostgresVoiceStore(ChronicleafDbContext dbContext) : IVoiceProfileStore
{
    private const string ProfileColumns =
        "id, scope_id, project_id, preset_id, overrides, revision, created_at, updated_at";

    private const string DraftColumns =
        "id, scope_id, project_id, title, body, status, revision, created_at, updated_at, voice_snapshot";

    /// <inheritdoc />
    public async Task<VoiceProfile?> FindProfileAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<VoiceProfile>(
            dbContext,
            $"SELECT {ProfileColumns} FROM voice_profiles WHERE scope_id = @p0 AND project_id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
            ],
            VoiceRowReader.ReadProfile,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<bool> InsertProfileAsync(VoiceProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // The unique (scope_id, project_id) index decides the race, not a read this method did first.
        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO voice_profiles
                (id, scope_id, project_id, preset_id, overrides, revision, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7)
            ON CONFLICT (scope_id, project_id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, profile.Id),
                new("@p1", NpgsqlDbType.Uuid, profile.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, profile.ProjectId),
                new("@p3", NpgsqlDbType.Varchar, profile.PresetId),
                new("@p4", NpgsqlDbType.Jsonb, VoiceOverridesCodec.Serialize(profile.Overrides)),
                new("@p5", NpgsqlDbType.Bigint, profile.Revision),
                new("@p6", NpgsqlDbType.TimestampTz, profile.CreatedAt),
                new("@p7", NpgsqlDbType.TimestampTz, profile.UpdatedAt),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    /// <inheritdoc />
    public Task<int> ReplaceProfileAsync(
        Guid scopeId,
        Guid projectId,
        long expectedRevision,
        string presetId,
        VoiceOverrides overrides,
        long newRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        // The compare-and-set is the WHERE clause. Zero rows means the stored revision moved or the
        // row is gone, and the write that lost is dropped with the statement.
        ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE voice_profiles
            SET preset_id = @p3, overrides = @p4, revision = @p5, updated_at = @p6
            WHERE scope_id = @p0 AND project_id = @p1 AND revision = @p2
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
                new("@p2", NpgsqlDbType.Bigint, expectedRevision),
                new("@p3", NpgsqlDbType.Varchar, presetId),
                new("@p4", NpgsqlDbType.Jsonb, VoiceOverridesCodec.Serialize(overrides)),
                new("@p5", NpgsqlDbType.Bigint, newRevision),
                new("@p6", NpgsqlDbType.TimestampTz, updatedAt),
            ],
            cancellationToken);

    /// <inheritdoc />
    public async Task<VoiceDraft?> FindDraftAsync(
        Guid scopeId,
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<VoiceDraft>(
            dbContext,
            $"SELECT {DraftColumns} FROM voice_drafts WHERE scope_id = @p0 AND id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, draftId),
            ],
            VoiceRowReader.ReadDraft,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VoiceDraft>> ListDraftsAsync(
        Guid scopeId,
        Guid projectId,
        DateTimeOffset? createdBefore,
        Guid? idBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        // The cursor ends exactly where this starts: the last row the caller saw is the first row
        // this statement refuses, and the id breaks a tie between two drafts of the same instant.
        var after = createdBefore is null || idBefore is null
            ? "AND (@p2::timestamptz IS NULL OR (created_at, id) < (@p2::timestamptz, @p3::uuid))"
            : "AND (created_at, id) < (@p2::timestamptz, @p3::uuid)";

        var rows = await ChronicleafSql.QueryAsync<VoiceDraft>(
            dbContext,
            $"""
             SELECT {DraftColumns} FROM voice_drafts
             WHERE scope_id = @p0 AND project_id = @p1 {after}
             ORDER BY created_at DESC, id DESC
             LIMIT @p4
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
                new("@p2", NpgsqlDbType.TimestampTz, createdBefore),
                new("@p3", NpgsqlDbType.Uuid, idBefore),
                new("@p4", NpgsqlDbType.Integer, limit),
            ],
            VoiceRowReader.ReadDraft,
            cancellationToken).ConfigureAwait(false);

        return rows;
    }

    /// <inheritdoc />
    public async Task<bool> InsertDraftAsync(VoiceDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        // The snapshot is written in the same statement that creates the draft, from the values the
        // service just resolved. There is no second statement that could set it later.
        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO voice_drafts
                (id, scope_id, project_id, title, body, status, revision, created_at, updated_at, voice_snapshot)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9)
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, draft.Id),
                new("@p1", NpgsqlDbType.Uuid, draft.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, draft.ProjectId),
                new("@p3", NpgsqlDbType.Text, draft.Title),
                new("@p4", NpgsqlDbType.Text, draft.Body),
                new("@p5", NpgsqlDbType.Varchar, draft.Status),
                new("@p6", NpgsqlDbType.Bigint, draft.Revision),
                new("@p7", NpgsqlDbType.TimestampTz, draft.CreatedAt),
                new("@p8", NpgsqlDbType.TimestampTz, draft.UpdatedAt),
                new("@p9", NpgsqlDbType.Jsonb, VoiceSnapshotCodec.Serialize(draft.VoiceSnapshot)),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    /// <inheritdoc />
    public Task<int> ReplaceDraftTextAsync(
        Guid scopeId,
        Guid draftId,
        long expectedRevision,
        string title,
        string body,
        long newRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        // voice_snapshot is intentionally not in this statement. Editing an owner's text must not be
        // able to reach the voice that text is judged under.
        ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE voice_drafts
            SET title = @p3, body = @p4, revision = @p5, updated_at = @p6
            WHERE scope_id = @p0 AND id = @p1 AND revision = @p2
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, draftId),
                new("@p2", NpgsqlDbType.Bigint, expectedRevision),
                new("@p3", NpgsqlDbType.Text, title),
                new("@p4", NpgsqlDbType.Text, body),
                new("@p5", NpgsqlDbType.Bigint, newRevision),
                new("@p6", NpgsqlDbType.TimestampTz, updatedAt),
            ],
            cancellationToken);

    /// <inheritdoc />
    public Task<int> ApproveDraftAsync(
        Guid scopeId,
        Guid draftId,
        long expectedRevision,
        long newRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE voice_drafts
            SET status = 'approved', revision = @p3, updated_at = @p4
            WHERE scope_id = @p0 AND id = @p1 AND revision = @p2
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, draftId),
                new("@p2", NpgsqlDbType.Bigint, expectedRevision),
                new("@p3", NpgsqlDbType.Bigint, newRevision),
                new("@p4", NpgsqlDbType.TimestampTz, updatedAt),
            ],
            cancellationToken);
}

/// <summary>
/// Materialises the two voice rows from their stored columns (VOICE-PROFILES-001/002).
/// </summary>
/// <remarks>
/// A row whose <c>overrides</c> or <c>voice_snapshot</c> column cannot be decoded is skipped rather
/// than reconstructed: a profile this build cannot read its own overrides from would resolve to
/// values the owner never set, and a draft whose snapshot is unreadable cannot honestly be said to
/// have a voice. Skipping makes it absent, which is a 404 and a visible gap rather than a wrong
/// answer.
/// </remarks>
internal static class VoiceRowReader
{
    public static VoiceProfile ReadProfile(DbDataReader reader)
    {
        var overrides = VoiceOverridesCodec.Deserialize(reader.GetString(4))
            ?? throw new InvalidOperationException("The stored voice overrides are not readable.");

        return new VoiceProfile(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            overrides,
            reader.GetInt64(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    public static VoiceDraft ReadDraft(DbDataReader reader)
    {
        var snapshot = VoiceSnapshotCodec.Deserialize(reader.GetString(9))
            ?? throw new InvalidOperationException("The stored voice snapshot is not readable.");

        return new VoiceDraft(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            snapshot);
    }
}
