using System.Net;
using System.Text.Json;
using Xunit;

namespace Chronicleaf.Api.Tests;

/// <summary>
/// SHELL-002 missing-build boundary: a client route requested with no frontend build is a
/// JSON 503, and liveness is unaffected.
/// </summary>
public sealed class MissingWebBuildTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/projects")]
    public async Task Client_route_without_a_client_build_returns_503_json(string path)
    {
        using var fixture = new WebRootFixture(withClientBuild: false);
        using var host = ShellTestHost.WithWebRoot(fixture.WebRootPath);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<", body, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(body);
        Assert.Equal("web_assets_unavailable", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Health_still_returns_200_while_the_client_build_is_missing()
    {
        using var fixture = new WebRootFixture(withClientBuild: false);
        using var host = ShellTestHost.WithWebRoot(fixture.WebRootPath);
        using var client = host.CreateClient();

        using var shellResponse = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, shellResponse.StatusCode);

        using var healthResponse = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.Equal("application/json", healthResponse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Removing_the_build_after_start_up_is_observed_on_the_next_request()
    {
        using var fixture = new WebRootFixture(withClientBuild: true);
        using var host = ShellTestHost.WithWebRoot(fixture.WebRootPath);
        using var client = host.CreateClient();

        using var built = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, built.StatusCode);

        File.Delete(Path.Combine(fixture.WebRootPath, WebAssetCatalog.IndexFileName));

        using var missing = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);

        using var document = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        Assert.Equal("web_assets_unavailable", document.RootElement.GetProperty("code").GetString());
    }
}
