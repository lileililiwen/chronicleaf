using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Application.Tenancy;

/// <summary>
/// Which deployment this process is: single-user Community or multi-tenant Hosted
/// (CLOUD-TENANT-BOUNDARY-001/002).
/// </summary>
/// <remarks>
/// A real input to the tenancy decisions, in the same shape as the delivered capture and
/// local-git deployment modes: in <see cref="Community"/> every private route resolves the
/// installation scope exactly as before and no Hosted identity exists, while in
/// <see cref="Hosted"/> the operator token path is closed and only verified site-local
/// end-user sessions with an explicit active tenant resolve a scope.
/// </remarks>
public enum TenancyMode
{
    /// <summary>A single-user Community install. Hosted identity does not exist here.</summary>
    Community = 0,

    /// <summary>The shared Hosted deployment. Tenants isolate every scoped row.</summary>
    Hosted = 1,
}

/// <summary>
/// The operator configuration the tenant boundary reads, resolved once at start-up.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <c>Chronicleaf__Tenancy__Mode</c> - <c>Community</c> (the default) or <c>Hosted</c>. An
/// unrecognized value fails closed to Community parsing refusal at start-up rather than to a
/// half-tenant deployment.
/// </description></item>
/// <item><description>
/// <c>Chronicleaf__Tenancy__OidcIssuer</c> - the site-specific OIDC issuer URL, required in
/// Hosted. There is no default and no shared issuer: a Hosted deployment without its own
/// issuer refuses to start.
/// </description></item>
/// <item><description>
/// <c>Chronicleaf__Tenancy__OidcClientId</c> - this site's client id at that issuer, required
/// in Hosted for the audience check.
/// </description></item>
/// <item><description>
/// <c>Chronicleaf__Tenancy__MaxTenantsPerUser</c> - how many tenants one account may own before
/// an operator quota exists. The default is 1.
/// </description></item>
/// </list>
/// </remarks>
public sealed record TenancySettings(
    TenancyMode Mode,
    string? OidcIssuer,
    string? OidcClientId,
    int MaxTenantsPerUser)
{
    /// <summary>Configuration section; the environment variable form is <c>Chronicleaf__Tenancy__*</c>.</summary>
    public const string SectionName = "Chronicleaf:Tenancy";

    /// <summary>How many tenants one account may own until an operator quota is configured.</summary>
    public const int DefaultMaxTenantsPerUser = 1;

    /// <summary>True when this process serves Hosted tenants rather than a Community owner.</summary>
    public bool IsHosted => Mode == TenancyMode.Hosted;

    /// <summary>
    /// The configured issuer, normalized to no trailing slash, or <c>null</c> when none was
    /// configured. Comparison against the ID token's <c>iss</c> is exact after this
    /// normalization, so a stored trailing slash can neither widen nor break the match.
    /// </summary>
    public string? NormalizedIssuer => NormalizeIssuer(OidcIssuer);

    public static TenancySettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        return new TenancySettings(
            ReadMode(section["Mode"]),
            Trimmed(section["OidcIssuer"]),
            Trimmed(section["OidcClientId"]),
            ReadMaxTenants(section["MaxTenantsPerUser"]));
    }

    /// <summary>
    /// Fails start-up when Hosted is configured without the identity it cannot work without. The
    /// message names the settings and never a configured value.
    /// </summary>
    public static void EnsureUsable(TenancySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.IsHosted)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.OidcIssuer)
            || !Uri.TryCreate(settings.OidcIssuer.Trim(), UriKind.Absolute, out var issuer)
            || (issuer.Scheme != Uri.UriSchemeHttp && issuer.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Configuration error: '{SectionName}:OidcIssuer' is required in Hosted mode and must " +
                "be an absolute HTTP(S) URL of this site's OIDC issuer. The configured value is never logged.");
        }

        if (string.IsNullOrWhiteSpace(settings.OidcClientId))
        {
            throw new InvalidOperationException(
                $"Configuration error: '{SectionName}:OidcClientId' is required in Hosted mode and must " +
                "be this site's client id at its OIDC issuer. The configured value is never logged.");
        }
    }

    private static TenancyMode ReadMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TenancyMode.Community;
        }

        if (Enum.TryParse<TenancyMode>(value.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Configuration error: '{SectionName}:Mode' must be 'Community' or 'Hosted'. " +
            "The configured value is never logged.");
    }

    private static int ReadMaxTenants(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultMaxTenantsPerUser;
        }

        if (int.TryParse(value.Trim(), out var parsed) && parsed >= 1)
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Configuration error: '{SectionName}:MaxTenantsPerUser' must be a positive integer. " +
            "The configured value is never logged.");
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeIssuer(string? value)
    {
        var trimmed = Trimmed(value);
        return trimmed is null ? null : trimmed.TrimEnd('/');
    }
}
