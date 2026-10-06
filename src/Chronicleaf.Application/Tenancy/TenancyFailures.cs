using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Tenancy;

/// <summary>
/// The tenant boundary's envelope codes. They extend the shared envelope rather than replacing
/// it: the body shape stays <c>{code,message,traceId,fieldErrors?}</c> and the messages are
/// authored here, never taken from an OIDC response, a token or a database message.
/// </summary>
public static class TenancyError
{
    /// <summary>An OIDC exchange or a Hosted session request was not accepted (401).</summary>
    public const string OidcLoginFailed = "oidc_login_failed";

    /// <summary>A foreign or missing tenant was addressed; the same answer for both (404).</summary>
    public const string NotFound = "not_found";

    /// <summary>A live membership is required but the caller holds none or a revoked one (403).</summary>
    public const string TenantAccessDenied = "tenant_access_denied";

    /// <summary>An authenticated Hosted session has no active tenant yet (409).</summary>
    public const string TenantSelectionRequired = "tenant_selection_required";

    /// <summary>A Community volume exists here and no explicit import acknowledges it (409).</summary>
    public const string CommunityImportRequired = "community_import_required";

    /// <summary>The account already owns its quota of tenants (422).</summary>
    public const string TenantLimitReached = "tenant_limit_reached";
}

/// <summary>
/// An OIDC login start or callback was not accepted. One generic failure covers a bad state, a
/// bad nonce, a wrong issuer or audience, an expired or mis-signed ID token, an unreachable
/// issuer and an operator token presented in Hosted mode: the caller cannot tell which, and no
/// response carries a token, a code or a provider body.
/// </summary>
public sealed class OidcLoginFailedException()
    : ApiException(TenancyError.OidcLoginFailed, "The Hosted login was not accepted.");

/// <summary>
/// A tenant id names no tenant this caller may see. The message is the same for a foreign
/// tenant and an invented id, so neither response says which ids exist elsewhere.
/// </summary>
public sealed class TenantNotFoundException()
    : ApiException(TenancyError.NotFound, "No such tenant.");

/// <summary>
/// The caller holds no live membership in the tenant, or holds the wrong role for membership
/// administration. A revoked membership and a missing one are the same refusal.
/// </summary>
public sealed class TenantAccessDeniedException()
    : ApiException(TenancyError.TenantAccessDenied, "This tenant is not available to the current user.");

/// <summary>
/// The Hosted session is authenticated but has no active tenant, so no domain scope can be
/// derived for it. Selecting a tenant is an explicit action, never an implicit default.
/// </summary>
public sealed class TenantSelectionRequiredException()
    : ApiException(
        TenancyError.TenantSelectionRequired,
        "No tenant is selected for this session. Select a tenant before acting on tenant data.");

/// <summary>
/// This installation already holds Community domain rows and no explicit import acknowledges
/// them, so tenant actions stop before any reassignment could happen. The remedy is the
/// explicit import action, which records the acknowledgment without moving any Community row.
/// </summary>
public sealed class CommunityImportRequiredException()
    : ApiException(
        TenancyError.CommunityImportRequired,
        "This installation already holds Community data. Record an explicit import before Hosted tenants act on it.");

/// <summary>
/// The account already owns its quota of tenants. Creating another one is refused until an
/// operator quota allows it.
/// </summary>
public sealed class TenantLimitReachedException(int limit)
    : ApiException(
        TenancyError.TenantLimitReached,
        "This account already owns its quota of tenants.")
{
    /// <summary>The quota that refused the creation.</summary>
    public int Limit { get; } = limit;
}
