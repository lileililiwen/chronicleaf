using System.Net;
using System.Text.Json;
using Xunit;

namespace Chronicleaf.Api.Tests;

/// <summary>
/// SHELL-001: unknown API routes are JSON 404s and never leak SPA HTML.
/// </summary>
public sealed class UnknownApiRouteTests
{
    public static TheoryData<string> UnknownApiPaths => new()
    {
        "/api/unknown",
        "/api/v1/does-not-exist",
    };

    [Theory]
    [MemberData(nameof(UnknownApiPaths))]
    public async Task Unknown_api_path_returns_json_not_found_instead_of_html(string path)
    {
        using var fixture = new WebRootFixture(withClientBuild: true);
        using var host = ShellTestHost.WithWebRoot(fixture.WebRootPath);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<!doctype", body, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(body);
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_api_path_stays_json_even_when_no_client_build_exists()
    {
        using var fixture = new WebRootFixture(withClientBuild: false);
        using var host = ShellTestHost.WithWebRoot(fixture.WebRootPath);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/unknown");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }
}
