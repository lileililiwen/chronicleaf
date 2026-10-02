using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Chronicleaf.Api.Tests;

/// <summary>
/// SHELL-001: <c>GET /api/health</c> is a stable, credential-free liveness contract.
/// </summary>
public sealed class HealthEndpointTests
{
    private static readonly Regex SecretLikeField =
        new("(key|token|secret|password|passwd|credential|connection|version|environment|machine|host|path|user)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public async Task Health_returns_ok_json_for_the_chronicleaf_service()
    {
        using var fixture = new WebRootFixture(withClientBuild: false);
        using var host = ShellTestHost.WithWebRoot(fixture.WebRootPath);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/health", HttpCompletionOption.ResponseContentRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("chronicleaf", root.GetProperty("service").GetString());
    }

    [Fact]
    public async Task Health_exposes_exactly_status_and_service()
    {
        using var fixture = new WebRootFixture(withClientBuild: false);
        using var host = ShellTestHost.WithWebRoot(fixture.WebRootPath);
        using var client = host.CreateClient();

        var body = await client.GetStringAsync("/api/health");

        using var document = JsonDocument.Parse(body);
        var fields = document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();

        Assert.Equal(["service", "status"], fields);
        Assert.DoesNotMatch(SecretLikeField, body);
    }
}
