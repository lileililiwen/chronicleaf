using Chronicleaf.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Persistence.Postgres.Testing;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The xUnit collection every persistence test in this assembly shares: one real PostgreSQL
/// container for the whole run, so no test class ever boots its own server.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ChronicleafPostgresCollection : ICollectionFixture<ChronicleafPostgresFixture>
{
    public const string Name = "ChronicleafPostgres";
}

/// <summary>
/// The disposable real PostgreSQL every persistence test in this assembly runs against.
/// </summary>
/// <remarks>
/// A thin adapter over the adopted <see cref="PostgresTestServer"/> platform package: one
/// pinned-image container per assembly, the committed EF migrations applied exactly once into
/// a template database, and one <c>CREATE DATABASE ... TEMPLATE</c> clone per test. Schema
/// application, transaction rollback, constraint enforcement and connection failure are all
/// produced by that server, never by an in-memory provider or a stub. The container is removed
/// when the fixture is disposed, and each test receives its own database on the shared server so
/// row counts are deterministic without cross-test coupling.
/// </remarks>
public sealed class ChronicleafPostgresFixture : IAsyncLifetime
{
    /// <summary>Pinned image tag. Change it deliberately, never to a floating tag.</summary>
    public const string Image = "postgres:17.6-alpine";

    private const string Username = "chronicleaf";
    private const string Password = "chronicleaf-test";
    private const string TemplateDatabase = "chronicleaf_template";

    private PostgresTestServer? _server;

    /// <summary>The adopted shared server, exposed for server-identity assertions.</summary>
    public PostgresTestServer Server =>
        _server ?? throw new InvalidOperationException("The disposable PostgreSQL fixture is not running.");

    /// <summary>True once a real container is running and seeded; asserted so a stub can never pass as one.</summary>
    public bool IsRealPostgres => _server?.IsTemplateSeeded is true;

    public async Task InitializeAsync()
    {
        var server = await PostgresTestServer.StartAsync(
            new PostgresTestServerOptions
            {
                Image = Image,
                Username = Username,
                Password = Password,
                MaintenanceDatabase = "postgres",
            },
            default).ConfigureAwait(false);
        _server = server;

        // The existing committed EF migrations, applied exactly once into the template database.
        // The context is disposed before the delegate returns so later TEMPLATE clones are
        // never blocked by an open connection, which the platform contract requires.
        await server.EnsureTemplateSeededAsync(
            static async (templateConnectionString, ct) =>
            {
                await using var context = CreateContext(templateConnectionString);
                await context.Database.MigrateAsync(ct).ConfigureAwait(false);
            },
            TemplateDatabase,
            default).ConfigureAwait(false);

        // Fail loudly here rather than silently falling back: a missing database is a failed
        // prerequisite, never a passing mock runtime.
        await using var connection = new NpgsqlConnection(server.ServerConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }

    /// <summary>Creates an isolated database on the running server and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        EnsureRunning();

        var name = $"chronicleaf_{purpose}_{Guid.NewGuid():N}"[..40].ToLowerInvariant();
        return await Server.CreateIsolatedDatabaseAsync(name, default).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the committed EF migrations, exactly as the operator command does. Against a
    /// template clone this is a history-table no-op, kept as an idempotent guard; applying it a
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
        if (!IsRealPostgres)
        {
            throw new InvalidOperationException("The disposable PostgreSQL fixture is not running.");
        }
    }
}
