using System.Collections.Concurrent;
using System.Security.Cryptography;
using Xunit;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application against a real disposable PostgreSQL, a real
/// temporary key file and the real operation queue (CREDENTIAL-VAULT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The key is a real file outside the repository, outside the published application and outside any
/// image: a temporary file whose 32 bytes are generated per scenario, passed in through the
/// production configuration key <c>Chronicleaf:EncryptionKeyFile</c> and deleted when the scenario
/// ends. Nothing here generates a key into a database, a fixture directory or a container layer, and
/// no key is ever printed: the scenarios read the file back to prove a rotation, and the assertions
/// about the file are about the bytes the cipher produced, not about the key's value.
/// </para>
/// <para>
/// The host is the delivered <c>WebApplicationFactory</c> pattern. Substituted services are the clock
/// and the credential identity source, and both are replaced through their production seams. The
/// store, the vault, the cipher, the key file reader, the routes, the access boundary, the Hangfire
/// storage and the worker are all the production ones - there is no in-memory queue, no in-memory
/// database and no substitute cipher anywhere in these scenarios, because the claim under test is
/// that a secret is absent from a real row and cannot be opened without a real key.
/// </para>
/// <para>
/// The automated dispatch loop is switched off and the worker is run by hand, for the same reason
/// the delivered background-jobs fixture does it: a job that has to race a deletion has to be placed
/// on a chosen side of the dispatch seam.
/// </para>
/// </remarks>
public sealed class CredentialVaultApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string? _keyFile;
    private readonly ICredentialIdSource? _ids;
    private readonly LogCapture _logs;

    private CredentialVaultApiHost(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        ICredentialIdSource? ids,
        LogCapture logs)
    {
        _connectionString = connectionString;
        _clock = clock;
        _keyFile = keyFile;
        _ids = ids;
        _logs = logs;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>Every log line this host has written, formatted the way a deployed host would write it.</summary>
    public LogCapture Logs => _logs;

    /// <summary>
    /// Starts a host whose key file is <paramref name="keyFile"/>. A null path starts a host with no
    /// key configured at all, which is the fail-closed scenario's own condition.
    /// </summary>
    public static CredentialVaultApiHost Start(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        ICredentialIdSource? ids = null)
    {
        var logs = new LogCapture();

        return new CredentialVaultApiHost(
            connectionString,
            clock,
            keyFile,
            ids,
            logs);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        if (_keyFile is not null)
        {
            builder.UseSetting(SecretVaultSettings.EncryptionKeyFileSettingName, _keyFile);
        }

        // The dispatch loop runs only when a test asks it to, so a job can be placed exactly on either
        // side of "the owner deleted the credential".
        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:SchedulePollingInterval", "00:00:00.200");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:HeartbeatInterval", "00:00:05");

        builder.ConfigureLogging(logging =>
        {
            // Trace, so the assertion about log lines covers the debug line the key reader writes as
            // well as every warning a refused vault produces. A default Information host would not
            // have written that line at all, and "it is not logged because it is not enabled" is not
            // the property under test.
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(_logs);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);

            if (_ids is not null)
            {
                services.RemoveAll<ICredentialIdSource>();
                services.AddSingleton(_ids);
            }
        });
    }

    /// <summary>Runs one real outbox dispatch pass, the loop's own body, by hand.</summary>
    public async Task<IReadOnlyList<OutboxDispatchResult>> DispatchAsync(CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        var stamper = scope.ServiceProvider.GetRequiredService<IOutboxDispatchStamper>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var results = await dispatcher.DispatchPendingAsync(100, cancellationToken);

        foreach (var result in results.Where(result => result.Outcome == OutboxDispatchOutcome.Handled))
        {
            await stamper.TryMarkDispatchedAsync(result.EventId, clock.GetUtcNow(), cancellationToken);
        }

        return results;
    }

    /// <summary>
    /// Opens a scope whose <see cref="OperationWorker"/> is bound to its own context, and keeps that
    /// scope alive until the handle is disposed.
    /// </summary>
    public WorkerHandle OpenWorker() => new(Services.CreateScope());

    /// <summary>A live scope and the worker it resolved.</summary>
    public sealed class WorkerHandle : IDisposable
    {
        private readonly IServiceScope _scope;

        internal WorkerHandle(IServiceScope scope)
        {
            _scope = scope;
            Worker = scope.ServiceProvider.GetRequiredService<OperationWorker>();
        }

        public OperationWorker Worker { get; }

        public void Dispose() => _scope.Dispose();
    }
}

