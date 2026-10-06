using System.Text;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Publishing;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chronicleaf.Publishers.DevTo;

/// <summary>
/// The Dev.to reviewed publishing adapter (PUBLISHER-DEVTO-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>An adapter converts generic content; it updates nothing.</b> It holds no store and receives
/// no store: the snapshot carries everything it may send, the lease carries the one credential it
/// may use, and neither outlives the call. It cannot update a milestone, a bundle, an approval
/// or an attempt, because it cannot reach any of them.
/// </para>
/// <para>
/// <b>Draft is a boolean, not a target.</b> The same create call carries
/// <c>published: false</c> for a draft and <c>published: true</c> only for an explicitly
/// confirmed publish: the mode arrives in the command the contract already authorized, so a
/// draft confirmation can never become a published article on the wire.
/// </para>
/// <para>
/// <b>Pictures do not travel.</b> The Forem article call carries Markdown text, not uploads, so
/// approval-covered pictures are named in the preview's warnings rather than sent. An owner who
/// confirms that preview consents to a text-only article, and the warning is hashed into the
/// preview so removing it later stales the confirmation.
/// </para>
/// <para>
/// <b>The preview binds the tag configuration.</b> The tag line travels in the preview's
/// warnings - which the contract hashes. An operator reconfiguration after the owner read the
/// preview changes what a send would do, so the stale confirmation is refused before a job
/// exists rather than as a failed run.
/// </para>
/// </remarks>
public sealed class DevToPublisher : IPublisherAdapter
{
    /// <summary>The registry id this adapter sends to.</summary>
    public const string TargetId = "devto";

    private readonly IOptionsMonitor<DevToOptions> targets;
    private readonly DevToTransport transport;
    private readonly ILogger<DevToPublisher> logger;

    public DevToPublisher(
        IOptionsMonitor<DevToOptions> targets,
        DevToTransport transport,
        ILogger<DevToPublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(transport);

        this.targets = targets;
        this.transport = transport;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DevToPublisher>.Instance;
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
                $"{snapshot.Pictures.Count} approval-covered picture(s) are not carried to Dev.to; the article is text only."));
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

        DevToArticleRef? created;

        try
        {
            created = await transport
                .CreateArticleAsync(
                    snapshot.Title,
                    snapshot.BodyMarkdown,
                    published,
                    target.ParsedTags,
                    key,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The create may have been committed before the answer was lost, and a create has
            // no reliable idempotency to ask about: unknown, reconciled by hand, never reissued.
            logger.LogInformation(
                "Dev.to publish for operation {OperationId} lost the create answer: unknown until reconciled by hand.",
                command.OperationId);
            throw new PublisherTimeoutException();
        }
        catch (HttpRequestException)
        {
            // A reset connection after the bytes left is indistinguishable from one before they
            // did from inside this process. The safe direction is unknown: it invites a manual
            // check, while failed would invite a second create of the same text.
            logger.LogInformation(
                "Dev.to publish for operation {OperationId} lost the create transport: unknown until reconciled by hand.",
                command.OperationId);
            throw new PublisherTimeoutException();
        }
        catch (DevToUpstreamException refused)
        {
            logger.LogInformation(
                "Dev.to publish for operation {OperationId} refused with {ErrorCode} before any article was created.",
                command.OperationId,
                refused.ErrorCode);
            return new PublishResult(PublishStates.Failed, ErrorCode: refused.ErrorCode);
        }

        if (created is null)
        {
            // Answered 201 in JSON this client does not understand: the article exists but its
            // id cannot be named, so the receipt stays unknown with a manual hint rather than
            // becoming a second create.
            logger.LogInformation(
                "Dev.to publish for operation {OperationId} answered created without a readable reference: unknown until reconciled by hand.",
                command.OperationId);
            return new PublishResult(PublishStates.Unknown);
        }

        var state = published ? PublishStates.Published : PublishStates.RemoteDraft;

        logger.LogInformation(
            "Dev.to publish for operation {OperationId} created article {RemoteId}.",
            command.OperationId,
            created.Id);

        return new PublishResult(state, RemoteId: created.Id, RemoteUrl: created.Url);
    }

    /// <inheritdoc />
    public Task<PublishResult> ReconcileAsync(
        PublicationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        // No reliable reconcile exists for a create with no idempotency: the provider names no
        // query by which an ambiguous write could be resolved, so the receipt stays unknown and
        // the owner inspects the article list by hand. The contract answers this before calling
        // here (SupportsReconcile is false); a direct call gets the same answer.
        return Task.FromResult(new PublishResult(PublishStates.Unknown, receipt.RemoteId, receipt.RemoteUrl));
    }

    /// <summary>
    /// The operator's target as of this call, re-validated: a reloaded section that no longer
    /// validates is a refusal, never a send under half-read tags.
    /// </summary>
    private DevToOptions CurrentTarget()
    {
        var current = targets.CurrentValue;

        return DevToOptions.Create(current.Tags);
    }
}
