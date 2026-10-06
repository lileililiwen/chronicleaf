using System.Text;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Publishing;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chronicleaf.Publishers.WordPress;

/// <summary>
/// The WordPress reviewed publishing adapter (PUBLISHER-WORDPRESS-ADAPTER-001/002).
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
/// <c>POST /wp-json/wp/v2/posts</c> with the same title, HTML, slug and taxonomy under the
/// operator's origin; only <c>status</c> differs - <c>draft</c> for a draft, <c>publish</c>
/// for an explicitly confirmed publish. The mode arrives in the command the contract already
/// authorized, so a draft confirmation can never become a published post on the wire.
/// </para>
/// <para>
/// <b>The post is HTML text.</b> The sent body is the shared HTML export's sanitized article -
/// the same subset the review preview shows - rendered from the approved bytes with no
/// pictures: approval-covered pictures are named in the preview's warnings rather than
/// sent, because the provider has no endpoint that could receive local bytes inside a post
/// create. An owner who confirms that preview consents to a text-only post, and the warning
/// is hashed into the preview so removing it later stales the confirmation.
/// </para>
/// <para>
/// <b>The preview binds the origin, the user and the rendered bytes.</b> The target line
/// names the configured origin, the user, the slug, the taxonomy and the exact byte count
/// of the HTML a send would carry - which the contract hashes. An operator reconfiguration
/// after the owner read the preview changes what a send would do, so the stale confirmation
/// is refused before a job exists rather than as a failed run.
/// </para>
/// </remarks>
public sealed class WordPressPublisher : IPublisherAdapter
{
    /// <summary>The registry id this adapter sends to.</summary>
    public const string TargetId = "wordpress";

    /// <summary>The post status a draft-mode run sends.</summary>
    public const string DraftStatus = "draft";

    /// <summary>The post status a publish-mode run sends.</summary>
    public const string PublishedStatus = "publish";

    /// <summary>The article markers the sent body is read between: the shared export's fixed template.</summary>
    private const string ArticleOpen = "<article>\n";

    /// <summary>The article marker the sent body ends at: the shared export's fixed template.</summary>
    private const string ArticleClose = "</article>";

    private readonly IOptionsMonitor<WordPressOptions> targets;
    private readonly WordPressTransport transport;
    private readonly IContentExportAdapter htmlExport;
    private readonly ILogger<WordPressPublisher> logger;

    public WordPressPublisher(
        IOptionsMonitor<WordPressOptions> targets,
        WordPressTransport transport,
        IContentExportAdapter htmlExport,
        ILogger<WordPressPublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(htmlExport);

        this.targets = targets;
        this.transport = transport;
        this.htmlExport = htmlExport;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WordPressPublisher>.Instance;
    }

    /// <inheritdoc />
    string IPublisherAdapter.TargetId => TargetId;

    /// <inheritdoc />
    public PublisherCapabilities Describe() => new(TargetId, SupportsDraft: true, SupportsPublish: true, SupportsReconcile: false);

    /// <inheritdoc />
    public async Task<PublishPreview> PrepareAsync(
        ApprovedAssetSnapshot snapshot,
        PublisherTargetConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);

        // The preview is a pure function of the snapshot and the operator's target: it writes
        // nothing remotely, leases no credential and creates no row. Rendering here is CPU only.
        var target = CurrentTarget();
        var html = await RenderPostHtmlAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>
        {
            target.TargetLine(Encoding.UTF8.GetByteCount(html)),
        };

        if (snapshot.Pictures.Count > 0)
        {
            warnings.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{snapshot.Pictures.Count} approval-covered picture(s) are not carried to WordPress; the post is HTML text only."));
        }

        return new PublishPreview(
            TargetId,
            snapshot.BodyHash,
            MediaPlan: [],
            warnings.AsReadOnly());
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