/// <summary>
/// Every log line a host writes, kept so a scenario can read what a failure would have left behind.
/// </summary>
/// <remarks>
/// The formatted message and the exception text are both captured, because a secret or a path would
/// most likely reach a log through the exception attached to a failed operation rather than through a
/// message the application chose. Scoping the capture to this host keeps a concurrent test's lines
/// out of the assertion.
/// </remarks>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>Every line, in the order it was written.</summary>
    public IReadOnlyList<string> Lines => [.. _lines];

    /// <summary>All lines joined, which is what a grep of a log file would see.</summary>
    public string Text => string.Join('\n', _lines);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_lines);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            lines.Enqueue(
                $"{logLevel} {eventId.Id} {eventId.Name}: {formatter(state, exception)}" +
                (exception is null ? string.Empty : $" | {exception}"));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

/// <summary>
/// A fixed sequence of credential ids, so a scenario can name the row, the reference and the key it
/// expects without reading back whatever the process happened to produce.
/// </summary>
public sealed class SequenceCredentialIdSource(params Guid[] ids) : ICredentialIdSource
{
    private int _next = -1;

    public Guid NextCredentialId()
    {
        var index = Interlocked.Increment(ref _next);

        if (index >= ids.Length)
        {
            throw new InvalidOperationException(
                "The test declared fewer credential ids than it stored. Declare one id per save.");
        }

        return ids[index];
    }
}

/// <summary>
/// A real operator key file, in a real temporary directory, deleted when the scenario ends.
/// </summary>
/// <remarks>
/// The bytes come from the operating system's generator, the file is written with owner-only
/// permissions, and the object is disposable so no scenario can leave a key behind for another one to
/// find. The path lives in /tmp, which is where an operator's runtime key would be mounted from and
/// never where a repository file belongs.
/// </remarks>
public sealed class OperatorKeyFile : IDisposable
{
    private OperatorKeyFile(string path, byte[] key)
    {
        FilePath = path;
        Key = key;
    }

    /// <summary>The absolute path an operator would configure, and which must never appear in a log.</summary>
    public string FilePath { get; }

    /// <summary>
    /// The key's own bytes, so a scenario can prove which key sealed a row. Held only for the life of
    /// the scenario and never written anywhere else.
    /// </summary>
    public byte[] Key { get; private set; }

    /// <summary>Writes a new key file under a fresh temporary directory.</summary>
    public static OperatorKeyFile Create()
    {
        var directory = Directory.CreateTempSubdirectory("chronicleaf-credential-vault-");
        var path = Path.Combine(directory.FullName, "encryption.key");
        var key = RandomNumberGenerator.GetBytes(SecretVaultSettings.KeySizeBytes);

        File.WriteAllBytes(path, key);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new OperatorKeyFile(path, key);
    }

    /// <summary>Overwrites the file with different bytes, which is how a replaced key is expressed.</summary>
    public void ReplaceKey()
    {
        var replacement = RandomNumberGenerator.GetBytes(SecretVaultSettings.KeySizeBytes);
        File.WriteAllBytes(FilePath, replacement);
        Key = replacement;
    }

    /// <summary>Overwrites the file with a length that is not a key, which is the wrong-length fault.</summary>
    public void WriteWrongLength()
    {
        File.WriteAllBytes(FilePath, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>Removes the file, which is the missing-key fault.</summary>
    public void Remove() => File.Delete(FilePath);

    /// <summary>
    /// Makes the file unreadable to this process, which is the unreadable-key fault. Only a readable
    /// path can be made unreadable, so a scenario that calls this on an absent file is saying
    /// something else than it means.
    /// </summary>
    public void MakeUnreadable()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(FilePath, UnixFileMode.None);
        }
    }

