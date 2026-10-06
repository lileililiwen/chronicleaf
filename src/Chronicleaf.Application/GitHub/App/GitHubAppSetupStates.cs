using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Chronicleaf.Application.GitHub;

/// <summary>
/// The single-use setup states an App connection is completed with
/// (GITHUB-APP-CONNECTION-001).
/// </summary>
/// <remarks>
/// <para>
/// A state is 32 random bytes, bound to the scope and actor it was issued to, alive for ten
/// minutes and consumed when it is accepted. A replayed callback finds nothing to consume and is
/// refused exactly like a forged one, and a state issued to one session can never complete a
/// connection in another.
/// </para>
/// <para>
/// In memory only, by design: a state is a nonce, not a record, and a restart that forgets every
/// outstanding setup is a safe outcome - the owner simply starts the connection again. Hangfire
/// runs in this process, so there is exactly one holder.
/// </para>
/// </remarks>
public sealed class GitHubAppSetupStates(TimeProvider clock)
{
    /// <summary>How long an issued state may be completed in.</summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    private readonly record struct Entry(Guid ScopeId, Guid ActorId, Guid ProjectId, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> states = new(StringComparer.Ordinal);

    /// <summary>Issues a state for one scope, actor and project.</summary>
    public GitHubAppSetup Issue(Guid scopeId, Guid actorId, Guid projectId)
    {
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = clock.GetUtcNow().Add(TimeToLive);

        states[state] = new Entry(scopeId, actorId, projectId, expiresAt);

        return new GitHubAppSetup(state, expiresAt);
    }

    /// <summary>
    /// Consumes a state for one scope, actor and project, or returns <c>null</c> when it is
    /// unknown, expired, bound elsewhere or already used.
    /// </summary>
    public bool TryConsume(string? state, Guid scopeId, Guid actorId, Guid projectId)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        if (!states.TryRemove(state, out var entry))
        {
            return false;
        }

        return entry.ScopeId == scopeId
            && entry.ActorId == actorId
            && entry.ProjectId == projectId
            && clock.GetUtcNow() <= entry.ExpiresAt;
    }
}
