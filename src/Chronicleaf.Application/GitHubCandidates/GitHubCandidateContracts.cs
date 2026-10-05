using Chronicleaf.Core;

namespace Chronicleaf.Application.GitHubCandidates;

/// <summary>What an owner asked a project's repository to be polled for (GITHUB-RELEASE-CANDIDATES-001).</summary>
/// <param name="Since">
/// Only releases and tags at or after this instant are proposed, when the owner named one. An absent
/// window proposes everything one bounded page holds, which is the honest default for a first poll.
/// </param>
/// <param name="IdempotencyKey">The caller's key: a repeat of the same request returns the same job.</param>
public sealed record GitHubCandidateSyncRequest(DateTimeOffset? Since, string IdempotencyKey);

/// <summary>What an owner asked a project's connection to accept (GITHUB-RELEASE-CANDIDATES-001).</summary>
/// <param name="CredentialRef">
/// The vault reference of a stored webhook secret, never the secret. A project may have a GitHub token
/// stored already; this is a different credential, because a token authenticates Chronicleaf to GitHub
/// and a webhook secret authenticates GitHub to Chronicleaf, and one key cannot do both jobs.
/// </param>
public sealed record GitHubWebhookSubscribeRequest(Guid? CredentialRef);

/// <summary>A poll that was accepted, and the durable job that will carry it out.</summary>
/// <param name="Operation">
/// The operation that exists - the new one, or the original of a replay. A repeat of the same
/// idempotency key returns the original job rather than polling twice.
/// </param>
/// <param name="Created">False when an identical request had already created this operation.</param>
public sealed record GitHubCandidateSyncAccepted(Operation Operation, bool Created);

/// <summary>What one poll produced, as the operation's result reference and the numbers a reader needs.</summary>
/// <param name="CandidateCount">How many candidates this poll proposed, duplicates included or not.</param>
/// <param name="Recorded">How many of them were newly written rather than already present.</param>
/// <param name="ReleaseCount">How many published releases were read.</param>
/// <param name="TagCount">How many tags were read.</param>
public sealed record GitHubCandidatePolled(
    int CandidateCount,
    int Recorded,
    int ReleaseCount,
    int TagCount);

/// <summary>What a signed delivery was recorded as, and the receipt a retry gets back.</summary>
/// <param name="DeliveryId">GitHub's own identifier for the delivery.</param>
/// <param name="Event">
/// The event name the stored receipt holds. On a retry this is the original delivery's own event rather
/// than whatever header arrived this time, so a sender redelivering under a different header is told
/// what was actually recorded rather than what it just said.
/// </param>
/// <param name="Action">The action the stored receipt holds, on the same terms.</param>
/// <param name="Status">What this build did with it, from the closed receipt vocabulary.</param>
/// <param name="Reason">Why it produced nothing, from the closed reason vocabulary, when it produced nothing.</param>
/// <param name="CandidateCount">How many candidates this delivery produced.</param>
/// <param name="ReceivedAt">When the delivery arrived.</param>
/// <param name="Duplicate">
/// True when the delivery had already been accepted, which is what a retry looks like. The receipt is
/// the original one either way, so a sender cannot tell from the answer whether it was first.
/// </param>
public sealed record GitHubWebhookReceipt(
    string DeliveryId,
    string Event,
    string? Action,
    string Status,
    string? Reason,
    int CandidateCount,
    DateTimeOffset ReceivedAt,
    bool Duplicate);

/// <summary>One candidate as an owner reads it, with nothing that would let it pass for a Milestone.</summary>
/// <param name="Id">The candidate's own identifier.</param>
/// <param name="ProjectId">The project it proposes something about.</param>
/// <param name="SourceKey">The provider's own identity for the thing proposed.</param>
/// <param name="Kind">Which kind of thing it proposes.</param>
/// <param name="Title">The bounded label a human reads.</param>
/// <param name="TagName">The tag or release name, when the candidate is one of those.</param>
/// <param name="Revision">The commit or stable revision the source named.</param>
/// <param name="Source">Whether a signed delivery or a poll proposed it.</param>
/// <param name="DeliveryId">The receipt that proposed it, when a delivery did.</param>
/// <param name="ReceivedAt">When the source said it happened.</param>
public sealed record GitHubCandidateView(
    Guid Id,
    Guid ProjectId,
    string SourceKey,
    string Kind,
    string Title,
    string? TagName,
    string? Revision,
    string Source,
    string? DeliveryId,
    DateTimeOffset ReceivedAt);

/// <summary>A project's webhook subscription as its owner reads it. Never the secret, never a host.</summary>
/// <param name="ConnectionId">The connection that accepts a signed delivery.</param>
/// <param name="ProjectId">The project it belongs to.</param>
/// <param name="Enabled">Whether a delivery for this connection is routed at all.</param>
/// <param name="HasSecret">Whether a secret is stored, as a boolean because an owner needs to know that and nothing more.</param>
/// <param name="UpdatedAt">When the subscription last changed.</param>
public sealed record GitHubWebhookSubscriptionView(
    Guid ConnectionId,
    Guid ProjectId,
    bool Enabled,
    bool HasSecret,
    DateTimeOffset UpdatedAt);

