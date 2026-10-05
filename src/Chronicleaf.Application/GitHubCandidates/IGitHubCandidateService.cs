using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Application.GitHubCandidates;

/// <summary>
/// The GitHub candidate use cases over the delivered stores, vault, queue and repository connection
/// (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here confirms anything.</b> There is no method that creates a milestone, appends evidence,
/// writes a draft or publishes, and no method that could be described as doing so by accident: a
/// candidate is written and read back, and what an owner does with it is a decision this package does not
/// hold. That is the whole of the "human-reviewed" in the requirement, and it is structural rather than
/// a promise: there is no verb to call.
/// </para>
/// <para>
/// <b>A delivery writes through the delivered outbox.</b> The webhook route's own transaction records a
/// receipt and enqueues one inbox event; the candidates themselves are written by the outbox consumer
/// that later picks it up. That is what lets the route answer 202 after a bounded authentication and
/// nothing else, and it is why a candidate can carry a resolved commit without the route waiting on a
/// network read.
/// </para>
/// </remarks>
public interface IGitHubCandidateService
{
    /// <summary>Reads a project's webhook subscription, or <c>null</c> when it has none.</summary>
    Task<GitHubWebhookSubscriptionView?> ReadSubscriptionAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Points a project's connection at a stored webhook secret, or clears the subscription when no
    /// secret is named.
    /// </summary>
    Task<GitHubWebhookSubscriptionView> SubscribeAsync(
        OwnerScope owner,
        Guid projectId,
        GitHubWebhookSubscribeRequest request,
        long expectedProjectRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Stops a connection accepting deliveries, without losing the receipts it already has.</summary>
    Task<GitHubWebhookSubscriptionView?> UnsubscribeAsync(
        OwnerScope owner,
        Guid projectId,
        long expectedProjectRevision,
        CancellationToken cancellationToken = default);

    /// <summary>A project's proposed candidates, newest first.</summary>
    Task<IReadOnlyList<GitHubCandidateView>> ListCandidatesAsync(
        OwnerScope owner,
        Guid projectId,
        int? limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many candidates a project has stored, which is the number an empty state differs from a
    /// stored one by.
    /// </summary>
    /// <remarks>
    /// Separate from the list on purpose. A panel that reported a bounded page as the total would tell
    /// its owner they had lost every proposal past the twenty-fifth, and a panel that reported the
    /// total instead of a page would have had to read them all to show twenty-five.
    /// </remarks>
    Task<long> CountCandidatesAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Asks a project's repository to be polled for releases and tags.</summary>
    Task<GitHubCandidateSyncAccepted> RequestSyncAsync(
        OwnerScope owner,
        Guid projectId,
        GitHubCandidateSyncRequest request,
        long expectedProjectRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts a signed delivery, and answers with the receipt a retry of the same delivery also gets.
    /// </summary>
    /// <exception cref="GitHubCandidateSignatureInvalidException">The delivery did not authenticate.</exception>
    /// <exception cref="GitHubCandidateDeliveryMalformedException">It authenticated but could not be read.</exception>
    Task<GitHubWebhookReceipt> ReceiveDeliveryAsync(
        Guid connectionId,
        string? eventName,
        string? deliveryId,
        string? signature,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns one stored inbox event into candidates, once.
    /// </summary>
    /// <remarks>
    /// <para>Called by the outbox consumer, not by a route. It is idempotent by construction: the
    /// receipt's source keys are the dedupe, and a receipt that already carries an outcome is left
    /// alone, so a redelivered inbox event cannot produce a second candidate for the same thing.</para>
    /// <para><paramref name="receiptId"/> is this server's own row id, because that is the only
    /// identifier the outbox envelope can carry. The connection and the delivery id are read back from
    /// the row, which is also what makes a queued event unable to name a connection it does not belong
    /// to.</para>
    /// </remarks>
    Task ProcessInboxAsync(Guid scopeId, Guid receiptId, CancellationToken cancellationToken = default);

    /// <summary>Performs the poll a job was created for.</summary>
    Task<GitHubCandidatePolled> PollAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default);
}
