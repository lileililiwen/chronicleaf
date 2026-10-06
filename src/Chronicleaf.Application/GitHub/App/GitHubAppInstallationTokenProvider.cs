using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Secrets;
using Chronicleaf.GitHub.App;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.GitHub;

/// <summary>
/// Mints installation tokens restricted to one repository, and holds them in memory until just
/// before they expire (GITHUB-APP-CONNECTION-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped, with a singleton cache beside it.</b> The vault is scoped over the unit of work, so
/// this provider is scoped too and leases the App PEM inside the call that needs it. The entries
/// and the per-key gates live in the process-wide cache, which is what makes a concurrent refresh
/// mint once no matter how many callers arrive together.
/// </para>
/// <para>
/// <b>A cached token is bytes in memory and nothing else.</b> It reaches no row, no log line, no
/// operation payload and no exception. It is handed to the transport that reads one repository and
/// is returned - by the lease - when that call is over.
/// </para>
/// </remarks>
public sealed class GitHubAppInstallationTokenProvider(
    GitHubAppTokenCache cache,
    GitHubAppClient app,
    ISecretVault vault,
    IGitHubAppInstallationStore installations,
    GitHubAppOptions configured,
    ILogger<GitHubAppInstallationTokenProvider> logger) : IGitHubInstallationTokenProvider
{
    /// <inheritdoc />
    public async Task<string> GetAsync(
        Guid scopeId,
        long installationId,
        long? repositoryId,
        CancellationToken cancellationToken = default)
    {
        if (repositoryId is null or < 1)
        {
            throw new GitHubAppRepositoryUnselectedException();
        }

        if (cache.TryGet(scopeId, installationId, repositoryId.Value, out var cached))
        {
            return cached;
        }

        // The gate is per key and process-wide, so a concurrent refresh mints once no matter how
        // many callers arrive together - including callers sharing one scope, whose DbContext
        // could never serve two reads at once. The binding is rechecked inside, on every call:
        // a revoked installation has no live row here, and a row that moved to another scope is
        // not this scope's to mint from.
        var gate = cache.GateFor(scopeId, installationId, repositoryId.Value);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.TryGet(scopeId, installationId, repositoryId.Value, out var winner))
            {
                return winner;
            }

            var bound = await installations
                .FindAsync(scopeId, installationId, cancellationToken)
                .ConfigureAwait(false);

            if (bound is null || bound.ScopeId != scopeId || !bound.IsLive)
            {
                throw new GitHubAppInstallationMismatchException();
            }

            var minted = await MintAsync(installationId, repositoryId.Value, cancellationToken).ConfigureAwait(false);
            cache.Put(scopeId, installationId, repositoryId.Value, minted);

            return minted.Token;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate(long installationId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(installationId);

        cache.Invalidate(installationId);
    }

    /// <summary>
    /// Mints one token through the App transport. The PEM is leased, copied to the one string the
    /// JWT needs, and returned before the call returns.
    /// </summary>
    internal async Task<GitHubAppToken> MintAsync(
        long installationId,
        long repositoryId,
        CancellationToken cancellationToken = default)
    {
        if (configured.AppId is null or < 1 ||
            !Guid.TryParse(configured.PrivateKeyCredentialRef, out var keyRef) ||
            keyRef == Guid.Empty)
        {
            throw new GitHubAppNotConfiguredException();
        }

        string pem;
        using (var lease = await vault.ResolveAsync(new SecretRef(keyRef), cancellationToken).ConfigureAwait(false))
        {
            // The App PEM is the server's own credential, not its owner's: it is resolved by
            // reference and the lease's scope is deliberately not compared, because there is no
            // owner scope it could belong to. It is copied once, for the signature below, and the
            // lease is returned here - before any network call, and whatever happens next.
            pem = Encoding.UTF8.GetString(lease.Secret);
        }

        string jwt;
        try
        {
            jwt = app.CreateAppJwt(configured.AppId.Value, pem);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            // The stored PEM does not parse. That is operator-repairable state, not a broken
            // server, and the message names no byte of what was stored.
            throw new GitHubAppNotConfiguredException();
        }
        finally
        {
            pem = string.Empty;
        }

        var (outcome, token, _) = await app
            .CreateInstallationTokenAsync(jwt, installationId, repositoryId, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "GitHub App token mint for installation {InstallationId} answered {Outcome}.",
            installationId,
            outcome);

        return outcome switch
        {
            GitHubAppOutcome.Ok when token is not null => token,
            GitHubAppOutcome.NotFound => throw new GitHubAppInstallationUnknownException(),
            GitHubAppOutcome.Unauthorized => throw new GitHubAppInstallationMismatchException(),
            _ => throw new GitHubAppUnreachableException(),
        };
    }
}

/// <summary>
/// The process-wide installation token entries and their per-key mint gates
/// (GITHUB-APP-CONNECTION-002).
/// </summary>
/// <remarks>
/// <para>
/// Tokens live here and nowhere else: no row, no log, no payload. An entry is served until a
/// minute before GitHub expires it, so a token is never handed to a transport that is about to
/// watch it die. <see cref="Invalidate"/> drops every entry for an installation, which is what
/// makes revocation immediate for tokens that were already minted.
/// </para>
/// </remarks>
public sealed class GitHubAppTokenCache(TimeProvider clock)
{
    /// <summary>How early before GitHub's expiry an entry stops being served.</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private readonly record struct Key(Guid ScopeId, long InstallationId, long RepositoryId);

    private sealed record Entry(string Token, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<Key, Entry> entries = new();
    private readonly ConcurrentDictionary<Key, SemaphoreSlim> gates = new();

    public bool TryGet(Guid scopeId, long installationId, long repositoryId, out string token)
    {
        if (entries.TryGetValue(new Key(scopeId, installationId, repositoryId), out var entry) &&
            clock.GetUtcNow().Add(RefreshSkew) < entry.ExpiresAt)
        {
            token = entry.Token;
            return true;
        }

        token = string.Empty;
        return false;
    }

    public void Put(Guid scopeId, long installationId, long repositoryId, GitHubAppToken minted)
    {
        ArgumentNullException.ThrowIfNull(minted);

        entries[new Key(scopeId, installationId, repositoryId)] = new Entry(minted.Token, minted.ExpiresAt);
    }

    public SemaphoreSlim GateFor(Guid scopeId, long installationId, long repositoryId) =>
        gates.GetOrAdd(new Key(scopeId, installationId, repositoryId), _ => new SemaphoreSlim(1, 1));

    public void Invalidate(long installationId)
    {
        foreach (var key in entries.Keys.Where(candidate => candidate.InstallationId == installationId).ToArray())
        {
            entries.TryRemove(key, out _);
        }
    }
}
