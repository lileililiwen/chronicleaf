using Chronicleaf.Application.Access;

namespace Chronicleaf.Api.Access;

/// <summary>
/// Applies the configured access token hash when the host starts
/// (COMMUNITY-ACCESS-001: changing the configured hash revokes all existing sessions).
/// </summary>
/// <remarks>
/// This lives in the composition root because applying the token is a lifecycle decision, not a
/// storage one: the Infrastructure guard is the mechanism, and the API decides when to run it.
/// <para>
/// An unreachable database does not fail start-up. Liveness has to stay honest and
/// <c>GET /api/ready</c> has to report the real problem, so the failure is logged and the guard
/// stays unapplied - which means no login is accepted until the token has actually been applied to
/// the database, rather than a login being accepted by a process that could not check the rotation.
/// </para>
/// </remarks>
public sealed class AccessTokenRotationStartup(IAccessTokenRotationGuard rotationGuard, ILogger<AccessTokenRotationStartup> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await rotationGuard.EnsureAppliedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "The configured access token could not be applied at start-up; no login is accepted until it is.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