        // The application password is the vault's verbatim bytes: interior spaces are the
        // provider's own grouping, so they travel untouched rather than trimmed.
        var password = Encoding.UTF8.GetString(credential.Secret.ToArray());

        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogInformation(
                "WordPress publish for operation {OperationId} refused: the leased credential names no password.",
                command.OperationId);
            return new PublishResult(PublishStates.Failed, ErrorCode: WordPressError.CredentialRejected);
        }

        var published = string.Equals(command.Mode, PublishModes.Publish, StringComparison.Ordinal);
        var html = await RenderPostHtmlAsync(snapshot, cancellationToken).ConfigureAwait(false);

        WordPressPostRef? created;

        try
        {
            created = await transport
                .CreatePostAsync(
                    target.ValidatedOrigin,
                    target.ValidatedUsername,
                    snapshot.Title,
                    html,
                    published ? PublishedStatus : DraftStatus,
                    target.ValidatedSlug,
                    target.ParsedCategories,
                    target.ParsedTags,
                    password,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The create may have been committed before the answer was lost, and a create has
            // no reliable idempotency to ask about: unknown, reconciled by hand, never reissued.
            logger.LogInformation(
                "WordPress publish for operation {OperationId} lost the create answer: unknown until reconciled by hand.",
                command.OperationId);
            throw new PublisherTimeoutException();
        }
        catch (HttpRequestException)
        {
            // A reset connection after the bytes left is indistinguishable from one before they
            // did from inside this process. The safe direction is unknown: it invites a manual
            // check, while failed would invite a second create of the same post.
            logger.LogInformation(
                "WordPress publish for operation {OperationId} lost the create transport: unknown until reconciled by hand.",
                command.OperationId);
            throw new PublisherTimeoutException();
        }
        catch (WordPressUpstreamException refused)
        {
            logger.LogInformation(
                "WordPress publish for operation {OperationId} refused with {ErrorCode} before any post was created.",
                command.OperationId,
                refused.ErrorCode);
            return new PublishResult(PublishStates.Failed, ErrorCode: refused.ErrorCode);
        }

        if (created is null)
        {
            // Answered success in JSON this client does not understand: the post may exist but
            // its id cannot be named, so the receipt stays unknown with a manual hint rather
            // than becoming a second create.
            logger.LogInformation(
                "WordPress publish for operation {OperationId} answered without a readable post reference: unknown until reconciled by hand.",
                command.OperationId);
            return new PublishResult(PublishStates.Unknown);
        }

        if (!published)
        {
            logger.LogInformation(
                "WordPress publish for operation {OperationId} created draft post {RemoteId}.",
                command.OperationId,
                created.Id);

            return new PublishResult(PublishStates.RemoteDraft, RemoteId: created.Id, RemoteUrl: created.Url);
        }

        logger.LogInformation(
            "WordPress publish for operation {OperationId} published post {RemoteId}.",
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

        // No reliable reconcile exists for a create-only write with no idempotent query: the
        // reconciler holds no credential - the contract calls it with the receipt alone - so
        // an authenticated read is unreachable and the receipt stays unknown. The contract
        // answers this before calling here (SupportsReconcile is false); a direct call gets
        // the same answer, carrying the post id the run knew, when it knew one.
        return Task.FromResult(new PublishResult(PublishStates.Unknown, receipt.RemoteId, receipt.RemoteUrl));
    }

    /// <summary>
    /// The operator's target as of this call, re-validated: a reloaded section that no longer
    /// validates is a refusal, never a send under a half-read origin.
    /// </summary>
    private WordPressOptions CurrentTarget()
    {
        var current = targets.CurrentValue;

        return WordPressOptions.Create(current.Origin, current.Username, current.Slug, current.Categories, current.Tags);
    }

    /// <summary>
    /// Renders the approved snapshot to the exact article body a send carries: the shared
    /// export's sanitized document for the approved text with no pictures, read between the
    /// template's own article markers.
    /// </summary>
    /// <remarks>
    /// The snapshot is re-wrapped with an empty picture set on purpose: the export renders
    /// approval-covered pictures as a packaged section addressing relative paths that name
    /// nothing upstream, so the sent body renders the text the owner approved with no picture
    /// section at all - exactly what the preview's warning describes.
    /// </remarks>
    private async Task<string> RenderPostHtmlAsync(
        ApprovedAssetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var textOnly = new ApprovedAssetSnapshot(
            snapshot.AssetId,
            snapshot.ContentRevision,
            snapshot.Title,
            snapshot.BodyMarkdown,
            snapshot.BodyHash,
            snapshot.EvidenceRevision,
            []);

        var artifact = await htmlExport.ExportAsync(textOnly, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(artifact.MediaType, HtmlExportAdapter.HtmlMediaType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The text-only render is a single document, so it cannot be published as anything else.");
        }

        var document = Encoding.UTF8.GetString(artifact.Bytes);
        var open = document.IndexOf(ArticleOpen, StringComparison.Ordinal);

        if (open < 0)
        {
            throw new InvalidOperationException(
                "The rendered document carries no article, so it cannot be published.");
        }

        var close = document.IndexOf(ArticleClose, open + ArticleOpen.Length, StringComparison.Ordinal);

        if (close < 0)
        {
            throw new InvalidOperationException(
                "The rendered document never closes its article, so it cannot be published.");
        }

        return document.Substring(open + ArticleOpen.Length, close - open - ArticleOpen.Length);
    }
}
