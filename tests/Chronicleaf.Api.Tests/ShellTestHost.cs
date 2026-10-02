using Chronicleaf.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chronicleaf.Api.Tests;

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application against a caller-supplied web root.
/// </summary>
/// <remarks>
/// The web root is redirected through the same configuration key that production uses, so
/// the missing-build and static-routing behaviour under test is the production code path
/// reading a real directory - not a substituted or simulated service.
/// </remarks>
internal sealed class ShellTestHost : WebApplicationFactory<Program>
{
    private readonly string _webRootPath;

    private ShellTestHost(string webRootPath) => _webRootPath = webRootPath;

    public static ShellTestHost WithWebRoot(string webRootPath) => new(webRootPath);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebAssetCatalog.WebRootPathSettingKey, _webRootPath);
    }
}
