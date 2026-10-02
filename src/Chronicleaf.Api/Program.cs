// Chronicleaf runtime shell (SHELL-001/SHELL-002/SHELL-003 in
// openspec/specs/runtime-shell/spec.md): a credential-free API liveness contract plus
// static hosting of the built React client from this repository's own build output.
using Chronicleaf.Api;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// The built React client lives in the API's wwwroot after the explicit publish step
// (scripts/publish.sh, also used by the Dockerfile). The root is resolved once at start-up
// and every routing decision below is a real filesystem lookup against it.
var webAssets = WebAssetCatalog.Resolve(builder.Configuration, builder.Environment);

var app = builder.Build();

// The static file middleware runs before endpoint routing on purpose: in .NET 10 it declines
// any request that routing has already matched, and the catch-all client route below matches
// every path that is not an existing file. Existing files are therefore served here, and
// everything else falls through to the endpoints.
if (Directory.Exists(webAssets.WebRootPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(webAssets.WebRootPath),
    });
}

app.UseRouting();

// SHELL-001: liveness only. No database, provider, version or environment detail is exposed.
app.MapGet("/api/health", () => Results.Json(HealthResponse.Instance));

// SHELL-001/SHELL-002: every unmatched path is either an unknown API path, an unknown
// static asset, or a client route. Only the last one falls back to the SPA entry point.
app.MapGet("/", (HttpContext context) => WebAssetRouting.ServeClientRoute(context, webAssets));
app.MapFallback("{**clientRoute}", (HttpContext context) => WebAssetRouting.ServeClientRoute(context, webAssets));

app.Run();

// Exposed so tests/Chronicleaf.Api.Tests can host this application with
// WebApplicationFactory<Program>.
public partial class Program
{
}
