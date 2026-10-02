namespace Chronicleaf.Api;

/// <summary>
/// Resolves and describes the directory that holds the built React client.
/// </summary>
/// <remarks>
/// This is the single real mechanism behind the missing-build behaviour required by
/// SHELL-002: the directory is resolved from configuration or the ASP.NET Core web root,
/// and <see cref="IndexAvailable"/> is a live filesystem check performed per request.
/// Nothing about the missing-build response is simulated, in production or in tests.
/// </remarks>
public sealed class WebAssetCatalog
{
    /// <summary>
    /// Configuration key that overrides the web root. Production uses the default
    /// (<c>wwwroot</c> of the API project); it exists so a publish layout, a container
    /// mount or a test can point the host at a different asset directory.
    /// </summary>
    public const string WebRootPathSettingKey = "Chronicleaf:WebRootPath";

    /// <summary>The SPA entry point file that a client build must produce.</summary>
    public const string IndexFileName = "index.html";

    private WebAssetCatalog(string webRootPath) => WebRootPath = webRootPath;

    /// <summary>Absolute path of the directory that contains the built client.</summary>
    public string WebRootPath { get; }

    /// <summary>Absolute path of the SPA entry point inside <see cref="WebRootPath"/>.</summary>
    public string IndexFilePath => Path.Combine(WebRootPath, IndexFileName);

    /// <summary>
    /// True when a client build is present. Checked per request so that a build appearing
    /// or disappearing after start-up is observed exactly as production observes it.
    /// </summary>
    public bool IndexAvailable => File.Exists(IndexFilePath);

    /// <summary>Resolves the web root from the <see cref="WebRootPathSettingKey"/> setting, the web root, or <c>wwwroot</c>.</summary>
    public static WebAssetCatalog Resolve(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration[WebRootPathSettingKey];
        var webRoot = !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");

        return new WebAssetCatalog(Path.GetFullPath(webRoot));
    }
}
