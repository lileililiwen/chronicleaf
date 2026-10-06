using System.Text;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Publishing;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chronicleaf.Publishers.CustomBlog;

/// <summary>
/// The custom-blog reviewed publishing adapter (PUBLISHER-CUSTOM-BLOG-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>An adapter converts generic content; it updates nothing.</b> It holds no store and receives
/// no store: the snapshot carries everything it may send, the lease carries the one credential it
/// may use, and neither outlives the call. It cannot update a milestone, a bundle, an approval
/// or an attempt, because it cannot reach any of them.
/// </para>
/// <para>
/// <b>One create carries the mode.</b> A draft-mode run and a publish-mode run send the same
/// <c>POST /chronicleaf/v1/posts</c> with the same title, Markdown and operation id under the
/// operator's origin; only <c>mode</c> differs - <c>draft</c> for a draft, <c>publish</c>
/// for an explicitly confirmed publish. The mode arrives in the command the contract already
/// authorized, so a draft confirmation can never become a published post on the wire.
/// </para>
/// <para>
/// <b>The post is Markdown text.</b> The sent body is the approved revision's exact stored
/// bytes - no HTML render, no pictures: approval-covered pictures are named in the preview's
/// warnings rather than sent, because this contract carries no private media URLs and the
/// media manifest travels empty. An owner who confirms that preview consents to a text-only
/// post, and the warning is hashed into the preview so removing it later stales the
/// confirmation.
/// </para>
/// <para>
/// <b>The preview binds the origin, the allowlist and the sent bytes.</b> The target line
/// names the configured origin, the format allowlist and the exact byte count of the
/// Markdown a send would carry - which the contract hashes. An operator reconfiguration
/// after the owner read the preview changes what a send would do, so the stale confirmation
/// is refused before a job exists rather than as a failed run.
/// </para>
/// </remarks>
public sealed class CustomBlogPublisher : IPublisherAdapter
{
    /// <summary>The registry id this adapter sends to.</summary>
    public const string TargetId = "custom-blog";

    /// <summary>The post mode a draft-mode run sends.</summary>
    public const string DraftMode = "draft";

    /// <summary>The post mode a publish-mode run sends.</summary>
    public const string PublishedMode = "publish";

    private readonly IOptionsMonitor<CustomBlogOptions> targets;
    private readonly CustomBlogTransport transport;
    private readonly ILogger<CustomBlogPublisher> logger;

    public CustomBlogPublisher(
        IOptionsMonitor<CustomBlogOptions> targets,
        CustomBlogTransport transport,
        ILogger<CustomBlogPublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(transport);

        this.targets = targets;
        this.transport = transport;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CustomBlogPublisher>.Instance;
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
        // nothing remotely, leases no credential, probes no capabilities and creates no row.
        // The sent bytes are the approved Markdown verbatim, so the bound count is its length.
        var target = CurrentTarget();
        var markdownBytes = Encoding.UTF8.GetByteCount(snapshot.BodyMarkdown);
        var warnings = new List<string>
        {
            target.TargetLine(markdownBytes),
        };

        if (snapshot.Pictures.Count > 0)
        {
            warnings.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{snapshot.Pictures.Count} approval-covered picture(s) are not carried to the custom blog; the post is Markdown text only."));
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

        // The API key is the vault's verbatim bytes: it travels as one bearer credential and
        // is returned with the lease before the call returns.
        var apiKey = Encoding.UTF8.GetString(credential.Secret.ToArray());

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogInformation(
                "Custom-blog publish for operation {OperationId} refused: the leased credential names no key.",
                command.OperationId);
            return new PublishResult(PublishStates.Failed, ErrorCode: CustomBlogError.CredentialRejected);
        }

        var published = string.Equals(command.Mode, PublishModes.Publish, StringComparison.Ordinal);

        CustomBlogPostRef? created;

        try
        {
            created = await transport
                .CreatePostAsync(
                    target.ValidatedOrigin,
                    target.ParsedFormats,
                    apiKey,
                    command.OperationId,
                    snapshot.BodyHash,
                    snapshot.Title,
                    snapshot.BodyMarkdown,
                    published ? PublishedMode : DraftMode,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The create may have been committed before the answer was lost, and a create has
            // no reliable idempotency to ask about from here - the server's operations
            // endpoint is for a human reconciliation by hand, carrying the operation id the
            // accept response already named. Unknown, never reissued.
            logger.LogInformation(
                "Custom-blog publish for operation {OperationId} lost the create answer: unknown until reconciled by hand.",
                command.OperationId);
            throw new PublisherTimeoutException();
        }
        catch (HttpRequestException)
        {
            // A reset connection after the bytes left is indistinguishable from one before they
            // did from inside this process. The safe direction is unknown: it invites a manual
            // check, while failed would invite a second create of the same post.
            logger.LogInformation(
                "Custom-blog publish for operation {OperationId} lost the create transport: unknown until reconciled by hand.",
                command.OperationId);
            throw new PublisherTimeoutException();
        }
        catch (CustomBlogUpstreamException refused)
        {
            logger.LogInformation(
                "Custom-blog publish for operation {OperationId} refused with {ErrorCode} before any post was created.",
                command.OperationId,
                refused.ErrorCode);
            return new PublishResult(PublishStates.Failed, ErrorCode: refused.ErrorCode);
        }

        if (created is null)
        {
            // Answered success in JSON this client does not understand, or a reference that
            // cannot be stored: the post may exist but its id cannot be named, so the receipt
            // stays unknown with a manual hint rather than becoming a second create.
            logger.LogInformation(
                "Custom-blog publish for operation {OperationId} answered without a readable post reference: unknown until reconciled by hand.",
                command.OperationId);
            return new PublishResult(PublishStates.Unknown);
        }

        if (!published)
        {
            logger.LogInformation(
                "Custom-blog publish for operation {OperationId} created draft post {RemoteId}.",
                command.OperationId,
                created.Id);

            return new PublishResult(PublishStates.RemoteDraft, RemoteId: created.Id, RemoteUrl: created.Url);
        }

        logger.LogInformation(
            "Custom-blog publish for operation {OperationId} published post {RemoteId}.",
            command.OperationId,
            created.Id);

        return new PublishResult(PublishStates.Published, RemoteId: created.Id, RemoteUrl: created.Url);
    }

    /// <inheritdoc />
    public Task<PublishResult> ReconcileAsync(
        PublicationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        // No reliable reconcile exists for a create-only write from here: the reconciler
        // holds no credential - the contract calls it with the receipt alone - so the
        // server's operations endpoint is unreachable and the receipt stays unknown. The
        // contract answers this before calling here (SupportsReconcile is false); a direct
        // call gets the same answer, carrying the post id the run knew, when it knew one.
        return Task.FromResult(new PublishResult(PublishStates.Unknown, receipt.RemoteId, receipt.RemoteUrl));
    }

    /// <summary>
    /// The operator's target as of this call, re-validated: a reloaded section that no longer
    /// validates is a refusal, never a send under a half-read origin.
    /// </summary>
    private CustomBlogOptions CurrentTarget()
    {
        var current = targets.CurrentValue;

        return CustomBlogOptions.Create(current.Origin, current.Formats);
    }
}
