using System.Globalization;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Content;

/// <summary>
/// The static blog export use cases, over the delivered project, milestone, asset, approval,
/// evidence, media, HTML export and queue contracts (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A request freezes; a run re-resolves; a download re-checks.</b> The request records the
/// per-asset bindings it was accepted against - content revision, body hash, evidence bundle and
/// revision, media selection hash - and the run refuses to build from anything else: a binding
/// that moved ends the job superseded with no completed archive. The download renders
/// deterministically from the still-live state, so an archive served after a revocation is
/// impossible rather than merely unlikely.
/// </para>
/// <para>
/// <b>Nothing here logs prose.</b> The logger receives identifiers, codes and counts. Approved
/// bodies are read from stored revisions and packed into an archive; they never appear in a log
/// line, in an exception message or in an audit detail, because all three outlive the request and
/// are readable more widely than the archive is.
/// </para>
/// </remarks>
public sealed class StaticBlogExportService(
    IProjectStore projects,
    IMilestoneStore milestones,
    IContentAssetStore assets,
    IContentReviewStore reviews,
    IEvidenceStore evidence,
    IMediaStore media,
    IContentExportAdapter htmlExport,
    IStaticBlogExportRequestStore requests,
    IOperationService operations,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<StaticBlogExportService> logger) : IStaticBlogExportService
{
    /// <summary>
    /// The job kind this package owns. The leading segment names the delivered target it binds to:
    /// a project, so the delivered supersede check reads the project's own revision and turns a
    /// project edited mid-run into a superseded operation rather than a commit against a record
    /// somebody else has since changed.
    /// </summary>
    public const string ExportOperationKind = "project.static-blog-export";

    /// <summary>The audit action recorded when an export is asked for.</summary>
    public const string ExportRequestedAction = "static-blog-export.requested";

    /// <summary>The audit action recorded when an export job committed an archive.</summary>
    public const string ExportCommittedAction = "static-blog-export.committed";

    /// <summary>The audit action recorded when an export job ended without an archive.</summary>
    public const string ExportRefusedAction = "static-blog-export.refused";

    private const int MaxIdempotencyKeyLength = 200;

    /// <summary>The prefix every result reference this package commits carries.</summary>
    private const string ResultPrefix = "static-blog-export";

    /// <inheritdoc />
    public async Task<StaticBlogExportAccepted> RequestExportAsync(
        OwnerScope owner,
        Guid projectId,
        StaticBlogExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (projectId == Guid.Empty)
        {
            throw new ValidationFailedException("projectId", "An export names its project, so name one.");
        }

        if (request.ApprovedAssetIds.Count == 0)
        {
            throw new ValidationFailedException(
                "approvedAssetIds",
                "A static site depicts at least one approved post, so name one.");
        }

        if (request.ApprovedAssetIds.Count > StaticBlogSiteBuilder.MaxPosts)
        {
            throw new ValidationFailedException(
                "approvedAssetIds",
                $"A static site depicts at most {StaticBlogSiteBuilder.MaxPosts} posts.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            throw new ValidationFailedException(
                "idempotencyKey",
                $"An idempotency key must be 1 to {MaxIdempotencyKeyLength} characters.");
        }

        // Normalized before anything is decided, so a filesystem path is refused before a job
        // exists that could only ever fail on it.
        var basePath = StaticBlogSiteBuilder.NormalizeBasePath(request.BasePath);

        var project = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        // Every selected asset is resolved against its live approval before a job exists, so a
        // request that could never build is refused here rather than as a failed job an owner has
        // to go and read. The frozen bindings below are what the run compares against.
        var resolved = new List<ResolvedPost>(request.ApprovedAssetIds.Count);
        foreach (var assetId in request.ApprovedAssetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolved.Add(await ResolveLiveAsync(owner, projectId, assetId, cancellationToken).ConfigureAwait(false));
        }

        var orderedIds = resolved
            .Select(post => post.Asset.Id)
            .OrderBy(id => id)
            .ToList();

        if (orderedIds.Distinct().Count() != resolved.Count)
        {
            throw new ValidationFailedException(
                "approvedAssetIds",
                "A static site names each approved asset once, so name each once.");
        }

        var selectionHash = StaticBlogSiteBuilder.SelectionHashFor(orderedIds);

        if (request.ExpectedSelectionHash is not null &&
            !string.Equals(request.ExpectedSelectionHash, selectionHash, StringComparison.Ordinal))
        {
            throw new ValidationFailedException(
                "expectedSelectionHash",
                "The selection changed since it was read. Re-read the approved assets and ask again.");
        }

        var accepted = await operations.EnqueueAsync(
            new EnqueueOperationRequest(
                owner.ScopeId,
                ExportOperationKind,
                projectId,
                project.Revision,
                request.IdempotencyKey.Trim()),
            cancellationToken).ConfigureAwait(false);

        // The request row is written after the job exists, because it names the job. A replay under
        // the same key gets the original job back and its own insert changes no row, so one press
        // of the button can never grow two request rows.
        var row = new StaticBlogExportRequestRow(
            Guid.NewGuid(),
            owner.ScopeId,
            accepted.Operation.Id,
            projectId,
            basePath,
            JsonSerializer.Serialize(orderedIds.Select(id => id.ToString("D")).ToList()),
            selectionHash,
            JsonSerializer.Serialize(resolved
                .OrderBy(post => post.Asset.Id)
                .Select(post => new
                {
                    assetId = post.Asset.Id.ToString("D"),
                    milestoneId = post.Milestone.Id.ToString("D"),
                    contentRevision = post.Revision.Revision,
                    bodyHash = post.Revision.BodyHash,
                    evidenceBundleId = post.Head.Id.ToString("D"),
                    evidenceRevision = post.Head.Revision,
                    mediaSelectionHash = post.Approval.MediaSelectionHash,
                })
                .ToList()),
            clock.GetUtcNow());

        var inserted = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var created = await requests.TryInsertAsync(row, token).ConfigureAwait(false);

                if (created)
                {
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        ExportRequestedAction,
                        projectId,
                        project.Revision,
                        $"export requested;posts={resolved.Count.ToString(CultureInfo.InvariantCulture)};"
                        + $"basePath={basePath};"));
                }

                return created;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Static blog export requested for project {ProjectId} as operation {OperationId} ({Created}).",
            projectId,
            accepted.Operation.Id,
            inserted);

        return new StaticBlogExportAccepted(accepted.Operation.Id, accepted.Created);
    }

    /// <inheritdoc />
    public async Task<StaticBlogExportOutcome> RunAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var row = await requests
            .FindByOperationAsync(owner.ScopeId, operationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Superseded(); // No request row: nothing was authorized, so nothing is built.

        FrozenAssetBinding[] frozen;
        try
        {
            frozen = ParseFrozen(row.FrozenJson);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            throw Superseded(); // Unreadable request row: guessing at a selection would be an archive built from nobody's authorization.
        }

        var project = await projects.FindAsync(owner.ScopeId, row.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw Superseded(); // The exported project is gone, so there is nothing to build from.

        try
        {
            var inputs = new List<SitePostInput>(frozen.Length);

            foreach (var binding in frozen.OrderBy(bound => bound.AssetId))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var live = await ResolveLiveAsync(owner, row.ProjectId, binding.AssetId, cancellationToken)
                    .ConfigureAwait(false);

                // The run builds only from the world the request froze. A live approval that no
                // longer names the frozen revision, body, evidence or pictures is a decision that
                // lapsed while the job was queued, and building from it would be an archive nobody
                // authorized.
                if (live.Revision.Revision != binding.ContentRevision ||
                    !string.Equals(live.Revision.BodyHash, binding.BodyHash, StringComparison.Ordinal) ||
                    live.Head.Id != binding.EvidenceBundleId ||
                    live.Head.Revision != binding.EvidenceRevision ||
                    !string.Equals(live.Approval.MediaSelectionHash, binding.MediaSelectionHash, StringComparison.Ordinal))
                {
                    throw Superseded(); // A binding moved after the request: the archive is withheld, not rebuilt from the moved state.
                }

                var pictures = new List<SitePostImage>(live.Pictures.Count);
                foreach (var (mediaId, caption) in live.Pictures)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pictures.Add(await ReadPictureAsync(owner, mediaId, caption, cancellationToken).ConfigureAwait(false));
                }

                inputs.Add(new SitePostInput(
                    live.Asset.Id,
                    live.Milestone.Id,
                    live.Milestone.Title,
                    live.Milestone.OccurredAt,
                    live.Revision.Revision,
                    live.Revision.BodyHash,
                    live.Head.Revision,
                    live.Revision.BodyMarkdown,
                    pictures));
            }

            var site = await StaticBlogSiteBuilder
                .BuildAsync(inputs, row.BasePath, project.Name, htmlExport, cancellationToken)
                .ConfigureAwait(false);

            await unitOfWork.ExecuteAsync(
                token =>
                {
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        ExportCommittedAction,
                        row.ProjectId,
                        project.Revision,
                        $"export committed;posts={site.PostCount.ToString(CultureInfo.InvariantCulture)};"
                        + $"images={site.ImageCount.ToString(CultureInfo.InvariantCulture)};"));
                    return Task.FromResult(true);
                },
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Static blog export committed for project {ProjectId} as operation {OperationId}.",
                row.ProjectId,
                operationId);

            return new StaticBlogExportOutcome(
                operationId,
                site.ContentSha256,
                site.PostCount,
                site.ImageCount);
        }
        catch (MediaUnavailableException)
        {
            await RecordRefusalAsync(owner, row, project, HtmlExportError.MediaUnavailable, cancellationToken)
                .ConfigureAwait(false);
            throw new OperationPermanentException(HtmlExportError.MediaUnavailable);
        }
        catch (ApiException)
        {
            // Any other refusal at run time means the frozen world moved after the request: a
            // revoked approval, a dismissed milestone, a vanished row. The run ends superseded
            // with no completed archive, and the refusal is recorded with the reason so a reader
            // can see the job did not silently drop anything.
            await RecordRefusalAsync(owner, row, project, StaticBlogError.Superseded, cancellationToken)
                .ConfigureAwait(false);
            throw Superseded();
        }
    }

    /// <inheritdoc />
    public async Task<StaticBlogExportView> ReadExportAsync(
        OwnerScope owner,
        Guid projectId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var operation = await FindExportOperationAsync(owner, projectId, operationId, cancellationToken)
            .ConfigureAwait(false);

        if (operation.Status != OperationStatus.Succeeded ||
            operation.ResultRef is null)
        {
            return new StaticBlogExportView(
                operation.Id,
                OperationStatuses.ToWire(operation.Status),
                operation.ErrorCode,
                null,
                null,
                null);
        }

        var (contentSha, posts, images) = ParseResultRef(operation.ResultRef);

        return new StaticBlogExportView(
            operation.Id,
            OperationStatuses.ToWire(operation.Status),
            null,
            contentSha,
            posts,
            images);
    }

    /// <inheritdoc />
    public async Task<StaticBlogDownload> RenderDownloadAsync(
        OwnerScope owner,
        Guid projectId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var operation = await FindExportOperationAsync(owner, projectId, operationId, cancellationToken)
            .ConfigureAwait(false);

        if (operation.Status != OperationStatus.Succeeded ||
            operation.ResultRef is null)
        {
            // No completed archive exists for this job. A media failure stays a media failure; any
            // other unfinished job is answered as a lapsed authorization, because the download
            // would have required live approvals that are not this job's to re-check.
            if (string.Equals(operation.ErrorCode, HtmlExportError.MediaUnavailable, StringComparison.Ordinal))
            {
                throw new MediaUnavailableException();
            }

            throw new ApprovalStaleException("operation");
        }

        var (contentSha, _, _) = ParseResultRef(operation.ResultRef);

        var row = await requests
            .FindByOperationAsync(owner.ScopeId, operationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ApprovalStaleException("operation");

        var project = await projects.FindAsync(owner.ScopeId, row.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        var frozen = ParseFrozen(row.FrozenJson);
        var inputs = new List<SitePostInput>(frozen.Length);

        foreach (var binding in frozen.OrderBy(bound => bound.AssetId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The same bindings the request froze, re-checked before a single byte is served: an
            // approval revoked after the job completed ends the download, it never serves the
            // archive that approval once authorized.
            var live = await ResolveLiveAsync(owner, row.ProjectId, binding.AssetId, cancellationToken)
                .ConfigureAwait(false);

            if (live.Revision.Revision != binding.ContentRevision ||
                !string.Equals(live.Revision.BodyHash, binding.BodyHash, StringComparison.Ordinal) ||
                live.Head.Id != binding.EvidenceBundleId ||
                live.Head.Revision != binding.EvidenceRevision ||
                !string.Equals(live.Approval.MediaSelectionHash, binding.MediaSelectionHash, StringComparison.Ordinal))
            {
                throw new ApprovalStaleException("evidence");
            }

            var pictures = new List<SitePostImage>(live.Pictures.Count);
            foreach (var (mediaId, caption) in live.Pictures)
            {
                cancellationToken.ThrowIfCancellationRequested();

                MediaContent content;
                try
                {
                    content = await media.OpenAsync(owner, mediaId, cancellationToken).ConfigureAwait(false);
                }
                catch (MediaNotFoundException)
                {
                    throw new MediaUnavailableException();
                }

                await using (content.ConfigureAwait(false))
                {
                    if (!string.Equals(content.Ref.MediaType, MediaTypes.Png, StringComparison.Ordinal))
                    {
                        throw new UnsupportedMediaTypeException();
                    }

                    pictures.Add(new SitePostImage(
                        mediaId,
                        caption,
                        content.Ref.MediaType,
                        await ReadBoundedAsync(content.Content, cancellationToken).ConfigureAwait(false)));
                }
            }

            inputs.Add(new SitePostInput(
                live.Asset.Id,
                live.Milestone.Id,
                live.Milestone.Title,
                live.Milestone.OccurredAt,
                live.Revision.Revision,
                live.Revision.BodyHash,
                live.Head.Revision,
                live.Revision.BodyMarkdown,
                pictures));
        }

        var site = await StaticBlogSiteBuilder
            .BuildAsync(inputs, row.BasePath, project.Name, htmlExport, cancellationToken)
            .ConfigureAwait(false);

        // The served bytes are the committed archive, not a lookalike: the digest of what was
        // rendered now is the digest the job committed, deterministically.
        if (!string.Equals(site.ContentSha256, contentSha, StringComparison.Ordinal))
        {
            throw new InternalFailureException(
                "A completed export could not be re-rendered by this build, so it cannot be served. "
                + "The stored approvals are unchanged.");
        }

        return new StaticBlogDownload(
            string.Create(
                CultureInfo.InvariantCulture,
                $"static-blog-{projectId:N}-{contentSha.Substring(0, 12)}.zip"),
            site.ZipBytes,
            site.ContentSha256);
    }

    // ==============================================================================================
    // Helpers.
    // ==============================================================================================

    /// <summary>
    /// One selected post with the live state the request, run or download must agree on.
    /// </summary>
    private sealed record ResolvedPost(
        ContentAsset Asset,
        Milestone Milestone,
        ContentRevision Revision,
        ContentApproval Approval,
        EvidenceBundle Head,
        IReadOnlyList<(Guid MediaId, string Caption)> Pictures);

    /// <summary>
    /// Resolves one selected asset against its live approval, or refuses with the review
    /// package's own codes: never approved is <c>approval_required</c>, once approved but moved
    /// is <c>approval_stale</c>, and a missing picture is <c>media_unavailable</c>.
    /// </summary>
    private async Task<ResolvedPost> ResolveLiveAsync(
        OwnerScope owner,
        Guid projectId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        if (assetId == Guid.Empty)
        {
            throw new ValidationFailedException("approvedAssetIds", "An export names stored assets, so name them.");
        }

        var asset = await assets.FindAsync(owner.ScopeId, assetId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        // An asset of another project answers the same 404 as a missing one: the selection is
        // bound to the project under export, and a foreign asset is not part of it.
        if (asset.ProjectId != projectId)
        {
            throw new ResourceNotFoundException("content asset");
        }

        var milestone = await milestones.FindAsync(owner.ScopeId, asset.MilestoneId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw UnreadableExportRow();

        // Only a confirmed milestone counts as history, so only a confirmed milestone's story is
        // depicted. Candidates and dismissed records are omitted rather than narrated, which is
        // the timeline's own rule applied to the artifact.
        if (!string.Equals(milestone.Status, MilestoneStatus.Confirmed, StringComparison.Ordinal))
        {
            throw new MilestoneNotConfirmedException(milestone.Status);
        }

        var approvals = await reviews
            .ListForAssetAsync(owner.ScopeId, assetId, cancellationToken)
            .ConfigureAwait(false);

        var live = approvals.FirstOrDefault(approval => approval.IsLive);

        if (live is null)
        {
            if (approvals.Count == 0)
            {
                throw new ApprovalRequiredException();
            }

            throw new ApprovalStaleException("invalidated");
        }

        if (live.ContentRevision != asset.CurrentRevision)
        {
            throw new ApprovalStaleException("revision");
        }

        var stored = await assets
            .FindRevisionAsync(owner.ScopeId, assetId, live.ContentRevision, cancellationToken)
            .ConfigureAwait(false)
            ?? throw UnreadableExportRow();

        if (!string.Equals(ContentRevision.Hash(stored.BodyMarkdown), live.BodyHash, StringComparison.Ordinal) ||
            !string.Equals(stored.BodyHash, live.BodyHash, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("body");
        }

        var head = await evidence
            .FindHeadAsync(owner.ScopeId, asset.MilestoneId, cancellationToken)
            .ConfigureAwait(false);

        if (head is null || head.Id != stored.EvidenceBundleId || head.Revision != live.EvidenceRevision)
        {
            throw new ApprovalStaleException("evidence");
        }

        var pictures = SelectedPictures(head);

        if (!string.Equals(
                ContentApproval.MediaSelectionHashFor(pictures),
                live.MediaSelectionHash,
                StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("media");
        }

        // The bytes must be servable right now, not merely referenced: an approval that binds a
        // picture whose bytes are gone refuses the export rather than serving it picture-less.
        foreach (var (mediaId, _) in pictures)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var content = await media
                    .OpenAsync(owner, mediaId, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.Equals(content.Ref.MediaType, MediaTypes.Png, StringComparison.Ordinal))
                {
                    throw new UnsupportedMediaTypeException();
                }
            }
            catch (MediaNotFoundException)
            {
                throw new MediaUnavailableException();
            }
        }

        return new ResolvedPost(asset, milestone, stored, live, head, pictures);
    }

    /// <summary>
    /// Reads one approval-covered picture's bytes, bounded by the whole site bound.
    /// </summary>
    private async Task<SitePostImage> ReadPictureAsync(
        OwnerScope owner,
        Guid mediaId,
        string caption,
        CancellationToken cancellationToken)
    {
        MediaContent content;
        try
        {
            content = await media.OpenAsync(owner, mediaId, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaNotFoundException)
        {
            throw new MediaUnavailableException();
        }

        await using (content.ConfigureAwait(false))
        {
            if (!string.Equals(content.Ref.MediaType, MediaTypes.Png, StringComparison.Ordinal))
            {
                throw new UnsupportedMediaTypeException();
            }

            return new SitePostImage(
                mediaId,
                caption,
                content.Ref.MediaType,
                await ReadBoundedAsync(content.Content, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Reads a media stream up to the site bound. A picture larger than the whole site could ever
    /// be is refused before its bytes are all held, rather than after.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(
        System.IO.Stream content,
        CancellationToken cancellationToken)
    {
        using var buffered = new System.IO.MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;

        while ((read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > StaticBlogSiteBuilder.MaxSiteBytes)
            {
                throw new PayloadTooLargeException(StaticBlogSiteBuilder.MaxSiteBytes);
            }

            buffered.Write(chunk, 0, read);
        }

        return buffered.ToArray();
    }

    private async Task<Operation> FindExportOperationAsync(
        OwnerScope owner,
        Guid projectId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        Operation operation;
        try
        {
            operation = await operations.GetAsync(owner.ScopeId, operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationNotFoundException)
        {
            throw new ResourceNotFoundException("static blog export");
        }

        // A job of another kind, or for another project, answers the same 404 as a missing one.
        if (!string.Equals(operation.Kind, ExportOperationKind, StringComparison.Ordinal) ||
            operation.TargetId != projectId)
        {
            throw new ResourceNotFoundException("static blog export");
        }

        return operation;
    }

    private static FrozenAssetBinding[] ParseFrozen(string frozenJson)
    {
        using var document = JsonDocument.Parse(frozenJson);
        var bindings = new List<FrozenAssetBinding>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            bindings.Add(new FrozenAssetBinding(
                Guid.Parse(element.GetProperty("assetId").GetString()!),
                Guid.Parse(element.GetProperty("milestoneId").GetString()!),
                element.GetProperty("contentRevision").GetInt64(),
                element.GetProperty("bodyHash").GetString()!,
                Guid.Parse(element.GetProperty("evidenceBundleId").GetString()!),
                element.GetProperty("evidenceRevision").GetInt64(),
                element.GetProperty("mediaSelectionHash").GetString()!));
        }

        if (bindings.Count == 0)
        {
            throw new FormatException("An export request names at least one asset.");
        }

        return [.. bindings];
    }

    private static (string ContentSha, int Posts, int Images) ParseResultRef(string resultRef)
    {
        var segments = resultRef.Split(':');
        if (segments.Length != 4 ||
            !string.Equals(segments[0], ResultPrefix, StringComparison.Ordinal) ||
            segments[1].Length != 64 ||
            !int.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var posts) ||
            !int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var images))
        {
            throw new InternalFailureException(
                "A completed export could not be read by this build, so it cannot be served. "
                + "The stored approvals are unchanged.");
        }

        return (segments[1], posts, images);
    }

    /// <summary>The result reference the worker commits: the manifest digest and the depicted counts.</summary>
    internal static string ResultRefFor(string contentSha, int posts, int images) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ResultPrefix}:{contentSha}:{posts.ToString(CultureInfo.InvariantCulture)}:{images.ToString(CultureInfo.InvariantCulture)}");

    private async Task RecordRefusalAsync(
        OwnerScope owner,
        StaticBlogExportRequestRow row,
        Project project,
        string code,
        CancellationToken cancellationToken)
    {
        await unitOfWork.ExecuteAsync(
            token =>
            {
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ExportRefusedAction,
                    row.ProjectId,
                    project.Revision,
                    $"export refused;code={code};"));
                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Static blog export refused for project {ProjectId} as operation {OperationId} with {Code}.",
            row.ProjectId,
            row.OperationId,
            code);
    }

    /// <summary>
    /// The selected pictures of one evidence head, as the media hash binds them: the same rule
    /// the review package approves against, so the two cannot disagree about what an approval
    /// covers.
    /// </summary>
    private static IReadOnlyList<(Guid MediaId, string Caption)> SelectedPictures(EvidenceBundle head)
    {
        ArgumentNullException.ThrowIfNull(head);

        return [.. head.Items
            .Where(item => string.Equals(item.Kind, EvidenceKinds.Screenshot, StringComparison.Ordinal)
                && item.MediaId is not null
                && string.Equals(item.Availability, EvidenceAvailability.Complete, StringComparison.Ordinal))
            .Select(item => (item.MediaId!.Value, item.Text))];
    }

    private static OperationPermanentException Superseded() =>
        new(StaticBlogError.Superseded);

    /// <summary>
    /// A stored row this build cannot read (500).
    /// </summary>
    private static ApiException UnreadableExportRow() =>
        new InternalFailureException(
            "A stored export record could not be read by this build, so it cannot be exported. "
            + "The record itself is unchanged.");
}
