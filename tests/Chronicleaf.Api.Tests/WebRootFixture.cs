namespace Chronicleaf.Api.Tests;

/// <summary>
/// Creates and disposes the real directories the shell reads its frontend build from.
/// </summary>
internal sealed class WebRootFixture : IDisposable
{
    public WebRootFixture(bool withClientBuild)
    {
        WebRootPath = Path.Combine(Path.GetTempPath(), "chronicleaf-shell-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(WebRootPath);

        if (withClientBuild)
        {
            WriteFile(WebAssetCatalog.IndexFileName, BuildFixture.ShellIndexHtml);
            WriteFile(Path.Combine("assets", "shell-fixture.js"), "// fixture client bundle for static-routing assertions.\n");
        }
    }

    public string WebRootPath { get; }

    public void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(WebRootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(WebRootPath))
        {
            Directory.Delete(WebRootPath, recursive: true);
        }
    }
}

/// <summary>Fixture payload shared by the hosting tests.</summary>
internal static class BuildFixture
{
    public const string ShellIndexHtml =
        """
        <!doctype html>
        <html lang="en">
          <head><meta charset="utf-8" /><title>Chronicleaf</title></head>
          <body><div id="root"></div><script type="module" src="/assets/shell-fixture.js"></script></body>
        </html>
        """;
}
