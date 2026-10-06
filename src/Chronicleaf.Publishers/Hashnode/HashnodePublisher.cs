using System.Text;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Publishing;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chronicleaf.Publishers.Hashnode;

/// <summary>
/// The Hashnode reviewed publishing adapter (PUBLISHER-HASHNODE-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>An adapter converts generic content; it updates nothing.</b> It holds no store and receives
/// no store: the snapshot carries everything it may send, the lease carries the one credential it
/// may use, and neither outlives the call. It cannot update a milestone, a bundle, an approval
/// or an attempt, because it cannot reach any of them.
/// </para>
/// <para>
/// <b>Draft stops after the create; publish continues to the publish.</b> The same create call
/// carries the approved title and bytes under the operator's publication in both modes, and
/// only an explicitly confirmed publish continues to <c>publishDraft</c> for the draft this
/// run created: the mode arrives in the command the contract already authorized, so a draft
/// confirmation can never become a published post on the wire.
/// </para>
/// <para>
/// <b>A publish failure keeps its draft.</b> When the create of a publish-mode run succeeded
/// but the publish mutation did not, the result carries the created draft id beside the
/// refusal code - a recoverable remote draft receipt - and no new create is issued: the
/// contract never reissues an attempt, so the second create cannot happen from a retry
/// either. When the publish answer was lost instead, the unknown carries the draft id so the
/// owner knows which draft to look for by hand.
/// </para>
/// <para>
/// <b>Pictures do not travel.</b> The draft call carries Markdown text, not uploads, so
/// approval-covered pictures are named in the preview's warnings rather than sent. An owner who
/// confirms that preview consents to a text-only post, and the warning is hashed into the
/// preview so removing it later stales the confirmation.
/// </para>
/// <para>
/// <b>The preview binds the publication.</b> The publication id travels in the preview's
/// warnings - which the contract hashes. An operator reconfiguration after the owner read the
/// preview changes what a send would do, so the stale confirmation is refused before a job
/// exists rather than as a failed run.
/// </para>
/// </remarks>
public sealed class HashnodePublisher : IPublisherAdapter
{
    /// <summary>The registry id this adapter sends to.</summary>
    public const string TargetId = "hashnode";

    private readonly IOptionsMonitor<HashnodeOptions> targets;
    private readonly HashnodeTransport transport;
    private readonly ILogger<HashnodePublisher> logger;

    public HashnodePublisher(
        IOptionsMonitor<HashnodeOptions> targets,
        HashnodeTransport transport,
        ILogger<HashnodePublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(transport);

        this.targets = targets;
        this.transport = transport;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HashnodePublisher>.Instance;
    }

    /// <inheritdoc />
    string IPublisherAdapter.TargetId => TargetId;

    /// <inheritdoc />
    public PublisherCapabilities Describe() => new(TargetId, SupportsDraft: true, SupportsPublish: true, SupportsReconcile: false);

    /// <inheritdoc />
    public Task<PublishPreview> PrepareAsync(
        ApprovedAssetSnapshot snapshot,
        PublisherTargetConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);

        // The preview is a pure function of the snapshot and the operator's target: it writes
        // nothing remotely, leases no credential and creates no row.
        var target = CurrentTarget();
        var warnings = new List<string> { target.TargetLine() };
        if (snapshot.Pictures.Count > 0)
        {
            warnings.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{snapshot.Pictures.Count} approval-covered picture(s) are not carried to Hashnode; the post is text only."));
        }