    /// <summary>Makes the file readable again, so a scenario can prove a restore fixes the vault.</summary>
    public void MakeReadable()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(System.IO.Path.GetDirectoryName(FilePath)!, recursive: true);
        }
        catch (IOException)
        {
            // A key file that outlives its scenario on a machine that is being torn down is not a test
            // failure, and the directory is under the operating system's own temporary root.
        }
    }
}

/// <summary>Raw reads of the credential table, for the assertions about stored state.</summary>
public static class CredentialRows
{
    /// <summary>One row exactly as PostgreSQL holds it, ciphertext and nonce included.</summary>
    public sealed record Row(
        Guid Id,
        Guid ScopeId,
        string Provider,
        byte[] Ciphertext,
        byte[] Nonce,
        int KeyVersion,
        DateTimeOffset UpdatedAt,
        long Revision);

    /// <summary>Reads one row by id, sealed bytes and all.</summary>
    public static async Task<Row?> ReadAsync(string connectionString, Guid credentialId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, scope_id, provider, ciphertext, nonce, key_version, updated_at, revision FROM credentials WHERE id = @id";
        command.Parameters.AddWithValue("id", credentialId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new Row(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            (byte[])reader.GetValue(3),
            (byte[])reader.GetValue(4),
            reader.GetInt32(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetInt64(7));
    }

    /// <summary>Reads the one row this scope has for a provider.</summary>
    public static async Task<Row?> ReadByProviderAsync(string connectionString, Guid scopeId, string provider)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, scope_id, provider, ciphertext, nonce, key_version, updated_at, revision FROM credentials WHERE scope_id = @scope AND provider = @provider";
        command.Parameters.AddWithValue("scope", scopeId);
        command.Parameters.AddWithValue("provider", provider);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new Row(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            (byte[])reader.GetValue(3),
            (byte[])reader.GetValue(4),
            reader.GetInt32(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetInt64(7));
    }

    /// <summary>Rewrites a row's scope, so a scenario can move a sealed row and watch it fail to open.</summary>
    public static async Task MoveToScopeAsync(string connectionString, Guid credentialId, Guid scopeId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE credentials SET scope_id = @scope WHERE id = @id";
        command.Parameters.AddWithValue("scope", scopeId);
        command.Parameters.AddWithValue("id", credentialId);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>Rewrites a row's provider, so a scenario can move it between providers.</summary>
    public static async Task MoveToProviderAsync(string connectionString, Guid credentialId, string provider)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE credentials SET provider = @provider WHERE id = @id";
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("id", credentialId);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>
    /// Rewrites a row's key version, so a scenario can present a ciphertext as one sealed under an
    /// older key version than it claims.
    /// </summary>
    public static async Task SetKeyVersionAsync(string connectionString, Guid credentialId, int keyVersion)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE credentials SET key_version = @version WHERE id = @id";
        command.Parameters.AddWithValue("version", keyVersion);
        command.Parameters.AddWithValue("id", credentialId);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>Every value column of the table, as text, so a whole-table sweep is possible.</summary>
    public static async Task<string> DumpAllTextAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id::text || '|' || scope_id::text || '|' || provider || '|' || encode(ciphertext, 'hex') || '|' || encode(nonce, 'hex') || '|' || key_version::text || '|' || updated_at::text || '|' || revision::text FROM credentials";

        await using var reader = await command.ExecuteReaderAsync();
        var text = new System.Text.StringBuilder();

        while (await reader.ReadAsync())
        {
            text.AppendLine(reader.GetString(0));
        }

        return text.ToString();
    }

    /// <summary>How many rows the table holds, for the assertions that nothing was written.</summary>
    public static async Task<long> CountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM credentials";

        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>True when any stored value or byte sequence of this row contains the given bytes.</summary>
    public static bool Contains(Row row, byte[] needle) =>
        Contains(row.Ciphertext, needle) || Contains(row.Nonce, needle);

    /// <summary>
    /// True when <paramref name="needle"/> appears in <paramref name="haystack"/> anywhere, not only
    /// at a boundary. A secret is not required to be aligned inside a ciphertext, and a search that
    /// only looked at aligned positions would miss a leak that a shorter key or a longer value
    /// produced.
    /// </summary>
    public static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        for (var offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            var matched = true;

            for (var index = 0; index < needle.Length; index++)
            {
                if (haystack[offset + index] == needle[index])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
