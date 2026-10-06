using Chronicleaf.Core;

namespace Chronicleaf.Application.GitHub;

/// <summary>
/// What an owner asked to connect, exactly as it arrived (GITHUB-EVIDENCE-001).
/// </summary>
/// <remarks>
/// <para>
/// Two names and an optional credential reference. There is no URL field, and that is the security
/// property rather than a simplification: a body that could carry a host would let a request choose
/// where the server sends its next request, and possibly with a token on it.
/// </para>
/// <para>
/// The credential arrives as a reference, never as a token. The service resolves it against the
/// vault to confirm it belongs to this owner and this provider, and stores the id again - so a
/// replayed request, a log line and the stored row all carry the same identifier and none of them
/// carries a secret.
/// </para>
/// </remarks>
public sealed record GitHubConnectionRequest(string? Owner, string? Repository, Guid? CredentialRef);

/// <summary>
/// What an owner asked a milestone to collect (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <param name="HeadRef">The ref to collect. Required, and checked before a job exists.</param>
/// <param name="BaseRef">The ref to compare from, when the owner named one.</param>
/// <param name="ReleaseTag">A release to read as well, when the owner named one.</param>
/// <param name="IssueNumber">An issue to read as well, when the owner named one.</param>
/// <param name="PullRequestNumber">A pull request to read as well, when the owner named one.</param>
/// <param name="ExpectedBundleRevision">The evidence revision the owner was looking at.</param>
/// <param name="IdempotencyKey">The caller's key: a repeat of the same request returns the same job.</param>
public sealed record GitHubSyncRequest(
    string? HeadRef,
    string? BaseRef,
    string? ReleaseTag,
    int? IssueNumber,
    int? PullRequestNumber,
    long ExpectedBundleRevision,
    string IdempotencyKey);

/// <summary>
/// A sync request that was accepted, and the durable job that will carry it out.
/// </summary>
/// <param name="Operation">
/// The operation that exists - the new one, or the original of a replay. A repeat of the same
/// idempotency key returns the original job rather than starting a second collection.
/// </param>
/// <param name="Created">False when an identical request had already created this operation.</param>
public sealed record GitHubSyncAccepted(Operation Operation, bool Created);

/// <summary>
/// What one collection produced, as the operation's result reference and the numbers a reader needs
/// (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <param name="BundleRevision">The evidence revision the append produced.</param>
/// <param name="ItemCount">How many evidence items were appended.</param>
/// <param name="CommitCount">How many commits were recorded.</param>
/// <param name="ChangedFileCount">How many changed files were recorded.</param>
/// <param name="Truncated">True when at least one segment stopped at a cap.</param>
/// <param name="ExcludedFileCount">How many files were excluded by name.</param>
public sealed record GitHubCollected(
    long BundleRevision,
    int ItemCount,
    int CommitCount,
    int ChangedFileCount,
    bool Truncated,
    int ExcludedFileCount);

/// <summary>
/// The connection side of storage. Every method is scoped: a lookup for a project this owner may not
/// reach answers <c>null</c> and the route turns that into a 404.
/// </summary>
public interface IGitHubConnectionStore
{
    /// <summary>Reads the connection of one project in a scope, or <c>null</c>.</summary>
    Task<GitHubConnection?> FindAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the connection that covers a milestone, by joining through its project. This is what a
    /// job uses, because a job is handed a milestone id and nothing else.
    /// </summary>
    Task<GitHubConnection?> FindForMilestoneAsync(
        Guid scopeId,
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the connection for a project, replacing whatever was stored for it. The id of an
    /// existing connection is kept, so a route that answers with a connection id answers with the
    /// same one the owner has been looking at.
    /// </summary>
    Task<GitHubConnection> UpsertAsync(GitHubConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the connection of one project in a scope. Used when an App installation is
    /// disconnected: a token connection that no longer has an installation behind it must not
    /// survive as a row a job could still collect from.
    /// </summary>
    Task DeleteAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every connection bound to one installation in a scope. Used when GitHub reports the
    /// installation removed: revocation is immediate, and a queued job finds no connection rather
    /// than a token it could still read with.
    /// </summary>
    Task DeleteByInstallationAsync(Guid scopeId, long installationId, CancellationToken cancellationToken = default);
}

/// <summary>The run-log side of storage: one row per collection run.</summary>
public interface ISyncSourceStore
{
    /// <summary>Inserts the row for a run that was just asked for.</summary>
    /// <returns>False when a row for this operation is already there, which a replayed request is.</returns>
    Task<bool> TryInsertAsync(SyncSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the run a job was asked to carry out. The job is handed an operation id and nothing
    /// else, so this is the only way it can learn which refs the owner named.
    /// </summary>
    Task<SyncSource?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records what the run read.
    /// </summary>
    /// <returns>
    /// False when the row was already completed - a second attempt on the same operation must not
    /// overwrite what the first one recorded.
    /// </returns>
    Task<bool> TryCompleteAsync(SyncSource source, CancellationToken cancellationToken = default);
}