        return Task.FromResult(new PublishPreview(
            TargetId,
            snapshot.BodyHash,
            MediaPlan: [],
            warnings.AsReadOnly()));
    }

    /// <inheritdoc />
    public async Task<PublishResult> PublishAsync(
        PublishCommand command,
        ApprovedAssetSnapshot snapshot,
        SecretLease credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(credential);

        var target = CurrentTarget();
        var key = Encoding.UTF8.GetString(credential.Secret.ToArray());
        var published = string.Equals(command.Mode, PublishModes.Publish, StringComparison.Ordinal);

        HashnodeDraftRef? created;

        try
        {
            created = await transport
                .CreateDraftAsync(
                    target.ValidatedPublicationId,
                    snapshot.Title,
                    snapshot.BodyMarkdown,
                    key,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The create may have been committed before the answer was lost, and a create has
            // no reliable idempotency to ask about: unknown, reconciled by hand, never reissued.
            logger.LogInformation(
                "Hashnode publish for operation {OperationId} lost the create answer: unknown until reconciled by hand.",
                command.OperationId);
            throw new PublisherTimeoutException();
        }
        catch (HttpRequestException)
        {
            // A reset connection after the bytes left is indistinguishable from one before they
            // did from inside this process. The safe direction is unknown: it invites a manual
            // check, while failed would invite a second create of the same text.
            logger.LogInformation(
                "Hashnode publish for operation {OperationId} lost the create transport: unknown until reconciled by hand.",
                command.OperationId);
            throw new PublisherTimeoutException();
        }
        catch (HashnodeUpstreamException refused)
        {
            logger.LogInformation(
                "Hashnode publish for operation {OperationId} refused with {ErrorCode} before any draft was created.",
                command.OperationId,
                refused.ErrorCode);
            return new PublishResult(PublishStates.Failed, ErrorCode: refused.ErrorCode);
        }

        if (created is null)
        {
            // Answered 200 in JSON this client does not understand: the draft may exist but
            // its id cannot be named, so the receipt stays unknown with a manual hint rather
            // than becoming a second create.
            logger.LogInformation(
                "Hashnode publish for operation {OperationId} answered without a readable draft reference: unknown until reconciled by hand.",
                command.OperationId);
            return new PublishResult(PublishStates.Unknown);
        }

        if (!published)
        {
            logger.LogInformation(
                "Hashnode publish for operation {OperationId} created draft {RemoteId}.",
                command.OperationId,
                created.Id);

            return new PublishResult(PublishStates.RemoteDraft, RemoteId: created.Id);
        }

        HashnodePostRef? post;

        try
        {
            post = await transport
                .PublishDraftAsync(created.Id, key, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The create of this run is known: the unknown carries its draft id, so the owner
            // knows which remote draft to look for by hand instead of a bare unknown.
            logger.LogInformation(
                "Hashnode publish for operation {OperationId} lost the publish answer for draft {RemoteId}: unknown until reconciled by hand.",
                command.OperationId,
                created.Id);
            throw new PublisherTimeoutException(created.Id);
        }
        catch (HttpRequestException)
        {
            logger.LogInformation(
                "Hashnode publish for operation {OperationId} lost the publish transport for draft {RemoteId}: unknown until reconciled by hand.",
                command.OperationId,
                created.Id);
            throw new PublisherTimeoutException(created.Id);
        }
        catch (HashnodeUpstreamException refused)
        {
            // The publish mutation was refused after this run's create succeeded: the draft
            // stays upstream, so the receipt carries its id beside the refusal - a recoverable
            // remote draft receipt with no new create call.
            logger.LogInformation(
                "Hashnode publish for operation {OperationId} refused with {ErrorCode} after draft {RemoteId} was created.",
                command.OperationId,
                refused.ErrorCode,
                created.Id);
            return new PublishResult(
                PublishStates.Failed, RemoteId: created.Id, ErrorCode: refused.ErrorCode);
        }

        if (post is null)
        {
            // Answered 200 in JSON this client does not understand: the post may exist but
            // its id cannot be named. The draft of this run is known, so the unknown carries
            // it rather than leaving the owner with nothing to look for.
            logger.LogInformation(
                "Hashnode publish for operation {OperationId} answered without a readable post reference for draft {RemoteId}: unknown until reconciled by hand.",
                command.OperationId,
                created.Id);
            return new PublishResult(PublishStates.Unknown, RemoteId: created.Id);
        }

        logger.LogInformation(
            "Hashnode publish for operation {OperationId} published post {RemoteId}.",
            command.OperationId,
            post.Id);

        return new PublishResult(PublishStates.Published, RemoteId: post.Id, RemoteUrl: post.Url);
    }

    /// <inheritdoc />
    public Task<PublishResult> ReconcileAsync(
        PublicationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        // No reliable reconcile exists for a draft-first write with no idempotent query: the
        // provider names no lookup by which an ambiguous write could be resolved, so the
        // receipt stays unknown - carrying the draft id the run knew, when it knew one - and
        // the owner inspects the publication by hand. The contract answers this before calling
        // here (SupportsReconcile is false); a direct call gets the same answer.
        return Task.FromResult(new PublishResult(PublishStates.Unknown, receipt.RemoteId, receipt.RemoteUrl));
    }

    /// <summary>
    /// The operator's target as of this call, re-validated: a reloaded section that no longer
    /// validates is a refusal, never a send under a half-read publication.
    /// </summary>
    private HashnodeOptions CurrentTarget()
    {
        var current = targets.CurrentValue;

        return HashnodeOptions.Create(current.PublicationId);
    }
}
