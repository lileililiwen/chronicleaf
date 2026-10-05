using Chronicleaf.Core;

namespace Chronicleaf.Application.Git;

/// <summary>
/// What an owner asked to register, exactly as it arrived (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// Two words, and neither of them is a path to the filesystem. The alias is a name the operator
/// registered; the relative path is resolved inside it and is refused if it leaves. There is no third
/// field that could carry an absolute directory, and that absence is the security property rather
/// than a simplification - a body that could name a directory would be a body that decides what this
/// server reads.
/// </para>
/// <para>
/// The absolute path never travels back out either. The stored row holds the relative half, the
/// response holds the relative half, and the operator's half stays in the operator's configuration.
/// </para>
/// </remarks>
public sealed record LocalRepositoryRequest(string? RootAlias, string? RelativePath);

/// <summary>
/// What an owner asked a milestone to collect (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <param name="HeadRef">The ref to collect. Required, and checked before a job exists.</param>
/// <param name="BaseRef">The ref to compare from, when the owner named one.</param>
/// <param name="ExpectedBundleRevision">The evidence revision the owner was looking at.</param>
/// <param name="IdempotencyKey">The caller's key: a repeat of the same request returns the same job.</param>
public sealed record LocalGitSyncRequest(
    string? HeadRef,
    string? BaseRef,
    long ExpectedBundleRevision,
    string IdempotencyKey);

/// <summary>
/// A collection request that was accepted, and the durable job that will carry it out
/// (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <param name="Operation">
/// The operation that exists - the new one, or the original of a replay. A repeat of the same
/// idempotency key returns the original job rather than starting a second collection.
/// </param>
/// <param name="Created">False when an identical request had already created this operation.</param>
public sealed record LocalGitSyncAccepted(Operation Operation, bool Created);

/// <summary>
/// What one collection produced, as the operation's result reference and the numbers a reader needs
/// (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <param name="BundleRevision">The evidence revision the append produced.</param>
/// <param name="ItemCount">How many evidence items were appended.</param>
/// <param name="CommitCount">How many commits were recorded.</param>
/// <param name="ChangedFileCount">How many changed files were recorded.</param>
/// <param name="Truncated">True when at least one segment stopped at a cap.</param>
/// <param name="ExcludedFileCount">How many files were excluded by name.</param>
/// <param name="UncommittedCount">
/// How many uncommitted changes the working tree held and this collection deliberately did not read.
/// </param>
public sealed record LocalGitCollected(
    long BundleRevision,
    int ItemCount,
    int CommitCount,
    int ChangedFileCount,
    bool Truncated,
    int ExcludedFileCount,
    int UncommittedCount);

/// <summary>
/// The connection side of storage. Every method is scoped: a lookup for a project this owner may not
/// reach answers <c>null</c> and the route turns that into a 404.
/// </summary>
public interface ILocalRepositoryStore
{
    /// <summary>Reads the registered repository of one project in a scope, or <c>null</c>.</summary>
    Task<LocalRepository?> FindAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the registered repository that covers a milestone, by joining through its project. This is
    /// what a job uses, because a job is handed a milestone id and nothing else.
    /// </summary>
    Task<LocalRepository?> FindForMilestoneAsync(
        Guid scopeId,
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the registration for a project, replacing whatever was stored for it. The id of an
    /// existing registration is kept, so a route that answers with a repository id answers with the
    /// same one the owner has been looking at.
    /// </summary>
    Task<LocalRepository> UpsertAsync(LocalRepository repository, CancellationToken cancellationToken = default);
}

/// <summary>The run-log side of storage: one row per local collection run.</summary>
public interface ILocalSyncSourceStore
{
    /// <summary>Inserts the row for a run that was just asked for.</summary>
    /// <returns>False when a row for this operation is already there, which a replayed request is.</returns>
    Task<bool> TryInsertAsync(LocalSyncSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the run a job was asked to carry out. The job is handed an operation id and nothing else,
    /// so this is the only way it can learn which refs the owner named.
    /// </summary>
    Task<LocalSyncSource?> FindByOperationAsync(
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
    Task<bool> TryCompleteAsync(LocalSyncSource source, CancellationToken cancellationToken = default);
}
