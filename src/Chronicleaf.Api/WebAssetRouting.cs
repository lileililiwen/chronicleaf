using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Net.Http.Headers;

namespace Chronicleaf.Api;

/// <summary>
/// Static and client routing for the runtime shell (SHELL-001, SHELL-002).
/// </summary>
/// <remarks>
/// Three request classes reach this handler, because existing files under the web root
/// have already been served by the static file middleware and <c>GET /api/health</c> has
/// already been matched by its own endpoint:
/// <list type="number">
/// <item><description><c>/api/*</c> that health does not serve: 404 JSON, never SPA HTML.</description></item>
/// <item><description>an unknown asset path (a final segment with a file extension): 404.</description></item>
/// <item><description>a client route: the SPA entry point, or 503 when no build is present.</description></item>
/// </list>
/// </remarks>
public static class WebAssetRouting
{
    private const string HtmlContentType = "text/html; charset=utf-8";
    private const string JsonContentType = "application/json; charset=utf-8";

    public static Task ServeClientRoute(HttpContext context, WebAssetCatalog catalog)
    {
        var path = context.Request.Path.Value ?? "/";

        if (IsApiPath(path) || LooksLikeAssetPath(path))
        {
            return WriteErrorAsync(context, StatusCodes.Status404NotFound, ApiError.NotFound);
        }

        if (!catalog.IndexAvailable)
        {
            return WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, ApiError.WebAssetsUnavailable);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = HtmlContentType;
        context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
        return context.Response.SendFileAsync(catalog.IndexFilePath);
    }

    /// <summary>True for <c>/api</c> and every path beneath it.</summary>
    internal static bool IsApiPath(string path) =>
        new PathString(path).StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the request names a file rather than a client route. A missing file with
    /// an extension is a 404; a missing extension-less path is the SPA fallback.
    /// </summary>
    internal static bool LooksLikeAssetPath(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        var lastSegment = lastSlash < 0 ? path : path[(lastSlash + 1)..];
        return lastSegment.Contains('.', StringComparison.Ordinal);
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, ApiError error)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = JsonContentType;
        return context.Response.WriteAsJsonAsync(error);
    }
}
