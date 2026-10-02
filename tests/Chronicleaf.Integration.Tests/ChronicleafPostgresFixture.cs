using Chronicleaf.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The disposable real PostgreSQL every persistence test in this assembly runs against.
/// </summary>
/// <remarks>
/// The database is a genuine PostgreSQL server started from a pinned image in Docker: schema
/// application, transaction rollback, constraint enforcement and connection failure are all
/// produced by that server, never by an in-memory provider or a stub. The container is removed
/// when the fixture is disposed, and each test receives its own database on the shared server so
/// row counts are deterministic without cross-test coupling.
/// </remarks>
public sealed class ChronicleafPostgresFixture : IAsyncLifetime
{
    /// <summary>Pinned image tag. Change it deliberately, never to a floating tag.</summary>
    public const string Image = "postgres:17.6-alpine";

    private const string DatabaseName = "chronicleaf";
    private const string Username = "chronicleaf";
    private const string Password = "chronicleaf-test";

    private PostgreSqlContainer? _database;

    /// <summary>True once a real container is running; asserted so a stub can never pass as one.</summary>
    public bool IsRealPostgres => _database is not null;

    public async Task InitializeAsync()
    {
        _database = new PostgreSqlBuilder(Image)
            .WithDatabase(DatabaseName)
            .WithUsername(Username)
            .WithPassword(Password)
            .WithCleanUp(true)
            .Build();

        await _database.StartAsync().ConfigureAwait(false);

        // Fail loudly here rather than silently falling back: a missing database is a failed
        // prerequisite, never a passing mock runtime.
        await using var connection = new NpgsqlConnection(_database.GetConnectionString());
        await connection.OpenAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync().ConfigureAwait(false);
            _database = null;
        }
    }

    /// <summary>Creates an empty database on the running server and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        EnsureRunning();

        var name = $"chronicleaf_{purpose}_{Guid.NewGuid():N}"[..40].ToLowerInvariant();
        await using (var connection = new NpgsqlConnection(ServerConnectionString()))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return $"Host={_database!.Hostname};Port={_database.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort)};Database={name};Username={Username};Password={Password};Timeout=5;Command Timeout=5";
    }

    /// <summary>A connection string for the server itself, used to create and drop databases.</summary>
    private string ServerConnectionString() =>
        $"Host={_database!.Hostname};Port={_database.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort)};Database=postgres;Username={Username};Password={Password};Timeout=5";

    /// <summary>
    /// Applies the committed EF migrations, exactly as the operator command does. Applying it a
    /// second time is a no-op, which is what the idempotence boundary scenario asserts.
    /// </summary>
    public static async Task MigrateAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    /// <summary>Runs arbitrary SQL against a database; used only to build and inspect fixtures.</summary>
    public static async Task<int> ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Counts the rows of one of the three tables this package owns, over a raw connection.</summary>
    public static async Task<long> CountRowsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{table}\"";
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    /// <summary>Reads a single jsonb/text column as a string, over a raw connection.</summary>
    public static async Task<string?> ReadTextAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Builds a context against a real database, outside any HTTP host.</summary>
    public static ChronicleafDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ChronicleafDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ChronicleafDbContext(options);
    }

    private void EnsureRunning()
    {
        if (_database is null)
        {
            throw new InvalidOperationException("The disposable PostgreSQL fixture is not running.");
        }
    }
}