/// <summary>The connection side of storage: one subscription per connection, scoped.</summary>
public interface IGitHubWebhookSubscriptionStore
{
    /// <summary>
    /// Reads the subscription of one connection, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately addressed by connection id rather than by project: the webhook route knows a
    /// connection id and nothing else, and a read that needed a project id would make the route's only
    /// lookup impossible before it has authenticated anybody.
    /// </remarks>
    Task<GitHubWebhookSubscription?> FindByConnectionAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the subscription of one project in a scope, or <c>null</c>.</summary>
    Task<GitHubWebhookSubscription?> FindForProjectAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a subscription, replacing whatever was stored for that connection and keeping its id.
    /// </summary>
    /// <remarks>
    /// The id is kept so a route that answers with a connection id answers with the one the owner has
    /// been looking at, and so an archived subscription can be reactivated rather than replaced by a
    /// row a past delivery would no longer find.
    /// </remarks>
    Task<GitHubWebhookSubscription> UpsertAsync(
        GitHubWebhookSubscription subscription,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives a subscription, or reports that it was already archived.
    /// </summary>
    /// <returns>False when there was nothing to archive.</returns>
    Task<bool> TryArchiveAsync(
        Guid scopeId,
        Guid projectId,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>The receipt side of storage: one row per signed delivery, deduplicated by GitHub's own id.</summary>
public interface IGitHubWebhookDeliveryStore
{
    /// <summary>
    /// Stores the receipt for a delivery that has just arrived.
    /// </summary>
    /// <returns>
    /// The stored receipt, and whether this call was the one that created it. False means the delivery
    /// is a retry, and the returned row is the original receipt rather than a second one - which is
    /// what lets a sender be told the same thing twice.
    /// </returns>
    Task<(GitHubWebhookDelivery Delivery, bool Created)> AcceptAsync(
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the receipt this inbox event is about, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <b>Addressed by the receipt row's own id, scoped.</b> The outbox envelope's aggregate is a GUID
    /// and its revision is one or greater, so the only identifier a queued event can carry is an id
    /// this server generated - GitHub's own delivery id is a string of no guaranteed shape. The
    /// connection and the delivery id are therefore read back from the row rather than smuggled through
    /// a queue envelope that has room for neither, and the scope travels with the read so a receipt
    /// belonging to another installation is simply not found.
    /// </remarks>
    Task<GitHubWebhookDelivery?> FindByReceiptAsync(
        Guid scopeId,
        Guid receiptId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records what a delivery produced, once.
    /// </summary>
    /// <returns>False when the receipt already has an outcome, which a redelivery is.</returns>
    Task<bool> TryRecordOutcomeAsync(
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken = default);

    /// <summary>A connection's receipts, newest first, bounded by the delivered list policy.</summary>
    Task<IReadOnlyList<GitHubWebhookDelivery>> ListAsync(
        Guid scopeId,
        Guid connectionId,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>The candidate side of storage. Candidates are proposals, so they are only ever added.</summary>
public interface IGitHubCandidateStore
{
    /// <summary>
    /// Records a candidate, or reports that its source key is already there.
    /// </summary>
    /// <remarks>
    /// The unique index on <c>(scope_id, source_key)</c> decides this in the database rather than in a
    /// read-then-write, so two deliveries of the same release that arrive at the same instant produce
    /// one candidate and one refusal rather than two candidates. The refusal is not an error: it is the
    /// answer "this is already proposed", which the caller counts and reports.
    /// </remarks>
    /// <returns>The stored candidate, and whether this call created it.</returns>
    Task<(GitHubCandidate Candidate, bool Created)> TryRecordAsync(
        GitHubCandidate candidate,
        CancellationToken cancellationToken = default);

    /// <summary>A project's candidates, newest first.</summary>
    Task<IReadOnlyList<GitHubCandidate>> ListForProjectAsync(
        Guid scopeId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Counts a project's candidates, which is what an empty state and a stored state differ by.</summary>
    Task<long> CountForProjectAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default);
}

/// <summary>The run log: one row per poll, written when the owner asks and completed by the job.</summary>
public interface IGitHubCandidateRunStore
{
    /// <summary>Inserts the row for a poll that was just asked for.</summary>
    /// <returns>False when a row for this operation is already there, which a replayed request is.</returns>
    Task<bool> TryInsertAsync(GitHubCandidateRun run, CancellationToken cancellationToken = default);

    /// <summary>Reads the run a job was asked to carry out. A job is handed an operation id and nothing else.</summary>
    Task<GitHubCandidateRun?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records what a poll read.
    /// </summary>
    /// <returns>False when the row was already completed: a second attempt must not overwrite the first.</returns>
    Task<bool> TryCompleteAsync(GitHubCandidateRun run, CancellationToken cancellationToken = default);
}
