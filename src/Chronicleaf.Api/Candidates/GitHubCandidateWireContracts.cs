using System.Globalization;
using System.Text.Json.Serialization;
using Chronicleaf.Application.GitHubCandidates;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Candidates;

/// <summary>
/// The wire shape of one tag- or event-derived Milestone candidate (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every field here describes a proposal, and none of them could be read as a record.</b> There is
/// no milestone id, no bundle revision, no status a human has approved and no route in this package
/// that would turn one of these into one. What a client gets is what the provider said, when it said
/// it, and which delivery or poll produced it - which is exactly the information a person needs in
/// order to decide whether to go and confirm it themselves.
/// </para>
/// <para>
/// <c>sourceKey</c> is the provider's own identity for the thing proposed, and it is published rather
/// than a server-internal id because it is what makes the row deduplicable: the same release arriving
/// twice is one candidate, and a client that wanted to reason about that would have to be able to see
/// the key. It is also why the delivered candidate vocabulary is closed - a key with a prefix no reader
/// knows would be a key nobody could interpret.
/// </para>
/// <para>
/// Field names are pinned explicitly, as the access and milestone packages' are, so the contract cannot
/// drift with serializer policy.
/// </para>
/// </remarks>
public sealed record GitHubCandidateResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("tagName")] string? TagName,
    [property: JsonPropertyName("revision")] string? Revision,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("deliveryId")] string? DeliveryId,
    [property: JsonPropertyName("receivedAt")] string ReceivedAt)
{
    public static GitHubCandidateResponse From(GitHubCandidateView candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return new GitHubCandidateResponse(
            candidate.Id.ToString("D"),
            candidate.ProjectId.ToString("D"),
            candidate.SourceKey,
            candidate.Kind,
            candidate.Title,
            candidate.TagName,
            candidate.Revision,
            candidate.Source,
            candidate.DeliveryId,
            Stamp(candidate.ReceivedAt));
    }

    internal static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// A project's proposed candidates, and the numbers a panel needs to be honest about an empty state.
/// </summary>
/// <remarks>
/// <para>
/// <c>count</c> is the stored total and <c>items</c> is the bounded page. They are different numbers on
/// purpose: a project with two hundred candidates and a page of twenty-five is not an empty list, and a
/// panel that reported the page as the total would tell its owner they had lost a hundred and seventy
/// five proposals.
/// </para>
/// </remarks>
public sealed record GitHubCandidateListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<GitHubCandidateResponse> Items,
    [property: JsonPropertyName("count")] long Count)
{
    public static GitHubCandidateListResponse From(
        IReadOnlyList<GitHubCandidateView> items,
        long count) =>
        new([.. items.Select(GitHubCandidateResponse.From)], count);
}

/// <summary>
/// A project's webhook subscription as its owner reads it: never the secret, never a host.
/// </summary>
/// <remarks>
/// <c>hasSecret</c> is a boolean rather than the vault's identifier. An owner needs to know that a
/// secret is configured; a stored id in a browser's memory is one more copy of a thing that identifies
/// a secret, and this package's own delivery route is the only thing that ever needs the reference.
/// </remarks>
public sealed record GitHubWebhookSubscriptionResponse(
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("hasSecret")] bool HasSecret,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt)
{
    public static GitHubWebhookSubscriptionResponse From(GitHubWebhookSubscriptionView subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        return new GitHubWebhookSubscriptionResponse(
            subscription.ConnectionId.ToString("D"),
            subscription.ProjectId.ToString("D"),
            subscription.Enabled,
            subscription.HasSecret,
            GitHubCandidateResponse.Stamp(subscription.UpdatedAt));
    }
}

/// <summary>
/// The body a subscription is stored from (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>One field, and it is a reference.</b> The secret is stored first through the delivered vault route
/// under the <c>github-webhook</c> provider; this route then points the connection at it. It never
/// accepts a secret value and never returns one, so there is no path by which a webhook secret could
/// reach a request body, a log line, an envelope or an exception message.
/// </para>
/// <para>
/// An absent or null <c>credentialRef</c> clears the subscription rather than being a malformed body:
/// the owner unsubscribing is a legitimate outcome of asking for no secret, and refusing it would mean
/// the only way to stop a connection accepting deliveries was a different route.
/// </para>
/// </remarks>
public sealed record GitHubWebhookSubscribeRequest(
    [property: JsonPropertyName("credentialRef")] string? CredentialRef);

/// <summary>
/// The body a candidate poll is asked for (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// The project revision travels as <c>If-Match</c> and is not repeated in the body: it is a
/// precondition on the project, not a value the client gets to choose, and a body that carried its own
/// copy would be a second place the two could disagree. The idempotency key is the caller's, and a
/// repeat of the same key returns the job the first request created rather than polling twice.
/// </remarks>
public sealed record GitHubCandidateSyncRequest(
    [property: JsonPropertyName("since")] string? Since,
    [property: JsonPropertyName("idempotencyKey")] string? IdempotencyKey)
{
    /// <summary>
    /// The request a body that did not arrive produces: no window and no key, so a POST with no body
    /// at all is refused by name for the key rather than being read as "revision zero, whole history".
    /// </summary>
    public static GitHubCandidateSyncRequest Empty { get; } = new(Since: null, IdempotencyKey: null);
}

/// <summary>
/// The answer to a poll request: the job that will perform it, and where to read its progress
/// (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// A 202 and an operation id, never a candidate. A poll is a bounded set of network reads against a
/// rate-limited host, so the work happens on the delivered queue and this response points at the
/// delivered operation route. <c>created</c> says whether this request created the job or replayed one
/// under an idempotency key it had already used.
/// </remarks>
public sealed record GitHubCandidateSyncAcceptedResponse(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("operationUrl")] string OperationUrl,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created")] bool Created);

/// <summary>
/// The receipt a signed delivery is answered with, and the answer a retry of the same delivery also gets
/// (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <c>status</c> is <c>pending</c> on the first delivery and stays whatever it became on a retry, which
/// is the point: a sender that is told the original outcome learns nothing about which of its two
/// problems to fix, and a receipt a reader sees and the candidate rows a reader counts cannot disagree.
/// </para>
/// <para>
/// There is no body echo here, no provider prose and no signature. Nothing a delivery carried is ever
/// written into this response, because this response is the one artefact a misconfigured sender is
/// guaranteed to log.
/// </para>
/// </remarks>
public sealed record GitHubWebhookReceiptResponse(
    [property: JsonPropertyName("deliveryId")] string DeliveryId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("candidateCount")] int CandidateCount,
    [property: JsonPropertyName("receivedAt")] string ReceivedAt,
    [property: JsonPropertyName("duplicate")] bool Duplicate)
{
    public static GitHubWebhookReceiptResponse From(GitHubWebhookReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return new GitHubWebhookReceiptResponse(
            receipt.DeliveryId,
            receipt.Event,
            receipt.Action,
            receipt.Status,
            receipt.Reason,
            receipt.CandidateCount,
            GitHubCandidateResponse.Stamp(receipt.ReceivedAt),
            receipt.Duplicate);
    }
}
