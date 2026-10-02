using Chronicleaf.Api;
using Chronicleaf.Application.Access;
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
/// <para>
/// This host also supplies the access configuration the shell needs to start at all: a
/// non-Development process refuses to start without a usable access token hash. The value is the
/// SHA-256 hex of a fixture token that no test submits, so the shell assertions can never depend
/// on a login, and the shell routes under test are not private API routes in the first place.
/// </para>
/// </remarks>
internal sealed class ShellTestHost : WebApplicationFactory<Program>
{
    /// <summary>A fixture token no test presents, hashed the way a deployment would hash one.</summary>
    private const string FixtureAccessToken = "chronicleaf-shell-fixture-token-not-a-real-secret";

    private static readonly string FixtureAccessTokenHash = Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(FixtureAccessToken)));

    private readonly string _webRootPath;

    private ShellTestHost(string webRootPath) => _webRootPath = webRootPath;

    public static ShellTestHost WithWebRoot(string webRootPath) => new(webRootPath);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebAssetCatalog.WebRootPathSettingKey, _webRootPath);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, FixtureAccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, "http://127.0.0.1:5081");
        builder.UseEnvironment("Production");
    }
}
