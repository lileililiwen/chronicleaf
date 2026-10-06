using Chronicleaf.Application.Access;
using Chronicleaf.Application.Tenancy;
using Chronicleaf.Infrastructure.Access;

namespace Chronicleaf.Infrastructure.Tenancy;

/// <summary>
/// The Hosted half of the access boundary (CLOUD-TENANT-BOUNDARY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This decorator is the only place Hosted identity meets the Community boundary, which is what
/// keeps every domain endpoint untouched: the middleware and the twenty <c>OwnerScopeAsync</c>
/// call sites keep calling <see cref="IOwnerAccessService"/>, and in Community mode every call
/// delegates without a branch taken on Hosted state. Hosted behavior exists only when the
/// tenancy mode is Hosted, and then it is exclusive: an owner cookie authenticates nothing, an
/// operator token signs nobody in, and a domain scope is always the active tenant's.
/// </para>
/// <para>
/// The antiforgery check needs no branch at all: a validated Hosted session carries its stored
/// hash in the context it returns, so the delivered constant-time comparison verifies a Hosted
/// value exactly as it verifies an owner one.
/// </para>
/// <para>
/// The decorator remembers the Hosted session it validated, because it is request-scoped: the
/// boundary middleware and the endpoint resolve the same instance, so the scope resolution in
/// <see cref="EnsureOwnerAsync"/> sees the same session the boundary authenticated without any
/// HTTP context reaching this project. A scope used outside the request that validated it -
/// a background worker, a second request - resolves to nothing here rather than to a stale
/// tenant.
/// </para>
/// </remarks>
public sealed class HostedOwnerAccessDecorator(
    PostgresOwnerAccessService inner,
    TenancyService tenancy,
    TenancySettings settings) : IOwnerAccessService
{
    private OwnerSessionContext? _validatedHostedSession;

    public Task<CreatedOwnerSession> SignInAsync(
        string? presentedToken,
        string clientKey,
        string? currentSessionToken = null,
        CancellationToken cancellationToken = default)
    {
        if (settings.IsHosted)
        {
            // No shared manager/operator token in Hosted: the operator exchange issues nothing,
            // and the failure is the same generic one a wrong token gets.
            throw new InvalidCredentialsException();
        }

        return inner.SignInAsync(presentedToken, clientKey, currentSessionToken, cancellationToken);
    }

    public async Task<OwnerSessionContext?> ValidateAsync(
        string? sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (!settings.IsHosted)
        {
            return await inner.ValidateAsync(sessionToken, cancellationToken).ConfigureAwait(false);
        }

        // Hosted validates Hosted sessions only. An owner cookie presented here authenticates
        // nothing, which is what makes site-local end-user login distinct from operator auth.
        var session = await tenancy.ValidateHostedAsync(sessionToken, cancellationToken).ConfigureAwait(false);
        _validatedHostedSession = session;
        return session;
    }

    public bool VerifyAntiforgery(OwnerSessionContext session, string? presentedToken) =>
        inner.VerifyAntiforgery(session, presentedToken);

    public async Task<string> IssueAntiforgeryAsync(
        OwnerSessionContext session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (settings.IsHosted && await tenancy.IsHostedSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false))
        {
            return await tenancy.IssueHostedAntiforgeryAsync(session, cancellationToken).ConfigureAwait(false);
        }

        return await inner.IssueAntiforgeryAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAsync(string? sessionToken, CancellationToken cancellationToken = default)
    {
        // Revoking an unknown token is a no-op on both tables, so both revocations run: a caller
        // holding either kind of cookie is signed out by the same route.
        await tenancy.RevokeHostedAsync(sessionToken, cancellationToken).ConfigureAwait(false);
        await inner.RevokeAsync(sessionToken, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> RevokeAllSessionsAsync(CancellationToken cancellationToken = default) =>
        inner.RevokeAllSessionsAsync(cancellationToken);

    public async Task<OwnerIdentity> EnsureOwnerAsync(CancellationToken cancellationToken = default)
    {
        if (settings.IsHosted)
        {
            if (_validatedHostedSession is { } session)
            {
                // The installation id this request acts under is the active tenant's scope: every
                // scoped lookup below this point carries the tenant, and a request body cannot
                // influence it. A Hosted session without a resolvable tenant is refused with the
                // reason - no selection, revoked membership or dead session - instead of falling
                // back to the installation scope, which is what would silently reassign Community
                // rows to a tenant.
                var scope = await tenancy.RequireTenantScopeAsync(session, cancellationToken).ConfigureAwait(false);
                return new OwnerIdentity(scope, session.ActorId);
            }

            throw new TenantSelectionRequiredException();
        }

        return await inner.EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);
    }
}
