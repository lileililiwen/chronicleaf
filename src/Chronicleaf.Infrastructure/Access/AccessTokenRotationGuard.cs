using Chronicleaf.Application.Access;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core.Access;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Infrastructure.Access;

/// <summary>
/// Applies the configured access token hash exactly once per process, and revokes every existing
/// session when that hash is not the one this installation last started with.
/// </summary>
/// <remarks>
/// The applied fingerprint is a row in <c>access_state</c>, so the revocation is durable: it is
/// still in force after a restart, after a crash, and for any other process pointed at the same
/// database. Comparing fingerprints rather than tokens means the stored value is never a usable
/// credential, and the comparison is fixed-time.
/// <para>
/// A first application also revokes. On a genuinely new installation there is nothing to revoke,
/// and for an installation whose state row was lost the fail-safe direction is to close every
/// session rather than to trust sessions issued under a hash nobody can now prove.
/// </para>
/// <para>
/// The guard resolves its work from a fresh scope on every application because it is a singleton
/// that runs at start-up and again on the login path. One process owns one installation's
/// sessions; running two API processes against the same Community database is outside this
/// package's scope.
/// </para>
/// </remarks>
public sealed class AccessTokenRotationGuard(
    IServiceScopeFactory scopeFactory,
    AccessSettings settings,
    TimeProvider clock,
    ILogger<AccessTokenRotationGuard> logger)
    : IAccessTokenRotationGuard
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _applied;
    private int _lastRevokedSessionCount;

    public int LastRevokedSessionCount => Volatile.Read(ref _lastRevokedSessionCount);

    /// <summary>
    /// Applies the configured access token hash exactly once per process. The composition root
    /// calls this at start-up, and every login calls it again, so a hash changed while the process
    /// is alive is applied before anything is accepted.
    /// </summary>
    public async Task<int> EnsureAppliedAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _applied))
        {
            return LastRevokedSessionCount;
        }

        return await ApplyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ApplyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_applied)
            {
                return LastRevokedSessionCount;
            }

            using var scope = scopeFactory.CreateScope();
            var provider = scope.ServiceProvider;
            var dbContext = provider.GetRequiredService<ChronicleafDbContext>();
            var access = provider.GetRequiredService<IOwnerAccessService>();

            var fingerprint = settings.TokenHashFingerprint;
            var now = clock.GetUtcNow();

            var state = await dbContext.AccessTokenStates
                .AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == AccessTokenState.SingletonId, cancellationToken)
                .ConfigureAwait(false);

            if (state is not null && Sha256.FixedTimeEqualsHex(state.TokenHashFingerprint, fingerprint))
            {
                // The same hash this installation already started with: nothing to revoke.
                Volatile.Write(ref _applied, true);
                return LastRevokedSessionCount;
            }

            var revoked = await access.RevokeAllSessionsAsync(cancellationToken).ConfigureAwait(false);

            if (state is null)
            {
                dbContext.AccessTokenStates.Add(new AccessTokenState(AccessTokenState.SingletonId, fingerprint, now));
            }
            else
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE access_state SET token_hash_fingerprint = {fingerprint}, updated_at = {now} WHERE id = {AccessTokenState.SingletonId}",
                    cancellationToken).ConfigureAwait(false);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref _lastRevokedSessionCount, revoked);
            Volatile.Write(ref _applied, true);

            // What is logged is the fact and the count. The hash, its fingerprint and every token
            // stay out of the log.
            logger.LogInformation(
                "Applied the configured access token; {RevokedSessionCount} existing session(s) were revoked.",
                revoked);

            return revoked;
        }
        finally
        {
            _gate.Release();
        }
    }
}
