using System.Text.Json.Serialization;

namespace Chronicleaf.Api;

/// <summary>
/// The complete, credential-free liveness payload of <c>GET /api/health</c> (SHELL-001).
/// Field names are pinned explicitly so the contract cannot drift with serializer policy.
/// </summary>
public sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("service")] string Service)
{
    public static HealthResponse Instance { get; } = new("ok", "chronicleaf");
}

/// <summary>The complete error payload shape for API and web-asset failures.</summary>
public sealed record ApiError([property: JsonPropertyName("code")] string Code)
{
    /// <summary>An API path that this runtime does not serve. Never HTML.</summary>
    public static ApiError NotFound { get; } = new("not_found");

    /// <summary>A client route was requested but no frontend build is present (SHELL-002).</summary>
    public static ApiError WebAssetsUnavailable { get; } = new("web_assets_unavailable");
}
