using System.Globalization;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Chronicleaf.GitHub.Candidates;
using Chronicleaf.GitHub.Evidence;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.GitHubCandidates;

/// <summary>
/// The GitHub candidate use cases over the delivered stores, vault, queue and repository connection
/// (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The dependency's contracts are consumed, never redefined.</b> The repository a poll reads comes
/// from the delivered <c>GitHubConnection</c> row, the work is a delivered operation on the delivered
/// queue, the webhook secret is a delivered vault credential, and the secret is opened by a lease that
/// is returned before the call that used it returns. This package adds no transport under
/// <c>IGitEvidenceSource</c> and no store under a delivered name; the one transport it adds is
/// <c>IGitHubCandidateSource</c>, a name of its own, because two names are already taken and a third
/// registration under either would move every collection onto the wrong reader.
/// </para>
/// <para>
/// <b>A candidate is a proposal and this class cannot make it into anything else.</b> There is no
/// milestone write, no evidence append, no draft and no publish anywhere below, and the only tables this
/// package writes are its own four. The boundary an owner crosses to confirm something is a different
/// package's route, which is what makes "human-reviewed" a structural fact rather than a UI convention.
/// </para>
/// <para>
/// <b>The delivery route authenticates over raw bytes and nothing else.</b> The signature is checked
/// against the bytes the server read, before the body is parsed, before a row exists and before a
/// receipt is written. A body this build cannot read afterwards is a 422 on an authenticated delivery,
/// which is a different answer from a 401 and is deliberately distinguishable so a correctly configured
/// sender can tell a wrong signature from an unreadable payload.
/// </para>
/// </remarks>
public sealed class GitHubCandidateService(
    IProjectStore projects,
    IGitHubConnectionStore connections,
    IGitHubWebhookSubscriptionStore subscriptions,
    IGitHubWebhookDeliveryStore deliveries,
    IGitHubCandidateStore candidates,
    IGitHubCandidateRunStore runs,
    IGitHubCandidateSource source,
    ISecretVault vault,
    IOperationService operations,
    IOutboxStore outbox,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<GitHubCandidateService> logger) : IGitHubCandidateService
{
    /// <summary>The job kind this package owns. A project is the target, so the delivered supersede check reads its revision.</summary>
    public const string SyncOperationKind = "project.github-candidates-sync";

    /// <summary>
    /// The outbox kind this package consumes: one accepted delivery, waiting to become candidates.
    /// </summary>
    /// <remarks>
    /// An inbox event rather than a delivery row, because the webhook route's own transaction must not
    /// also do the work. Recording the receipt and enqueuing this event commit together; the candidates
    /// are written by the consumer afterwards, off a request that has already answered 202. That is
    /// also what lets a created tag be proposed with the commit it names without the route waiting on a
    /// network read to resolve it.
    /// </remarks>
    public const string InboxOutboxKind = "github.candidate-delivery.received";

    /// <summary>The vault provider a webhook secret is stored for, as the delivered vault names it.</summary>
    public const string WebhookCredentialProvider = "github-webhook";

    /// <summary>The audit action recorded when a subscription is stored or replaced.</summary>
    public const string SubscriptionUpsertedAction = "github.webhook.subscribe";

    /// <summary>The audit action recorded when a subscription is archived.</summary>
    public const string SubscriptionArchivedAction = "github.webhook.unsubscribe";

    /// <summary>The audit action recorded when a poll is asked for.</summary>
    public const string SyncRequestedAction = "github.candidates.requested";

    private const int MaxListLimit = 100;
    private const int DefaultListLimit = 25;

    private static readonly JsonSerializerOptions ProposalWire = new(JsonSerializerDefaults.Web);

    // -----------------------------------------------------------------------------------------
    // Subscriptions.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<GitHubWebhookSubscriptionView?> ReadSubscriptionAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // A project this owner may not reach is a 404 rather than an empty subscription, so this read
        // cannot be used to discover another installation's project.
        _ = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        var stored = await subscriptions
            .FindForProjectAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false);

        return stored is null ? null : View(stored);
    }

    /// <inheritdoc />
    public async Task<GitHubWebhookSubscriptionView> SubscribeAsync(
        OwnerScope owner,
        Guid projectId,
        GitHubWebhookSubscribeRequest request,
        long expectedProjectRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        RequireRevision(expectedProjectRevision);

        // The secret is leased and rechecked before it is referenced, so a subscription can only ever
        // hold a reference to a credential of this owner's scope and this provider. The lease is
        // returned by the finally below whatever happens, and its bytes are zeroed on dispose.
        var lease = request.CredentialRef is { } credentialRef
            ? await LeaseWebhookSecretAsync(owner, credentialRef, cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            var now = clock.GetUtcNow();

            var saved = await unitOfWork.ExecuteAsync(
                async token =>
                {
                    var project = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
                        ?? throw new ResourceNotFoundException("project");

                    RequireCurrentRevision(project.Revision, expectedProjectRevision);

                    if (project.ArchivedAt is not null)
                    {
                        throw new ProjectArchivedException();
                    }

                    // A subscription without a connection has nothing to receive deliveries for, and a
                    // row that could exist without one would be a row no route would ever read.
                    var connection = await connections
                        .FindAsync(owner.ScopeId, projectId, token)
                        .ConfigureAwait(false)
                        ?? throw new GitHubCandidateConnectionMissingException();

                    if (lease is null)
                    {
                        return await ClearAsync(owner, projectId, connection.Id, now, token)
                            .ConfigureAwait(false);
                    }

                    var existing = await subscriptions
                        .FindByConnectionAsync(connection.Id, token)
                        .ConfigureAwait(false);

                    var storedSubscription = new GitHubWebhookSubscription(
                        existing?.Id ?? Guid.NewGuid(),
                        owner.ScopeId,
                        connection.Id,
                        projectId,
                        request.CredentialRef!.Value,
                        // Re-subscribing an archived connection reactivates it rather than replacing the
                        // row: past deliveries name this connection, and a row they could no longer find
                        // would leave their receipts orphaned.
                        archivedAt: null,
                        existing?.CreatedAt ?? now,
                        now);

                    var savedSubscription = await subscriptions
                        .UpsertAsync(storedSubscription, token)
                        .ConfigureAwait(false);

                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        SubscriptionUpsertedAction,
                        savedSubscription.Id,
                        expectedProjectRevision,
                        "github webhook subscription stored"));

                    return View(savedSubscription);
                },
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "GitHub webhook subscription stored for connection {ConnectionId} (enabled={Enabled}).",
                saved.ConnectionId,
                saved.Enabled);

            return saved;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<GitHubWebhookSubscriptionView?> UnsubscribeAsync(
        OwnerScope owner,
        Guid projectId,
        long expectedProjectRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        RequireRevision(expectedProjectRevision);

        var now = clock.GetUtcNow();

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var project = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                RequireCurrentRevision(project.Revision, expectedProjectRevision);

                var connection = await connections
                    .FindAsync(owner.ScopeId, projectId, token)
                    .ConfigureAwait(false);

                if (connection is null)
                {
                    // Nothing is subscribed to, which is the state the owner asked for. A 404 here would
                    // send a client looking for a subscription that is exactly what is absent.
                    return null;
                }

                await ClearAsync(owner, projectId, connection.Id, now, token).ConfigureAwait(false);

                var stored = await subscriptions
                    .FindForProjectAsync(owner.ScopeId, projectId, token)
                    .ConfigureAwait(false);

                return stored is null ? null : View(stored);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Archives the subscription, keeping its row and its receipts.
    /// </summary>
    /// <remarks>
    /// An archived subscription stops accepting candidates but is not deleted, because the receipts it
    /// already holds name this connection and are the only record that a delivery ever arrived.
    /// Removing the row would be throwing away history to express a change of mind, and a re-subscribe
    /// afterwards would have nowhere to reactivate.
    /// </remarks>
    private async Task<GitHubWebhookSubscriptionView> ClearAsync(
        OwnerScope owner,
        Guid projectId,
        Guid connectionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var archived = await subscriptions
            .TryArchiveAsync(owner.ScopeId, projectId, now, cancellationToken)
            .ConfigureAwait(false);

        var stored = await subscriptions
            .FindByConnectionAsync(connectionId, cancellationToken)
            .ConfigureAwait(false);

        if (archived)
        {
            audit.Record(new DomainAudit(
                owner.ActorId,
                SubscriptionArchivedAction,
                connectionId,
                now.ToUnixTimeMilliseconds(),
                "github webhook subscription archived"));
        }

        return stored is null
            ? new GitHubWebhookSubscriptionView(connectionId, projectId, Enabled: false, HasSecret: false, now)
            : View(stored);
    }

    // -----------------------------------------------------------------------------------------
    // Reading candidates.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitHubCandidateView>> ListCandidatesAsync(
        OwnerScope owner,
        Guid projectId,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _ = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        // A list without a connection would read as a quiet repository, and the panel could never tell
        // the two empty states apart. The read is refused by name instead, which is what lets the panel
        // say "no repository connected" rather than showing an empty list that looks like a quiet one.
        _ = await connections.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new GitHubCandidateConnectionMissingException();

        var stored = await candidates
            .ListForProjectAsync(owner.ScopeId, projectId, Clamp(limit), cancellationToken)
            .ConfigureAwait(false);

        return [.. stored.Select(View)];
    }

    /// <inheritdoc />
    public async Task<long> CountCandidatesAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // The project is checked on the same terms as the list, so a caller cannot use the count as a
        // way to ask whether another installation's project exists: it is a 404, not a zero. The
        // connection is checked too, for the same reason the list is: a zero for a project with no
        // repository would read as a quiet one.
        _ = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        _ = await connections.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new GitHubCandidateConnectionMissingException();

        return await candidates
            .CountForProjectAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------------------------
    // Asking for a poll.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<GitHubCandidateSyncAccepted> RequestSyncAsync(
        OwnerScope owner,
        Guid projectId,
        GitHubCandidateSyncRequest request,
        long expectedProjectRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > GitHubCandidatePolicy.IdempotencyKeyMaxLength)
        {
            throw new ValidationFailedException(
                "idempotencyKey",
                $"An idempotency key must be 1 to {GitHubCandidatePolicy.IdempotencyKeyMaxLength} characters.");
        }

        RequireRevision(expectedProjectRevision);

        // The project, its revision and its connection are read before a job exists, so a request that
        // could never run is refused here rather than as a failed job an owner has to go and read.
        var prepared = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var project = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                RequireCurrentRevision(project.Revision, expectedProjectRevision);

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                var connection = await connections
                    .FindAsync(owner.ScopeId, projectId, token)
                    .ConfigureAwait(false)
                    ?? throw new GitHubCandidateConnectionMissingException();

                return new PreparedPoll(project.Revision, connection);
            },
            cancellationToken).ConfigureAwait(false);

        // The job binds to the project's revision, which is a signal nothing this package's own work
        // moves: candidates are not milestones, so recording one never advances a project's revision. A
        // project that was archived, renamed or reconnected mid-poll therefore ends the operation
        // superseded rather than proposing against a connection that has since changed.
        var accepted = await operations.EnqueueAsync(
            new EnqueueOperationRequest(
                owner.ScopeId,
                SyncOperationKind,
                projectId,
                prepared.ProjectRevision,
                request.IdempotencyKey.Trim()),
            cancellationToken).ConfigureAwait(false);

        // The run row is written after the job exists, because it names the job. A replay under the same
        // idempotency key gets the original job back and its own insert changes no row.
        var now = clock.GetUtcNow();
        var run = new GitHubCandidateRun(
            Guid.NewGuid(),
            owner.ScopeId,
            accepted.Operation.Id,
            projectId,
            prepared.ProjectRevision,
            prepared.Connection.Owner,
            prepared.Connection.Repo,
            request.Since,
            now);

        var inserted = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var created = await runs.TryInsertAsync(run, token).ConfigureAwait(false);

                if (created)
                {
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        SyncRequestedAction,
                        run.Id,
                        prepared.ProjectRevision,
                        "github candidate poll requested"));
                }

                return created;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "GitHub candidate poll requested for project {ProjectId} as operation {OperationId} ({Created}).",
            projectId,
            accepted.Operation.Id,
            inserted);

        return new GitHubCandidateSyncAccepted(accepted.Operation, accepted.Created);
    }

    /// <inheritdoc />
    public async Task<GitHubCandidatePolled> PollAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // A job is handed an operation id and nothing else, so the window the owner typed is read back
        // from the run row. A job whose run row is missing cannot be carried out honestly and says so
        // rather than guessing at a window nobody asked for.
        var run = await runs
            .FindByOperationAsync(owner.ScopeId, operationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(GitHubCandidateJobError.RequestMissing);

        var connection = await connections
            .FindAsync(owner.ScopeId, run.ProjectId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(GitHubCandidateJobError.ConnectionMissing);

        var repository = RepoRef.Parse(connection.Owner, connection.Repo)
            ?? throw new OperationPermanentException(GitHubCandidateJobError.RepositoryUnavailable);

        // The credential is leased for the duration of the calls that use it and returned by the finally
        // below, whatever happens. The leased bytes become a string for the transport and nothing else:
        // no result, no log line and no exception ever carries it.
        var lease = connection.CredentialRef is { } reference
            ? await LeaseProviderCredentialAsync(owner, reference, cancellationToken).ConfigureAwait(false)
            : null;

        GitHubCandidatePoll polled;
        try
        {
            polled = await source
                .PollAsync(
                    repository,
                    GitHubCandidateRequest.Default(run.Since),
                    lease is null ? null : Encoding.UTF8.GetString(lease.Secret),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }

        ThrowFor(polled);

        var result = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var proposed = 0;
                var written = 0;

                foreach (var release in Within(polled.Releases, run.Since, release => release.PublishedAt))
                {
                    proposed++;
                    written += await RecordAsync(
                        owner.ScopeId,
                        connection.Id,
                        run.ProjectId,
                        GitHubCandidateKinds.Release,
                        GitHubCandidateSourceKeys.Release(release.Id),
                        GitHubCandidatePolicy.Title(release.Name, release.Tag),
                        release.Tag,
                        release.Sha,
                        GitHubCandidateSources.Poll,
                        deliveryId: null,
                        release.PublishedAt ?? polled.FetchedAt,
                        token).ConfigureAwait(false);
                }

                foreach (var tag in polled.Tags)
                {
                    // A tag whose commit could not be resolved is read but not proposed. Its key is
                    // `tag:{name}:{sha}` and a key with no sha in it is not a fact about anything, so
                    // guessing one would produce a candidate nobody can deduplicate or verify. The run's
                    // tag count still says how many were read, so a repository of unresolvable tags is
                    // legible rather than silent.
                    if (string.IsNullOrWhiteSpace(tag.Sha))
                    {
                        continue;
                    }

                    proposed++;
                    written += await RecordAsync(
                        owner.ScopeId,
                        connection.Id,
                        run.ProjectId,
                        GitHubCandidateKinds.Tag,
                        GitHubCandidateSourceKeys.Tag(tag.Name, tag.Sha),
                        GitHubCandidatePolicy.Title(tag.Name, tag.Name),
                        tag.Name,
                        tag.Sha,
                        GitHubCandidateSources.Poll,
                        deliveryId: null,
                        polled.FetchedAt,
                        token).ConfigureAwait(false);
                }

                run.Complete(
                    // The head this poll resolved is a tag's commit, because that is the one read that
                    // actually resolves a name to a commit. A release's `target_commitish` is GitHub's
                    // commitish and is often a branch name rather than a revision, so putting that in a
                    // column that says a sha would be recording a claim this read did not make.
                    polled.Tags.FirstOrDefault(tag => tag.Sha is not null)?.Sha,
                    "GET repos/{repository}/releases; GET repos/{repository}/tags",
                    polled.FetchedAt,
                    polled.Releases.Count,
                    polled.Tags.Count,
                    proposed,
                    written);

                await runs.TryCompleteAsync(run, token).ConfigureAwait(false);

                return new PolledResult(proposed, written);
            },
            cancellationToken).ConfigureAwait(false);

        return new GitHubCandidatePolled(result.Proposed, result.Written, polled.Releases.Count, polled.Tags.Count);
    }

    // -----------------------------------------------------------------------------------------
    // The delivery route's half of the work.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<GitHubWebhookReceipt> ReceiveDeliveryAsync(
        Guid connectionId,
        string? eventName,
        string? deliveryId,
        string? signature,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        // Everything the route can know before it has authenticated anybody is refused in the same
        // words. A caller with a guessed connection id learns nothing about whether it exists.
        if (connectionId == Guid.Empty)
        {
            throw new GitHubCandidateSignatureInvalidException();
        }

        var stored = await subscriptions
            .FindByConnectionAsync(connectionId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            throw new GitHubCandidateSignatureInvalidException();
        }

        SecretLease lease;
        try
        {
            lease = await vault
                .ResolveAsync(SecretRef.To(stored.CredentialRef), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CredentialMissingException)
        {
            // A secret deleted after the subscription was stored is indistinguishable from one that was
            // never stored, which is the honest answer and does not disclose that this row exists.
            throw new GitHubCandidateSignatureInvalidException();
        }

        bool authenticated;
        try
        {
            if (lease.ScopeId != stored.ScopeId ||
                !string.Equals(lease.Provider, WebhookCredentialProvider, StringComparison.Ordinal))
            {
                // A reference that came out of a stored row still has to open as this scope's, this
                // provider's secret. A row moved between scopes does not authenticate.
                throw new GitHubCandidateSignatureInvalidException();
            }

            authenticated = GitHubCandidatePolicy.VerifySignature(
                body.Span,
                signature,
                Encoding.UTF8.GetString(lease.Secret));
        }
        finally
        {
            lease.Dispose();
        }

        if (!authenticated)
        {
            throw new GitHubCandidateSignatureInvalidException();
        }

        // The two header values are bound by pattern rather than null-forgiven afterwards: the policy
        // call is the check, and a check whose result the compiler cannot see is a check that has to be
        // repeated by hand somewhere else.
        if (eventName is not { } rawEvent ||
            deliveryId is not { } rawDelivery ||
            !GitHubCandidatePolicy.IsUsableEventName(rawEvent) ||
            !GitHubCandidatePolicy.IsUsableDeliveryId(rawDelivery))
        {
            throw new GitHubCandidateDeliveryMalformedException(
                "That delivery is signed but does not name an event and a delivery id.");
        }

        var name = rawEvent.Trim();
        var id = rawDelivery.Trim();
        var now = clock.GetUtcNow();

        // The payload is read on the request path so that a body this build cannot read is a 422 on an
        // authenticated delivery rather than a receipt that quietly says "nothing". It costs one parse
        // and no network read: the resolved commit a created tag or a release needs is left to the
        // consumer, because that is a request to GitHub and this route answers 202.
        //
        // The parse happens here rather than after the routing table for a reason the design fixes: a
        // signed body that is not a JSON object is a document this server cannot read at all, and
        // answering `unsupported-action` for it would blame the event name for a payload nobody could
        // see. Readability is settled first, so the 422 means "we could not read this" and the 202 means
        // "we read it and chose not to act on it".
        var reading = Read(name, body, now);

        var receipt = new GitHubWebhookDelivery(
            Guid.NewGuid(),
            stored.ScopeId,
            connectionId,
            stored.ProjectId,
            id,
            name,
            reading.Action,
            GitHubCandidateReceipt.Pending,
            reading.IgnoredReason,
            candidateCount: 0,
            now,
            Serialize(reading.Proposals));

        var accepted = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var stored2 = await deliveries.AcceptAsync(receipt, token).ConfigureAwait(false);

                if (!stored2.Created)
                {
                    // A retry gets the original receipt, unchanged and unrecorded, and never a second
                    // inbox event. The sender learns nothing from the difference, and no candidate is
                    // proposed twice.
                    return stored2;
                }

                // The aggregate is the receipt row's own id, because that is the only identifier the
                // outbox envelope can carry: its aggregate is a GUID and its revision is one or greater,
                // while GitHub's delivery id is a string of no guaranteed shape. Addressing the stored
                // receipt is what lets the consumer read the connection, the delivery id and the scope
                // back from one scoped row instead of trusting a queue message to have carried them.
                await outbox.EnqueueAsync(
                    new OutboxPayload
                    {
                        Kind = InboxOutboxKind,
                        ScopeId = stored.ScopeId,
                        AggregateId = stored2.Delivery.Id,
                        Revision = 1,
                    },
                    cancellationToken: token).ConfigureAwait(false);

                return stored2;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "GitHub delivery {DeliveryId} ({Event}) accepted for connection {ConnectionId}: created={Created}, proposals={ProposalCount}.",
            id,
            name,
            connectionId,
            accepted.Created,
            reading.Proposals.Count);

        return new GitHubWebhookReceipt(
            accepted.Delivery.DeliveryId,
            accepted.Delivery.Event,
            accepted.Delivery.Action,
            accepted.Delivery.Status,
            accepted.Delivery.Reason,
            accepted.Delivery.CandidateCount,
            accepted.Delivery.ReceivedAt,
            Duplicate: !accepted.Created);
    }

    /// <inheritdoc />
    public async Task ProcessInboxAsync(
        Guid scopeId,
        Guid receiptId,
        CancellationToken cancellationToken = default)
    {
        // The receipt is the whole of what this event is about, and it is read scoped: an event naming
        // a receipt of another installation finds nothing and says so.
        var receipt = await deliveries
            .FindByReceiptAsync(scopeId, receiptId, cancellationToken)
            .ConfigureAwait(false);

        if (receipt is null)
        {
            throw new GitHubCandidateDeliveryMalformedException(
                "That inbox event names a delivery this installation did not accept.");
        }

        var connectionId = receipt.ConnectionId;
        var deliveryId = receipt.DeliveryId;

        // A receipt that already carries an outcome is finished. This is the guard that makes the
        // consumer idempotent: the delivered outbox may hand the same event over twice, and a second
        // pass must not propose a candidate the first pass already recorded.
        if (receipt.Status is not GitHubCandidateReceipt.Pending)
        {
            logger.LogInformation(
                "GitHub delivery {DeliveryId} was already {Status}; the inbox event is ignored.",
                deliveryId,
                receipt.Status);

            return;
        }

        var proposals = Deserialize(receipt.ProposalsJson);

        var subscription = await subscriptions
            .FindByConnectionAsync(connectionId, cancellationToken)
            .ConfigureAwait(false);

        // A delivery this build is not collecting from, and one whose payload named nothing, are the
        // two shapes of "acknowledged and nothing proposed", and they are recorded with different
        // reasons on purpose. `connection-inactive` says the owner stopped listening; `nothing-named`
        // says the payload was fine and there was nothing in it worth proposing. Collapsing them would
        // leave an owner who archived a project reading receipts that blame their own repository.
        if (subscription is null)
        {
            await IgnoreAsync(receipt, GitHubCandidateIgnoredReasons.ConnectionInactive, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        var connection = await connections
            .FindAsync(scopeId, subscription.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        if (connection is null)
        {
            await IgnoreAsync(receipt, GitHubCandidateIgnoredReasons.ConnectionInactive, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        var project = await projects
            .FindAsync(scopeId, subscription.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        // An archived project, an archived subscription and a removed connection are all the same answer:
        // acknowledged, and no candidate. A sender that got a refusal here would retry for hours, and an
        // owner who archived a project asked for silence rather than a stream of ignored deliveries.
        if (project is null || project.ArchivedAt is not null || subscription.IsArchived)
        {
            await IgnoreAsync(receipt, GitHubCandidateIgnoredReasons.ConnectionInactive, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        if (proposals.Count == 0)
        {
            // The reason the route already decided, kept: the route read the payload and the consumer
            // only has the decision it stored, so the consumer's own reading of it is a second opinion
            // this package has no reason to form.
            await IgnoreAsync(
                receipt,
                receipt.Reason ?? GitHubCandidateIgnoredReasons.NothingNamed,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        var written = 0;

        // The repository and its credential are read once for the whole delivery rather than per
        // proposal: a payload that names two tags would otherwise lease the secret twice, and a lease is
        // a moment during which a secret exists in this process.
        var repository = RepoRef.Parse(connection.Owner, connection.Repo);
        var lease = repository is null || subscription.CredentialRef == Guid.Empty
            ? null
            : await LeaseWebhookSecretAsync(
                new OwnerScope(scopeId, Guid.Empty),
                subscription.CredentialRef,
                cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var proposal in proposals)
            {
                // A created tag's payload names the ref and not the commit, and a release names only the
                // commitish it was cut from - which GitHub writes as a branch name whenever the release
                // was cut from the default branch. Neither is the immutable revision a candidate's
                // revision column claims to hold, so both are resolved here, on the consumer, off the
                // request path. A ref that does not resolve produces no candidate and the receipt still
                // says the delivery arrived.
                var revision = await ResolveRevisionAsync(
                    repository,
                    proposal,
                    lease,
                    cancellationToken).ConfigureAwait(false);

                if (revision is null)
                {
                    logger.LogInformation(
                        "GitHub delivery {DeliveryId} named a {Kind} whose revision does not resolve; no candidate was recorded.",
                        deliveryId,
                        proposal.Kind);

                    continue;
                }

                // A proposal whose provider identity cannot be keyed produces no candidate: "exactly one
                // candidate per source key" is the promise, and a row with an empty key could not be
                // deduplicated against anything.
                var sourceKey = KeyFor(receipt.Event, proposal, revision);

                if (string.IsNullOrEmpty(sourceKey))
                {
                    logger.LogInformation(
                        "GitHub delivery {DeliveryId} named a release with no usable id; no candidate was recorded.",
                        deliveryId);

                    continue;
                }

                written += await unitOfWork.ExecuteAsync(
                    token => RecordAsync(
                        scopeId,
                        connection.Id,
                        subscription.ProjectId,
                        proposal.Kind,
                        sourceKey,
                        proposal.Title,
                        // A tag's name is the thing a person recognises, and it is also the object the
                        // source key is built from, so it is published for that kind and no other. A
                        // release's identity is its numeric id and its name is a label.
                        tagName: proposal.Kind is GitHubCandidateKinds.Tag ? proposal.ObjectId : null,
                        revision,
                        GitHubCandidateSources.Webhook,
                        receipt.DeliveryId,
                        proposal.OccurredAt,
                        token),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lease?.Dispose();
        }

        // The reason is named even when there is no proposal to blame, and it is named from the closed
        // vocabulary rather than left blank. An earlier spelling passed an empty string here for the
        // zero-proposal case, which the database's own reason constraint refused - so a delivery whose
        // every proposal failed to resolve left its receipt pending and the failure surfaced as a
        // dispatched-but-failed outbox event rather than as an acknowledged delivery. `nothing-named` is
        // the honest answer: the payload did name something, and none of it was something this build can
        // turn into a candidate.
        receipt.RecordCandidates(
            written,
            written > 0 ? string.Empty : GitHubCandidateIgnoredReasons.NothingNamed);
        await deliveries.TryRecordOutcomeAsync(receipt, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "GitHub delivery {DeliveryId} recorded {CandidateCount} new candidate(s).",
            deliveryId,
            written);
    }

    /// <summary>
    /// The immutable revision one proposal's candidate should carry, or <c>null</c> when it names none
    /// that resolves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value is a resolved commit for every kind this package routes from a delivery, and the reason
    /// is the column's own claim. A revision column that held a branch name would be publishing a value
    /// that changes when somebody moves a branch, on a row that is otherwise append-only and attributed
    /// to an instant - so the commitish is resolved before the candidate is written rather than stored
    /// as though it were already a revision.
    /// </para>
    /// <para>
    /// This costs one read per candidate and it is the same read the created-tag case already needed. A
    /// delivery is authenticated, recorded and answered 202 before any of it happens, so the cost is
    /// never on the path GitHub is waiting for, and a ref that does not resolve produces an acknowledged
    /// receipt and no candidate rather than a candidate keyed to a guess.
    /// </para>
    /// </remarks>
    private async Task<string?> ResolveRevisionAsync(
        RepoRef? repository,
        CandidateProposal proposal,
        SecretLease? lease,
        CancellationToken cancellationToken)
    {
        if (repository is null)
        {
            // A connection row whose two names no longer parse cannot address a repository at all, and
            // there is nothing to resolve against. The delivery is still acknowledged, because the
            // receipt is a record that it arrived and not a claim that it could be read.
            return null;
        }

        // A payload that already named an immutable revision - a closed issue's instant, a merge's
        // instant, a crossed star threshold - is its own stable revision and needs no read at all. That
        // is most of the routed events, so the common case costs nothing.
        if (proposal.Kind is not (GitHubCandidateKinds.Tag or GitHubCandidateKinds.Release))
        {
            return proposal.Revision;
        }

        var reference = proposal.Kind is GitHubCandidateKinds.Release
            ? GitHubCandidatePolicy.Bounded(proposal.Revision, 200)
            : proposal.ObjectId;

        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var resolved = await source
            .ResolveRefAsync(
                repository,
                reference,
                lease is null ? null : Encoding.UTF8.GetString(lease.Secret),
                cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
    }
    // -----------------------------------------------------------------------------------------
    // Refusals and helpers.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Turns a transport refusal into the delivered classified job failure.
    /// </summary>
    /// <remarks>
    /// The classification is made once, here, because only this method knows the difference between
    /// "will never work" and "worth another attempt", and guessing wrong either retries a refusal
    /// forever or abandons a throttled response.
    /// </remarks>
    private static void ThrowFor(GitHubCandidatePoll poll)
    {
        ArgumentNullException.ThrowIfNull(poll);

        switch (poll.Outcome)
        {
            case GitHubCandidateOutcome.Collected:
                return;

            case GitHubCandidateOutcome.RepositoryUnavailable:
                throw new OperationPermanentException(GitHubCandidateJobError.RepositoryUnavailable);

            case GitHubCandidateOutcome.CredentialRejected:
                throw new OperationPermanentException(GitHubCandidateJobError.CredentialRejected);

            case GitHubCandidateOutcome.RateLimited:
                throw new OperationRateLimitedException(poll.RetryAfter);

            case GitHubCandidateOutcome.Rejected:
                throw new OperationPermanentException(GitHubCandidateJobError.RequestMissing);

            default:
                throw new OperationTransientException(GitHubCandidateJobError.Unreachable);
        }
    }

    /// <summary>
    /// The reading for one event, with the whole of the routing decision made by the payload reader.
    /// </summary>
    /// <remarks>
    /// <b>One parse, one decision.</b> The reader settles readability, then whether this build routes
    /// the event, then whether it acts on the action, and only then reads the payload's contents. When
    /// this method held the middle step and each per-kind reader re-asked it for itself, an event whose
    /// reader did not ask - an opened issue, a star below the first threshold - was routed and then
    /// proposed, which is the exact failure the design's "only selected event actions" requirement exists
    /// to prevent. Keeping the whole table in one place is what makes a per-kind reader unable to opt out
    /// of it.
    /// </remarks>
    private static GitHubWebhookPayload.Reading Read(
        string name,
        ReadOnlyMemory<byte> body,
        DateTimeOffset now) =>
        GitHubWebhookPayload.Read(body, name, now);

    /// <summary>
    /// Builds the provider's own key for one proposal.
    /// </summary>
    /// <remarks>
    /// A release key is GitHub's release id and nothing else; every other kind is
    /// <c>event:{event}:{objectId}:{revision}</c>. The revision a created tag carries is the resolved
    /// commit, so its key is the design's <c>tag:{name}:{sha}</c> and a tag and a release on one commit
    /// stay two separately attributed candidates rather than collapsing into one.
    /// </remarks>
    private static string KeyFor(string @event, CandidateProposal proposal, string? revision)
    {
        if (@event is "release")
        {
            // A release key is GitHub's own numeric release id and nothing else. The value came from a
            // payload this build already parsed as a number, but the proposals column is read back from
            // a row, and a row is data: a value that is not that number produces no key rather than an
            // unhandled parse failure inside a queue consumer.
            return long.TryParse(proposal.ObjectId, CultureInfo.InvariantCulture, out var releaseId)
                ? GitHubCandidateSourceKeys.Release(releaseId)
                : string.Empty;
        }

        if (proposal.Kind is GitHubCandidateKinds.Tag)
        {
            return GitHubCandidateSourceKeys.Tag(proposal.ObjectId, revision ?? string.Empty);
        }

        return GitHubCandidateSourceKeys.Event(@event, proposal.ObjectId, revision ?? "(none)");
    }

    private async Task<int> RecordAsync(
        Guid scopeId,
        Guid connectionId,
        Guid projectId,
        string kind,
        string sourceKey,
        string title,
        string? tagName,
        string? revision,
        string source,
        string? deliveryId,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        var candidate = new GitHubCandidate(
            Guid.NewGuid(),
            scopeId,
            connectionId,
            projectId,
            sourceKey,
            kind,
            GitHubCandidatePolicy.Title(title, tagName ?? kind),
            GitHubCandidatePolicy.Bounded(revision, 64),
            source,
            GitHubCandidatePolicy.Bounded(deliveryId, GitHubCandidatePolicy.DeliveryIdMaxLength),
            receivedAt);

        var (stored, created) = await candidates.TryRecordAsync(candidate, cancellationToken).ConfigureAwait(false);

        return created ? 1 : 0;
    }

    /// <summary>
    /// Records that a verified delivery produced nothing, and why.
    /// </summary>
    /// <remarks>
    /// The reason is passed rather than read back off the receipt on purpose. The receipt was created
    /// with the reason the <em>route</em> decided, which is the payload's own reading of the delivery;
    /// every reason written from here is a decision the <em>consumer</em> made, and a consumer that
    /// reused the route's reason would be reporting the route's excuse for a decision it had not made.
    /// </remarks>
    private async Task IgnoreAsync(
        GitHubWebhookDelivery receipt,
        string reason,
        CancellationToken cancellationToken)
    {
        receipt.RecordCandidates(0, reason);
        await deliveries.TryRecordOutcomeAsync(receipt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SecretLease> LeaseWebhookSecretAsync(
        OwnerScope owner,
        Guid credentialRef,
        CancellationToken cancellationToken)
    {
        var lease = await vault
            .ResolveAsync(SecretRef.To(credentialRef), cancellationToken)
            .ConfigureAwait(false);

        // The recheck a request body makes: a lease for another scope or another provider is refused
        // before it is stored or used, and the lease is still returned.
        if (lease.ScopeId != owner.ScopeId ||
            !string.Equals(lease.Provider, WebhookCredentialProvider, StringComparison.Ordinal))
        {
            lease.Dispose();

            throw new ValidationFailedException(
                "credentialRef",
                "The named credential is not a stored webhook secret for this installation.");
        }

        return lease;
    }

    private async Task<SecretLease?> LeaseProviderCredentialAsync(
        OwnerScope owner,
        Guid credentialRef,
        CancellationToken cancellationToken)
    {
        var lease = await vault
            .ResolveAsync(new SecretRef(credentialRef), cancellationToken)
            .ConfigureAwait(false);

        // The GitHub token is the delivered package's credential, under the delivered provider name, so
        // this package never invents a second vocabulary for the one row that already holds it.
        if (lease.ScopeId != owner.ScopeId ||
            !string.Equals(lease.Provider, GitHubEvidenceService.CredentialProvider, StringComparison.Ordinal))
        {
            lease.Dispose();

            throw new OperationPermanentException(GitHubCandidateJobError.CredentialRejected);
        }

        return lease;
    }

    private static void RequireRevision(long expectedProjectRevision)
    {
        if (expectedProjectRevision < 0)
        {
            throw new ValidationFailedException("If-Match", "A project revision is zero or greater.");
        }
    }

    private static void RequireCurrentRevision(long stored, long expected)
    {
        if (stored != expected)
        {
            throw new RevisionConflictException(stored);
        }
    }

    private static int Clamp(int? limit) => limit switch
    {
        null or < 1 => DefaultListLimit,
        > MaxListLimit => MaxListLimit,
        var bounded => bounded.Value,
    };

    private static IEnumerable<T> Within<T>(
        IReadOnlyList<T> items,
        DateTimeOffset? since,
        Func<T, DateTimeOffset?> at)
    {
        foreach (var item in items)
        {
            // An entry with no instant of its own is inside any window, because the window excludes
            // entries the provider dated before it and this one was never dated at all.
            if (since is { } floor && at(item) is { } moment && moment < floor)
            {
                continue;
            }

            yield return item;
        }
    }

    private static string Serialize(IReadOnlyList<CandidateProposal> proposals) =>
        JsonSerializer.Serialize(proposals, ProposalWire);

    private static IReadOnlyList<CandidateProposal> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CandidateProposal>>(json, ProposalWire) ?? [];
        }
        catch (JsonException)
        {
            // A proposals column this build cannot read produces nothing rather than something invented,
            // and the receipt still says the delivery arrived.
            return [];
        }
    }

    private static GitHubWebhookSubscriptionView View(GitHubWebhookSubscription stored) =>
        new(
            stored.ConnectionId,
            stored.ProjectId,
            Enabled: !stored.IsArchived,
            HasSecret: true,
            stored.UpdatedAt);

    private static GitHubCandidateView View(GitHubCandidate candidate) =>
        new(
            candidate.Id,
            candidate.ProjectId,
            candidate.SourceKey,
            candidate.Kind,
            candidate.Title,
            candidate.TagName,
            candidate.Revision,
            candidate.Source,
            candidate.DeliveryId,
            candidate.ReceivedAt);

    private sealed record PreparedPoll(long ProjectRevision, GitHubConnection Connection);

    private readonly record struct PolledResult(int Proposed, int Written);
}
