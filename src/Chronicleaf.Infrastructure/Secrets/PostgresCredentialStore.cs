using System.Data.Common;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Secrets;

/// <summary>
/// The credential metadata and sealed rows, in PostgreSQL (CREDENTIAL-VAULT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Every statement is parameterized and every one of them carries the scope in its WHERE clause, so
/// a forged parameter cannot read or write across installations. The read side returns metadata only:
/// this type has a method that reads the sealed bytes, and it is the only one, because the vault is
/// the only component that may have them.
/// </para>
/// <para>
/// The writes are statements rather than tracked entities, for the same reason the delivered stores
/// are: the replace is a revision compare-and-set the database decides, so two writers cannot both
/// believe they replaced the same revision, and a write that lost leaves the stored credential
/// byte-for-byte as it was.
/// </para>
/// </remarks>
public sealed class PostgresCredentialStore(ChronicleafDbContext dbContext) : ICredentialStore
{
    private const string MetadataColumns = "id, provider, updated_at, revision, key_version";

    /// <inheritdoc />
    public async Task<CredentialMetadata?> FindAsync(
        Guid scopeId,
        string provider,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<CredentialMetadata>(
            dbContext,
            $"SELECT {MetadataColumns} FROM credentials WHERE scope_id = @p0 AND provider = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Varchar, provider),
            ],
            CredentialRowReader.ReadMetadata,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<CredentialMetadata?> FindByIdAsync(
        Guid scopeId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<CredentialMetadata>(
            dbContext,
            $"SELECT {MetadataColumns} FROM credentials WHERE scope_id = @p0 AND id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, credentialId),
            ],
            CredentialRowReader.ReadMetadata,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CredentialMetadata>> ListAsync(
        Guid scopeId,
        string? afterProvider,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        // The provider is the sort key, so the page position is a string comparison and the cursor the
        // caller holds ends exactly where this starts.
        var after = afterProvider is null
            ? "AND (@p1::text IS NULL OR provider > @p1::text)"
            : "AND provider > @p1::text";

        return await ChronicleafSql.QueryAsync<CredentialMetadata>(
            dbContext,
            $"""
             SELECT {MetadataColumns} FROM credentials
             WHERE scope_id = @p0 {after}
             ORDER BY provider ASC
             LIMIT @p2
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Varchar, afterProvider),
                new("@p2", NpgsqlDbType.Integer, limit),
            ],
            CredentialRowReader.ReadMetadata,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> InsertAsync(Credential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // The unique (scope_id, provider) index decides the race, not a read this method did first.
        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO credentials (id, scope_id, provider, ciphertext, nonce, key_version, updated_at, revision)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7)
            ON CONFLICT (scope_id, provider) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, credential.Id),
                new("@p1", NpgsqlDbType.Uuid, credential.ScopeId),
                new("@p2", NpgsqlDbType.Varchar, credential.Provider),
                new("@p3", NpgsqlDbType.Bytea, credential.Ciphertext),
                new("@p4", NpgsqlDbType.Bytea, credential.Nonce),
                new("@p5", NpgsqlDbType.Integer, credential.KeyVersion),
                new("@p6", NpgsqlDbType.TimestampTz, credential.UpdatedAt),
                new("@p7", NpgsqlDbType.Bigint, credential.Revision),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    /// <inheritdoc />
    public Task<int> ReplaceAsync(
        Credential replacement,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        // The compare-and-set is the WHERE clause. Zero rows means the stored revision moved or the
        // row is gone, and the sealed bytes this caller produced are dropped with the statement.
        return ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE credentials
            SET ciphertext = @p3, nonce = @p4, key_version = @p5, updated_at = @p6, revision = @p7
            WHERE scope_id = @p0 AND id = @p1 AND revision = @p2
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, replacement.ScopeId),
                new("@p1", NpgsqlDbType.Uuid, replacement.Id),
                new("@p2", NpgsqlDbType.Bigint, expectedRevision),
                new("@p3", NpgsqlDbType.Bytea, replacement.Ciphertext),
                new("@p4", NpgsqlDbType.Bytea, replacement.Nonce),
                new("@p5", NpgsqlDbType.Integer, replacement.KeyVersion),
                new("@p6", NpgsqlDbType.TimestampTz, replacement.UpdatedAt),
                new("@p7", NpgsqlDbType.Bigint, replacement.Revision),
            ],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> DeleteAsync(
        Guid scopeId,
        Guid credentialId,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            "DELETE FROM credentials WHERE scope_id = @p0 AND id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, credentialId),
            ],
            cancellationToken);

    /// <inheritdoc />
    public Task<int> DeleteStoredAsync(
        Guid credentialId,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            "DELETE FROM credentials WHERE id = @p0",
            [new("@p0", NpgsqlDbType.Uuid, credentialId)],
            cancellationToken);
}

/// <summary>
/// The one place the sealed bytes are read, and the only reader that is not a metadata query
/// (CREDENTIAL-VAULT-001).
/// </summary>
/// <remarks>
/// It is internal to the Infrastructure assembly on purpose. The vault calls it, and the vault hands
/// what it returns to a lease; there is no public path from a request, a projection or a list onto
/// this statement, so a ciphertext cannot be read by anything but the component whose job is to open
/// it.
/// </remarks>
internal static class CredentialRowReader
{
    /// <summary>Reads the metadata columns. Never selects ciphertext or nonce.</summary>
    public static CredentialMetadata ReadMetadata(DbDataReader reader)
    {
        var id = reader.GetGuid(0);
        var provider = reader.GetString(1);
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(2);
        var revision = reader.GetInt64(3);
        var keyVersion = reader.GetInt32(4);

        return new CredentialMetadata(id, provider, updatedAt, revision, keyVersion);
    }

    /// <summary>Reads the whole sealed row, and is only ever called by the vault.</summary>
    public static SealedCredentialRow ReadSealed(DbDataReader reader)
    {
        var scopeId = reader.GetGuid(0);
        var id = reader.GetGuid(1);
        var provider = reader.GetString(2);
        var ciphertext = (byte[])reader.GetValue(3);
        var nonce = (byte[])reader.GetValue(4);
        var keyVersion = reader.GetInt32(5);

        return new SealedCredentialRow(
            scopeId,
            id,
            provider,
            ciphertext,
            nonce,
            keyVersion);
    }
}

/// <summary>A stored row exactly as the database holds it, ciphertext included.</summary>
internal sealed record SealedCredentialRow(
    Guid ScopeId,
    Guid Id,
    string Provider,
    byte[] Ciphertext,
    byte[] Nonce,
    int KeyVersion);
