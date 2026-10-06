using Chronicleaf.Application.Access;
using Chronicleaf.Core.Access;

namespace Chronicleaf.Application.Tenancy;

/// <summary>
/// The tenant boundary's contract: verified site-local Hosted identity and the explicit tenant
/// scope every private request acts under (CLOUD-TENANT-BOUNDARY-001/002).
/// </summary>
/// <remarks>
/// Everything a caller supplies is an id or an OIDC artifact; a tenant scope is always derived
/// server-side from the validated session's active tenant, never from a request body. The
/// Community owner path is untouched: with no Hosted configuration every method that needs a
/// Hosted session refuses, and scope resolution keeps returning the installation scope.
/// </remarks>
public interface ITenancyService
{
    /// <summary>
    /// Begins a verified login: stores the state/nonce challenge and returns the authorization
    /// URL the browser must visit. Public: it authenticates nobody and discloses nothing.
    /// </summary>
    Task<HostedLoginStart> StartLoginAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a login: validates the state, exchanges the code at the configured issuer,
    /// validates the ID token's issuer, audience, lifetime, nonce and signature, provisions the
    /// user and issues a local session. Every failure is the same generic refusal.
    /// </summary>
    /// <returns>The plaintext session token, which exists only in this result.</returns>
    Task<CreatedHostedSession> CompleteLoginAsync(
        string? code,
        string? state,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the caller's live tenant memberships, newest first.</summary>
    Task<IReadOnlyList<TenantMembershipView>> ListTenantsAsync(
        OwnerSessionContext session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a tenant the caller owns, with its fresh scope row, and selects it on the
    /// session. Refused past the account quota, and refused on a Community volume until the
    /// explicit import is recorded.
    /// </summary>
    Task<TenantView> CreateTenantAsync(
        OwnerSessionContext session,
        string? name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Selects one of the caller's live tenants onto the session. A foreign or invented id is
    /// the same 404; a revoked membership is denied.
    /// </summary>
    Task<TenantView> SelectTenantAsync(
        OwnerSessionContext session,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes one user's membership in a tenant the caller owns, and revokes the sessions that
    /// acted under it. Only a tenant owner may revoke, and an owner cannot revoke themselves.
    /// </summary>
    Task RevokeMembershipAsync(
        OwnerSessionContext session,
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the explicit acknowledgment that this installation already holds Community data.
    /// It moves no row: the Community scope stays unreachable to tenants.
    /// </summary>
    Task RecordImportAsync(
        OwnerSessionContext session,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the domain scope id for a validated session: the active tenant's scope in
    /// Hosted, or <c>null</c> when the session selects no tenant. Community sessions never reach
    /// this service.
    /// </summary>
    Task<Guid?> ResolveTenantScopeAsync(
        OwnerSessionContext session,
        CancellationToken cancellationToken = default);
}

/// <summary>A pending login: where the browser must go and the state it must bring back.</summary>
public sealed record HostedLoginStart(
    string AuthorizationUrl,
    string State);

/// <summary>A newly issued Hosted session. The token exists only here, like every secret.</summary>
public sealed record CreatedHostedSession(
    Guid SessionId,
    Guid UserId,
    string SessionToken,
    string AntiforgeryToken,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset ExpiresAt);

/// <summary>One tenant as served to its member: ids, name, role and revision, never a secret.</summary>
public sealed record TenantView(
    string Id,
    string Name,
    long Revision,
    string Role);

/// <summary>One live membership as served: the tenant, the caller's role and the scope id.</summary>
public sealed record TenantMembershipView(
    string TenantId,
    string Name,
    string Role,
    string ScopeId);
