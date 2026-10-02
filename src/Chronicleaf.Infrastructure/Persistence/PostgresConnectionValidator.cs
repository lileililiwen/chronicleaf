using Chronicleaf.Application.Persistence;
using Npgsql;

namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// Decides whether a configured connection string can be used, without ever revealing it.
/// </summary>
public static class PostgresConnectionValidator
{
    /// <summary>
    /// Throws a redacted start-up failure when a configured value cannot reach PostgreSQL.
    /// An absent value is not an error here: the process starts without a database and reports
    /// <c>database_unavailable</c> at <c>GET /api/ready</c>, which is what keeps liveness honest
    /// while never substituting a fake store.
    /// </summary>
    public static void EnsureUsable(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        if (TryDescribe(connectionString, out _, out _, out var failure))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Configuration error: '{ChronicleafConnection.SettingName}' is not a usable PostgreSQL connection string ({failure}). " +
            "The configured value is never logged. Set a valid PostgreSQL connection string for the Chronicleaf database.");
    }

    /// <summary>
    /// Parses the connection string. Only non-secret description fields leave this method; the
    /// configured value itself is never returned, logged or embedded in a message.
    /// </summary>
    internal static bool TryDescribe(
        string connectionString,
        out string? host,
        out string? database,
        out string failure)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            host = null;
            database = null;
            failure = "the value is not in keyword=value form";
            return false;
        }

        host = builder.Host;
        database = builder.Database;

        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            failure = "no host is configured";
            return false;
        }

        failure = string.Empty;
        return true;
    }
}
