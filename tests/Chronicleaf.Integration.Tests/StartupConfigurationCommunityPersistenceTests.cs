using Chronicleaf.Application.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Connection configuration: the value is required, an unusable one fails start-up with a
/// redacted message, and an absent one never becomes a working in-memory store.
/// </summary>
public sealed class StartupConfigurationCommunityPersistenceTests
{
    private const string Secret = "not-a-connection-string-with-a-secret";

    [Fact]
    public void Unusable_connection_string_fails_startup_without_echoing_it()
    {
        var failure = Assert.ThrowsAny<Exception>(() =>
        {
            using var host = ChronicleafApiHost.Start(Secret, DateTimeOffset.UnixEpoch);
            _ = host.Services;
        });

        var messages = Flatten(failure);
        Assert.Contains(
            messages,
            message => message.Contains(ChronicleafConnection.SettingName, StringComparison.Ordinal));

        // The value itself, and anything resembling one, must not reach the failure message.
        foreach (var message in messages)
        {
            Assert.DoesNotContain(Secret, message, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unconfigured_database_is_never_reported_ready_and_health_still_answers()
    {
        // No connection string at all: the process starts, liveness stays honest, and readiness
        // refuses to call a missing database ready.
        await using var host = ChronicleafApiHost.Start(string.Empty, DateTimeOffset.UnixEpoch);
        using var client = host.CreateClient();

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(200, (int)health.StatusCode);

        using var ready = await client.GetAsync("/api/ready");
        Assert.Equal(503, (int)ready.StatusCode);

        var body = await ready.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"database_unavailable\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("chronicleaf-unconfigured.invalid", body, StringComparison.Ordinal);
    }

    private static string[] Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return messages.ToArray();
    }
}
