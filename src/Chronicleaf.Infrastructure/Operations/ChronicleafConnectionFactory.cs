using Hangfire.PostgreSql;
using Npgsql;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// The engine's connection source, owned by this application rather than by the engine.
/// </summary>
/// <remarks>
/// <para>
/// The engine's own factory builds an <see cref="NpgsqlDataSource"/> and never disposes it, because
/// the engine's own hosting model assumes the process ends when the storage does. In this application
/// the storage is a singleton inside a container that is rebuilt - by a test host, by a deployment
/// restart - and an undisposed data source keeps its pooled connections open for the life of the
/// process. Against one shared PostgreSQL server that is a slow connection leak, and it was measured
/// as one: a run of hosts exhausted the server's own connection budget before it finished.
/// </para>
/// <para>
/// Implementing the engine's one-method factory interface is what fixes it. The data source is built
/// here, bounded, and disposed when the container is disposed - so a host that goes away takes its
/// connections with it, which is the same courtesy every other resource in this application extends.
/// </para>
/// <para>
/// The pool is capped for the same reason from the other side: the queue opens a connection per poll
/// and holds each one only for the length of one statement, so a large pool buys nothing and spends
/// a shared server's connection budget on connections that are never busy. An operator who configured
/// a pool size keeps their own value.
/// </para>
/// </remarks>
public sealed class ChronicleafConnectionFactory : IConnectionFactory, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public ChronicleafConnectionFactory(string connectionString, int maximumPoolSize)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        _dataSource = new NpgsqlDataSourceBuilder(WithMaximumPoolSize(connectionString, maximumPoolSize))
            .Build();
    }

    /// <inheritdoc />
    public NpgsqlConnection GetOrCreateConnection() => _dataSource.CreateConnection();

    /// <summary>Closes the pool and releases every connection it still holds.</summary>
    public void Dispose() => _dataSource.Dispose();

    private static string WithMaximumPoolSize(string connectionString, int maximumPoolSize)
    {
        if (connectionString.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        var separator = connectionString.Contains(';') && !connectionString.EndsWith(';') ? ";" : string.Empty;

        return $"{connectionString}{separator}Maximum Pool Size={maximumPoolSize}";
    }
}
