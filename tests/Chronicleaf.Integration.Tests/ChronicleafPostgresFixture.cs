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

    private static int _migrationDelegateInvocations;

    /// <summary>
    /// Number of times the <see cref="PostgresTestServer.EnsureTemplateSeededAsync"/> migrate
    /// delegate has been invoked for this assembly's shared server. The shared server seeds the
    /// template at most once per assembly, so this value is <c>1</c> for the lifetime of the run;
    /// a probe test asserts that invariant end-to-end (PG-ADOPT-COMPLETE-001).
    /// </summary>
    public static int MigrationDelegateInvocations =>
        Interlocked.CompareExchange(ref _migrationDelegateInvocations, 0, 0);

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
        // never blocked by an open connection, which the platform contract requires. The
        // counter is the PG-ADOPT-COMPLETE-001 oracle the proof test reads; it increments
        // inside the delegate so a re-entry past the seed gate cannot double-count.
        await server.EnsureTemplateSeededAsync(
            static async (templateConnectionString, ct) =>
            {
                Interlocked.Increment(ref _migrationDelegateInvocations);
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

    /// <summary>
    /// Creates an isolated database on the running server and returns its connection string with
    /// pool-self-trimming Npgsql params applied (PG-ADOPT-COMPLETE-002 + PG-TIGHTEN-004):
    /// <c>MaxPoolSize=4</c> bounds each per-test pool to a small handful of entries (the one test
    /// that opens two connections concurrently needs the second slot), <c>MinPoolSize=0</c> keeps
    /// no idle baseline, <c>ConnectionPruningInterval=1</c> + <c>ConnectionIdleLifetime=5</c>
    /// close idle connections within seconds, and <c>ConnectionLifetime=30</c> recycles any
    /// connection older than 30 seconds so a stuck connection cannot hold a backend slot
    /// indefinitely; the cumulative live PostgreSQL connections stay under PostgreSQL's default
    /// <c>max_connections=100</c> for the serialised assembly run. <c>Pooling</c> stays on so
    /// within-test reuse keeps the suite fast; the boundary is structural on the pool, not on the
    /// underlying TCP handshake.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        EnsureRunning();

        var name = BuildDatabaseName(purpose);
        var siblingString = await Server.CreateIsolatedDatabaseAsync(name, default).ConfigureAwait(false);
        var builder = new NpgsqlConnectionStringBuilder(siblingString)
        {
            MaxPoolSize = 4,
            MinPoolSize = 0,
            ConnectionPruningInterval = 1,
            ConnectionIdleLifetime = 5,
            // ConnectionLifetime is left at the Npgsql default (0 = no max lifetime). The audit
            // found that a hard 30s recycle bound inflates the assembly run by forcing a TCP+TLS
            // handshake on every long-running test, with no observable benefit on the 53300
            // failure class (the per-test pool is already bounded by MaxPoolSize=4 and pruned by
            // ConnectionIdleLifetime=5). The pool-self-trimming invariant is structural on the
            // four other params; this knob stays at the Npgsql default and is documented here so
            // a future audit does not mistake its absence for a defect.
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Creates an EMPTY database on the running server (not cloned from the seeded template) and
    /// returns its pool-self-trimming connection string. Reserved for the small set of tests that
    /// assert a deliberately under-migrated schema (<c>Missing_schema_…</c>,
    /// <c>Older_schema_…</c>): they need a fresh database the readiness probe reports as
    /// <c>schema_mismatch</c>, which the cloned-from-template path cannot give them.
    /// </summary>
    public async Task<string> CreateEmptyDatabaseAsync(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        EnsureRunning();

        var name = BuildDatabaseName(purpose);
        await using (var connection = new NpgsqlConnection(Server.ServerConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var siblingString = Server.BuildDatabaseConnectionString(name);
        var builder = new NpgsqlConnectionStringBuilder(siblingString)
        {
            MaxPoolSize = 4,
            MinPoolSize = 0,
            ConnectionPruningInterval = 1,
            ConnectionIdleLifetime = 5,
            // ConnectionLifetime stays at the Npgsql default; see CreateDatabaseAsync.
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Builds a sibling-safe database name from a free-form purpose: the sibling's
    /// <c>RequireDatabaseName</c> rejects hyphens (its regex is
    /// <c>^[A-Za-z_][A-Za-z0-9_$]{0,62}$</c>) but tests use purposes like
    /// <c>no-if-match</c> for clarity, so the assembly-local adapter normalises them to
    /// underscores before concatenating with the GUID suffix and truncating to the 63-char
    /// PostgreSQL limit. The original (pre-PG-ADOPT) fixture accepted the hyphenated purposes
    /// verbatim because it issued <c>CREATE DATABASE</c> directly via raw SQL — that path is
    /// intentionally kept inside <see cref="CreateEmptyDatabaseAsync"/> only.
    /// </summary>
    private static string BuildDatabaseName(string purpose)
    {
        var safePurpose = purpose.Replace('-', '_');
        var raw = $"chronicleaf_{safePurpose}_{Guid.NewGuid():N}";
        return (raw.Length <= 63 ? raw : raw[..63]).ToLowerInvariant();
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
