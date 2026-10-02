using System.Net;
using System.Text.Json;
using Xunit;

namespace Chronicleaf.Api.Tests;

/// <summary>
/// SHELL-002: client routes fall back to the built SPA entry point, and unknown asset
/// paths are 404s rather than the fallback.
/// </summary>
public sealed class WebAssetHostingTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/projects")]
    [InlineData("/projects/some-client-side-route")]
    public async Task Client_route_serves_the_built_index_html(string path)
    {
        using var fixture = new WebRootFixture(withClientBuild: true);
        using var host = ShellTestHost.WithWebRoot(fixture.WebRootPath);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<div id=\"root\"></div>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_hashed_client_asset_is_served_from_the_web_root()
    {
        using var fixture = new WebRootFixture(withClientBuild: true);
        using var host = ShellTestHost.WithWebRoot(fixture.WebRootPath);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/assets/shell-fixture.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("fixture client bundle", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/assets/missing-A1b2C3d4.js")]
    [InlineData("/favicon-does-not-exist.ico")]
    public async Task Unknown_static_asset_path_is_404_and_not_the_spa_fallback(string path)
    {
        using var fixture = new WebRootFixture(withClientBuild: true);
        using var host = ShellTestHost.WithWebRoot(fixture.WebRootPath);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<div id=\"root\"></div>", body, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(body);
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
    }
}
