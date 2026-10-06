using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Journey;

/// <summary>
/// The public project journey use cases, over the delivered project, milestone, asset, approval,
/// evidence and media contracts (PUBLIC-PROJECT-JOURNEY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A preview computes; a publication freezes; a public read re-checks.</b> The preview
/// resolves a selection against the live approvals and reports the hash publication must carry.
/// Publication re-resolves the same selection and refuses anything that moved, so only
/// explicitly published milestones and entries ever appear. The anonymous read serves the
/// frozen snapshot only while every binding still describes the stored state: a revocation or
/// a lapsed approval suspends it as a 404 rather than serving text nobody authorized.
/// </para>
/// <para>
/// <b>Nothing here logs prose.</b> The logger receives identifiers, codes and counts. Approved
/// bodies are read from stored revisions and served to anonymous readers; they never appear in
/// a log line, in an exception message or in an audit detail, because all three outlive the
/// request and are readable more widely than the projection is.
/// </para>
/// </remarks>
public sealed class ProjectJourneyService(
    IProjectStore projects,
    IMilestoneStore milestones,
    IContentAssetStore assets,
    IContentReviewStore reviews,
    IEvidenceStore evidence,
    IMediaStore media,
    IProjectJourneyStore journeys,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ProjectJourneyService> logger) : IProjectJourneyService
{
    /// <summary>The audit action recorded when a journey is published or republished.</summary>
    public const string JourneyPublishedAction = "project-journey.published";

    /// <summary>The audit action recorded when a journey is revoked.</summary>
    public const string JourneyRevokedAction = "project-journey.revoked";

    /// <summary>At most this many entries one journey depicts; a projection is curated, not complete.</summary>
    public const int MaxEntries = 50;

    /// <summary>Slugs are 3 to 80 characters of lowercase kebab case.</summary>
    public const int SlugMinLength = 3;

    /// <summary>Slugs are 3 to 80 characters of lowercase kebab case.</summary>
    public const int SlugMaxLength = 80;

    private static readonly Regex SlugPattern = new(
        "^[a-z0-9]+(-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<JourneyPreview> PreviewAsync(
        OwnerScope owner,
        Guid projectId,
        JourneyPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (projectId == Guid.Empty)
        {
            throw new ValidationFailedException("projectId", "A journey preview names its project, so name one.");
        }

        _ = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        var resolved = await ResolveSelectionAsync(owner, projectId, request.ApprovedAssetIds, cancellationToken)
            .ConfigureAwait(false);

        return new JourneyPreview(
            StaticBlogSiteBuilder.SelectionHashFor(resolved.Select(post => post.Asset.Id)),
            [.. resolved.Select(ToPreviewEntry)]);
    }

    /// <inheritdoc />
    public async Task<JourneyView> PublishAsync(
        OwnerScope owner,
        Guid projectId,
        JourneyPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (projectId == Guid.Empty)
        {
            throw new ValidationFailedException("projectId", "A journey names its project, so name one.");
        }

        var project = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        var stored = await journeys.FindByProjectAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is not null && request.ExpectedRevision is null)
        {
            throw new IfMatchRequiredException("journey");
        }

        if (stored is null && request.ExpectedRevision is > 0)
        {
            throw new RevisionConflictException(0);
        }

        if (stored is not null && request.ExpectedRevision != stored.Revision)
        {
            throw new RevisionConflictException(stored.Revision);
        }

        if (!request.Enabled)
        {
            return await RevokeAsync(owner, project, stored, request.ExpectedRevision, cancellationToken)
                .ConfigureAwait(false);
        }

        // Enabling: the selection is re-resolved against the live approvals first, so a request
        // that could never be served is refused before a row exists. The frozen snapshot below is
        // what the public read compares against.
        if (request.ApprovedAssetIds is null || request.ApprovedAssetIds.Count == 0)
        {
            throw new ValidationFailedException(
                "approvedAssetIds",
                "A published journey depicts at least one approved entry, so name one.");
        }

        if (string.IsNullOrWhiteSpace(request.SelectionHash))
        {
            throw new ValidationFailedException(
                "selectionHash",
                "Publication carries the preview hash it was reviewed against, so name it.");
        }

        // The slug is taken verbatim, never normalized: a public address the owner did not
        // type is an address nobody reviewed, so anything but lowercase kebab case is refused
        // rather than repaired into one.
        var slug = request.Slug?.Trim() ?? stored?.Slug
            ?? throw new ValidationFailedException(
                "slug",
                "A published journey needs a public address, so name one.");

        ValidateSlug(slug);

        var resolved = await ResolveSelectionAsync(owner, projectId, request.ApprovedAssetIds, cancellationToken)
            .ConfigureAwait(false);

        var selectionHash = StaticBlogSiteBuilder.SelectionHashFor(resolved.Select(post => post.Asset.Id));

        // The hash the owner reviewed must be the hash of what is stored: a selection that moved
        // after the preview is a publication nobody reviewed, so it is refused, not repaired.
        if (!string.Equals(request.SelectionHash, selectionHash, StringComparison.Ordinal))
        {
            throw new JourneySelectionStaleException();
        }

        await EnsureSlugFreeAsync(slug, projectId, cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var entries = resolved
            .OrderBy(post => post.Milestone.OccurredAt)
            .ThenBy(post => post.Milestone.Id)
            .ThenBy(post => post.Asset.Id)
            .Select(post => ToSnapshotEntry(post, slug))
            .ToList();

        var snapshotJson = JsonSerializer.Serialize(entries, SnapshotJson);
        var snapshotRef = Hash(Encoding.UTF8.GetBytes(snapshotJson));

        var revision = (stored?.Revision ?? 0) + 1;
        var row = new ProjectJourneyRow(
            stored?.Id ?? Guid.NewGuid(),
            owner.ScopeId,
            projectId,
            slug,
            true,
            revision,
            selectionHash,
            snapshotRef,
            snapshotJson,
            revokedAt: null,
            stored?.CreatedAt ?? now,
            now);

        var written = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var changed = stored is null
                    ? await journeys.TryInsertAsync(row, token).ConfigureAwait(false)
                    : await journeys.TryUpdateAsync(row, stored.Revision, token).ConfigureAwait(false);

                if (changed)
                {
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        JourneyPublishedAction,
                        projectId,
                        project.Revision,
                        $"journey published;slug={slug};entries={entries.Count.ToString(CultureInfo.InvariantCulture)};"));
                }

                return changed;
            },
            cancellationToken).ConfigureAwait(false);

        if (!written)
        {
            throw await ClassifyWriteConflictAsync(slug, owner.ScopeId, projectId, stored, cancellationToken)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            "Project journey published for project {ProjectId} under slug {Slug}.",
            projectId,
            slug);

        return new JourneyView(
            projectId,
            slug,
            true,
            revision,
            selectionHash,
            snapshotRef,
            null,
            entries.Count);
    }

    /// <inheritdoc />
    public async Task<JourneyView> ReadAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var stored = await journeys.FindByProjectAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("journey");

        return new JourneyView(
            stored.ProjectId,
            stored.Slug,
            stored.Enabled,
            stored.Revision,
            stored.SelectionHash,
            stored.SnapshotRef,
            stored.RevokedAt,
            SnapshotEntryCount(stored.SnapshotJson));
    }

    /// <inheritdoc />
    public async Task<JourneyPublicView> ReadPublicAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var stored = await journeys.FindBySlugAsync(slug ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null || !stored.Enabled || stored.RevokedAt is not null)
        {
            // Never enabled, revoked and missing are one answer: a caller cannot probe for a
            // journey that exists but is not public.
            throw new ResourceNotFoundException("journey");
        }

        var project = await projects.FindAsync(stored.ScopeId, stored.ProjectId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("journey");

        var entries = await RevalidateAsync(stored, cancellationToken).ConfigureAwait(false);

        return new JourneyPublicView(
            stored.Slug,
            project.Name,
            stored.Revision,
            entries);
    }

    /// <inheritdoc />
    public async Task<PublicJourneyMedia> ReadPublicMediaAsync(
        string slug,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        var stored = await journeys.FindBySlugAsync(slug ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null || !stored.Enabled || stored.RevokedAt is not null)
        {
            throw new ResourceNotFoundException("journey");
        }

        // The bindings are re-checked before a single byte is served: a picture whose approval
        // lapsed is suspended, not served from a snapshot nobody re-authorized.
        var entries = await RevalidateAsync(stored, cancellationToken).ConfigureAwait(false);

        var depicted = entries
            .SelectMany(entry => entry.Pictures)
            .Any(picture => MediaIdFromUrl(picture.MediaUrl) == mediaId);

        if (!depicted)
        {
            // Not an approval-covered picture of the live snapshot: the same 404 as a missing
            // journey, so a caller cannot probe for unselected or private media by comparing codes.
            throw new ResourceNotFoundException("journey");
        }

        var scope = new OwnerScope(stored.ScopeId, Guid.Empty);

        MediaContent content;
        try
        {
            content = await media.OpenAsync(scope, mediaId, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaNotFoundException)
        {
            throw new ResourceNotFoundException("journey");
        }

        await using (content.ConfigureAwait(false))
        {
            if (!string.Equals(content.Ref.MediaType, MediaTypes.Png, StringComparison.Ordinal))
            {
                throw new ResourceNotFoundException("journey");
            }

            return new PublicJourneyMedia(mediaId, content.Ref.MediaType, await ReadBoundedAsync(
                content.Content,
                cancellationToken).ConfigureAwait(false));
        }
    }

    // ==============================================================================================
    // Helpers.
    // ==============================================================================================

    /// <summary>
    /// Revokes one project's journey: the public routes stop serving it immediately. The stored
    /// selection and snapshot stay where they are, so a later publication can review them again -
    /// revocation removes the projection, never the records behind it.
    /// </summary>
    private async Task<JourneyView> RevokeAsync(
        OwnerScope owner,
        Project project,
        ProjectJourneyRow? stored,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (stored is null)
        {
            // Revoking what was never published is already revoked: the correct no-op answer is
            // the disabled state, not a 404 that would send the caller looking for a journey.
            return new JourneyView(project.Id, string.Empty, false, 0, MissingHash, MissingHash, now, 0);
        }

        var row = new ProjectJourneyRow(
            stored.Id,
            stored.ScopeId,
            stored.ProjectId,
            stored.Slug,
            false,
            stored.Revision + 1,
            stored.SelectionHash,
            stored.SnapshotRef,
            stored.SnapshotJson,
            now,
            stored.CreatedAt,
            now);

        var written = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var changed = await journeys.TryUpdateAsync(row, stored.Revision, token).ConfigureAwait(false);

                if (changed)
                {
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        JourneyRevokedAction,
                        project.Id,
                        project.Revision,
                        $"journey revoked;slug={stored.Slug};"));
                }

                return changed;
            },
            cancellationToken).ConfigureAwait(false);

        if (!written)
        {
            throw new RevisionConflictException(
                (await journeys.FindByProjectAsync(owner.ScopeId, project.Id, cancellationToken)
                    .ConfigureAwait(false))?.Revision ?? stored.Revision);
        }

        logger.LogInformation(
            "Project journey revoked for project {ProjectId}.",
            project.Id);

        return new JourneyView(
            row.ProjectId,
            row.Slug,
            false,
            row.Revision,
            row.SelectionHash,
            row.SnapshotRef,
            row.RevokedAt,
            SnapshotEntryCount(row.SnapshotJson));
    }

    /// <summary>
    /// Re-resolves a published snapshot against the live approvals and returns the public entries
    /// it still authorizes. Any drift - an edit, an unapproval, a dismissal, a moved bundle or a
    /// reselected picture - suspends the snapshot as a 404 rather than serving text nobody
    /// authorized.
    /// </summary>
    private async Task<IReadOnlyList<JourneyPublicEntry>> RevalidateAsync(
        ProjectJourneyRow stored,
        CancellationToken cancellationToken)
    {
        SnapshotEntry[] snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<SnapshotEntry[]>(stored.SnapshotJson, SnapshotJson)
                ?? throw new FormatException("A published journey depicts at least one entry.");
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            // A stored snapshot this build cannot read is suspended, not served half-read.
            throw new ResourceNotFoundException("journey");
        }

        if (snapshot.Length == 0)
        {
            throw new ResourceNotFoundException("journey");
        }

        var scope = new OwnerScope(stored.ScopeId, Guid.Empty);
        var entries = new List<JourneyPublicEntry>(snapshot.Length);

        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ResolvedPost live;
            try
            {
                live = await ResolveOneAsync(scope, stored.ProjectId, entry.AssetId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ResourceNotFoundException
                or ApprovalRequiredException
                or ApprovalStaleException
                or MilestoneNotConfirmedException
                or MediaUnavailableException
                or UnsupportedMediaTypeException
                or ValidationFailedException)
            {
                // The world the snapshot froze has moved: the snapshot is suspended, not updated.
                throw new ResourceNotFoundException("journey");
            }

            if (live.Revision.Revision != entry.ContentRevision ||
                !string.Equals(live.Revision.BodyHash, entry.BodyHash, StringComparison.Ordinal) ||
                live.Head.Id != entry.EvidenceBundleId ||
                live.Head.Revision != entry.EvidenceRevision ||
                !string.Equals(live.Approval.MediaSelectionHash, entry.MediaSelectionHash, StringComparison.Ordinal) ||
                !string.Equals(live.Milestone.Title, entry.MilestoneTitle, StringComparison.Ordinal) ||
                live.Milestone.OccurredAt != entry.OccurredAt)
            {
                throw new ResourceNotFoundException("journey");
            }

            entries.Add(new JourneyPublicEntry(
                live.Milestone.Title,
                live.Milestone.OccurredAt,
                live.Revision.BodyMarkdown,
                [.. live.Pictures.Select(picture => new JourneyPublicPicture(
                    picture.Caption,
                    MediaUrlFor(stored.Slug, picture.MediaId)))]));
        }

        return entries;
    }

    /// <summary>
    /// Resolves one published entry's selection against the live stores, or refuses with the
    /// review package's own codes: never approved is <c>approval_required</c>, once approved but
    /// moved is <c>approval_stale</c>, and a missing picture is <c>media_unavailable</c>.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedPost>> ResolveSelectionAsync(
        OwnerScope owner,
        Guid projectId,
        IReadOnlyList<Guid>? assetIds,
        CancellationToken cancellationToken)
    {
        if (assetIds is null || assetIds.Count == 0)
        {
            throw new ValidationFailedException(
                "approvedAssetIds",
                "A journey depicts at least one approved entry, so name one.");
        }

        if (assetIds.Count > MaxEntries)
        {
            throw new ValidationFailedException(
                "approvedAssetIds",
                $"A journey depicts at most {MaxEntries} entries.");
        }

        if (assetIds.Any(id => id == Guid.Empty))
        {
            throw new ValidationFailedException(
                "approvedAssetIds",
                "A journey names stored assets, so name them.");
        }

        if (assetIds.Distinct().Count() != assetIds.Count)
        {
            throw new ValidationFailedException(
                "approvedAssetIds",
                "A journey names each approved asset once, so name each once.");
        }

        var resolved = new List<ResolvedPost>(assetIds.Count);

        foreach (var assetId in assetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolved.Add(await ResolveOneAsync(owner, projectId, assetId, cancellationToken).ConfigureAwait(false));
        }

        return resolved;
    }

    private async Task<ResolvedPost> ResolveOneAsync(
        OwnerScope owner,
        Guid projectId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        if (assetId == Guid.Empty)
        {
            throw new ValidationFailedException("approvedAssetIds", "A journey names stored assets, so name them.");
        }

        var asset = await assets.FindAsync(owner.ScopeId, assetId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        // An asset of another project answers the same 404 as a missing one: the selection is
        // bound to the project under projection, and a foreign asset is not part of it.
        if (asset.ProjectId != projectId)
        {
            throw new ResourceNotFoundException("content asset");
        }

        var milestone = await milestones.FindAsync(owner.ScopeId, asset.MilestoneId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw UnreadableJourneyRow();

        // Only a confirmed milestone counts as history, so only a confirmed milestone's story is
        // depicted. Candidates and dismissed records are omitted rather than narrated, which is
        // the timeline's own rule applied to the projection.
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

        var storedRevision = await assets
            .FindRevisionAsync(owner.ScopeId, assetId, live.ContentRevision, cancellationToken)
            .ConfigureAwait(false)
            ?? throw UnreadableJourneyRow();

        if (!string.Equals(ContentRevision.Hash(storedRevision.BodyMarkdown), live.BodyHash, StringComparison.Ordinal) ||
            !string.Equals(storedRevision.BodyHash, live.BodyHash, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("body");
        }

        var head = await evidence
            .FindHeadAsync(owner.ScopeId, asset.MilestoneId, cancellationToken)
            .ConfigureAwait(false);

        if (head is null || head.Id != storedRevision.EvidenceBundleId || head.Revision != live.EvidenceRevision)
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
        // picture whose bytes are gone refuses the journey rather than depicting it picture-less.
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

        return new ResolvedPost(asset, milestone, storedRevision, live, head, pictures);
    }

    /// <summary>
    /// One selected entry with the live state the preview, publication or public read must agree on.
    /// </summary>
    private sealed record ResolvedPost(
        ContentAsset Asset,
        Milestone Milestone,
        ContentRevision Revision,
        ContentApproval Approval,
        EvidenceBundle Head,
        IReadOnlyList<(Guid MediaId, string Caption)> Pictures);

    /// <summary>One frozen snapshot entry, as stored in the journey row.</summary>
    private sealed record SnapshotEntry(
        Guid AssetId,
        Guid MilestoneId,
        string MilestoneTitle,
        DateTimeOffset OccurredAt,
        long ContentRevision,
        string BodyHash,
        Guid EvidenceBundleId,
        long EvidenceRevision,
        string MediaSelectionHash);

    private static JourneyPreviewEntry ToPreviewEntry(ResolvedPost post) => new(
        post.Asset.Id,
        post.Milestone.Id,
        post.Milestone.Title,
        post.Milestone.OccurredAt,
        post.Revision.Revision,
        post.Revision.BodyHash,
        post.Head.Revision,
        [.. post.Pictures.Select(picture => new JourneyPicture(picture.MediaId, picture.Caption))]);

    private static SnapshotEntry ToSnapshotEntry(ResolvedPost post, string slug)
    {
        _ = slug;
        return new SnapshotEntry(
            post.Asset.Id,
            post.Milestone.Id,
            post.Milestone.Title,
            post.Milestone.OccurredAt,
            post.Revision.Revision,
            post.Revision.BodyHash,
            post.Head.Id,
            post.Head.Revision,
            post.Approval.MediaSelectionHash);
    }

    private static void ValidateSlug(string slug)
    {
        if (slug.Length < SlugMinLength || slug.Length > SlugMaxLength || !SlugPattern.IsMatch(slug))
        {
            throw new ValidationFailedException(
                "slug",
                $"A journey address is {SlugMinLength} to {SlugMaxLength} lowercase letters, digits and single hyphens.");
        }
    }

    private async Task EnsureSlugFreeAsync(
        string slug,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var taken = await journeys.FindBySlugAsync(slug, cancellationToken).ConfigureAwait(false);

        if (taken is not null && taken.ProjectId != projectId)
        {
            throw new JourneySlugConflictException();
        }
    }

    /// <summary>
    /// Classifies a lost insert/update race by re-reading: a slug taken elsewhere is a slug
    /// conflict, anything else is a revision conflict on the journey the writer read.
    /// </summary>
    private async Task<ApiException> ClassifyWriteConflictAsync(
        string slug,
        Guid scopeId,
        Guid projectId,
        ProjectJourneyRow? read,
        CancellationToken cancellationToken)
    {
        var taken = await journeys.FindBySlugAsync(slug, cancellationToken).ConfigureAwait(false);

        if (taken is not null && taken.ProjectId != projectId)
        {
            return new JourneySlugConflictException();
        }

        var current = await journeys.FindByProjectAsync(scopeId, projectId, cancellationToken)
            .ConfigureAwait(false);

        return new RevisionConflictException(current?.Revision ?? read?.Revision ?? 0);
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

    private static string MediaUrlFor(string slug, Guid mediaId) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/journey/{slug}/media/{mediaId:D}");

    private static Guid MediaIdFromUrl(string mediaUrl)
    {
        var segment = mediaUrl[(mediaUrl.LastIndexOf('/') + 1)..];
        return Guid.TryParse(segment, out var mediaId) ? mediaId : Guid.Empty;
    }

    private static int SnapshotEntryCount(string snapshotJson)
    {
        try
        {
            return JsonSerializer.Deserialize<SnapshotEntry[]>(snapshotJson, SnapshotJson)?.Length ?? 0;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>Reads a media stream up to the whole-journey bound, refusing anything larger.</summary>
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
            if (total > MaxPublicMediaBytes)
            {
                throw new PayloadTooLargeException(MaxPublicMediaBytes);
            }

            buffered.Write(chunk, 0, read);
        }

        return buffered.ToArray();
    }

    /// <summary>At most this many bytes one public picture serves; stored pictures are bounded below it.</summary>
    private const long MaxPublicMediaBytes = 32 * 1024 * 1024;

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The selection and snapshot hashes of a journey that was never published.</summary>
    private static readonly string MissingHash = new('0', 64);

    /// <summary>
    /// A stored row this build cannot read (500).
    /// </summary>
    private static ApiException UnreadableJourneyRow() =>
        new InternalFailureException(
            "A stored journey record could not be read by this build, so it cannot be published. "
            + "The record itself is unchanged.");
}
