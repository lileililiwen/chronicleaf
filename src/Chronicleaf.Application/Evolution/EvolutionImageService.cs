using System.Globalization;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Curation;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Evolution;

/// <summary>
/// The evolution use cases: what an owner may compare, and what that comparison authorizes
/// (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Selected only, always.</b> Depicted pictures are read through the curation store and every
/// named id must already be selected: a candidate is never decoded, never placed and never cited,
/// and naming one is a 422 rather than a promotion. The human curation bindings are preserved
/// because this package moves none of them - it reads the selection the curation package owns.
/// </para>
/// <para>
/// <b>The media store writes first, the row second.</b> The rendered PNG is stored through the
/// delivered media store before this package's row exists, so a row can never point at bytes
/// that were not stored - the same order the capture package uses. A row that loses its insert
/// retires the bytes it just stored, so a retried render leaves nothing behind.
/// </para>
/// </remarks>
public sealed class EvolutionImageService(
    IProjectStore projects,
    IMilestoneStore milestones,
    IScreenshotCurationStore curation,
    IEvidenceStore evidence,
    IMediaStore media,
    IEvolutionImageStore evolutions,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<EvolutionImageService> logger) : IEvolutionImageService
{
    /// <summary>The audit action recorded when a comparison is rendered.</summary>
    public const string EvolutionRenderedAction = "evolution.rendered";

    /// <summary>The audit action recorded when a comparison is approved.</summary>
    public const string EvolutionApprovedAction = "evolution.approved";

    /// <summary>The manifest version every row in this package carries.</summary>
    public const string ManifestVersion = "evolution-manifest/1";

    /// <inheritdoc />
    public async Task<RenderEvolutionOutcome> RenderAsync(
        OwnerScope owner,
        Guid projectId,
        RenderEvolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        var layout = RequireLayout(request.Layout);
        var milestoneIds = RequireMilestoneIds(request.MilestoneIds, layout);
        var pins = RequirePins(request.SelectedScreenshotIds);

        var project = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        if (project.IsArchived)
        {
            throw new ProjectArchivedException();
        }

        var requested = await LoadMilestonesAsync(owner, projectId, milestoneIds, cancellationToken).ConfigureAwait(false);

        // Chronological by occurredAt/id unless the owner fixed the sequence in the manifest:
        // the canvas order is part of what was reviewed, so the default is derived, never asked.
        var ordered = request.ManualOrder is null
            ? requested.OrderBy(milestone => milestone.OccurredAt).ThenBy(milestone => milestone.Id).ToList()
            : RequireOrder(request.ManualOrder, milestoneIds)
                .Select(id => requested.Single(milestone => milestone.Id == id))
                .ToList();

        var depicted = await LoadDepictedAsync(owner, ordered, pins, cancellationToken).ConfigureAwait(false);

        RequireDepicted(depicted, layout);

        var viewports = depicted
            .Where(source => source.Slot.IsGap == false)
            .Select(source => source.Picture!.Viewport.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (viewports.Count > 1 && !string.Equals(layout, EvolutionLayouts.Gallery, StringComparison.Ordinal))
        {
            throw new ComparisonViewportMismatchException();
        }

        var selectionHash = SelectionHash(layout, depicted);

        if (request.ExpectedSelectionHash is not null &&
            !string.Equals(request.ExpectedSelectionHash, selectionHash, StringComparison.Ordinal))
        {
            throw new SelectionSupersededException();
        }

        var key = RequireIdempotencyKey(request.IdempotencyKey);

        if (key is not null)
        {
            var replay = await evolutions
                .FindByIdempotencyKeyAsync(owner.ScopeId, projectId, key, cancellationToken)
                .ConfigureAwait(false);

            if (replay is not null)
            {
                return ReplayOrConflict(replay, selectionHash, key);
            }
        }

        var slots = depicted.Select(source => source.Slot).ToList();

        // Composed before anything is stored: an oversized canvas is a 413 with no row and no
        // media, never a usable partial image.
        var rendered = EvolutionPng.Compose(slots, layout);
        var manifest = ManifestJson(projectId, layout, ordered, depicted, selectionHash, rendered);

        var artifactId = Guid.NewGuid();
        var createdAt = clock.GetUtcNow();

        MediaRef stored;
        await using (var png = new MemoryStream(rendered.Png, writable: false))
        {
            stored = await media.PutAsync(
                owner,
                new PutMediaInput(projectId, png, "image/png"),
                cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(stored.Sha256, rendered.Sha256, StringComparison.Ordinal))
        {
            throw new InternalFailureException("The stored artifact digest does not describe the rendered bytes.");
        }

        var artifact = new EvolutionImage(
            artifactId,
            owner.ScopeId,
            projectId,
            layout,
            manifest,
            selectionHash,
            stored.Id,
            rendered.Sha256,
            rendered.Width,
            rendered.Height,
            EvolutionImageStatus.Draft,
            approvedBy: null,
            approvedAt: null,
            key,
            createdAt);

        var inserted = await unitOfWork.ExecuteAsync(
            async token =>
            {
                if (!await evolutions.InsertAsync(artifact, token).ConfigureAwait(false))
                {
                    return false;
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    EvolutionRenderedAction,
                    artifactId,
                    1,
                    EvolutionImage.Label(artifactId)));

                return true;
            },
            cancellationToken).ConfigureAwait(false);

        if (!inserted)
        {
            await RetireQuietlyAsync(owner, stored.Id, cancellationToken).ConfigureAwait(false);

            if (key is not null)
            {
                var raced = await evolutions
                    .FindByIdempotencyKeyAsync(owner.ScopeId, projectId, key, cancellationToken)
                    .ConfigureAwait(false);

                if (raced is not null)
                {
                    return ReplayOrConflict(raced, selectionHash, key);
                }
            }

            throw new InternalFailureException("The rendered comparison could not be recorded.");
        }

        logger.LogInformation(
            "Evolution image {EvolutionId} rendered for project {ProjectId}: {Depicted} depicted, {Gaps} gaps.",
            artifactId,
            projectId,
            depicted.Count(source => !source.Slot.IsGap),
            depicted.Count(source => source.Slot.IsGap));

        return new RenderEvolutionOutcome(artifact, Created: true);
    }

    /// <inheritdoc />
    public async Task<(EvolutionImage Artifact, bool SelectionCurrent)> ReadAsync(
        OwnerScope owner,
        Guid evolutionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var artifact = await evolutions.FindAsync(owner.ScopeId, evolutionId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("evolution image");

        return (artifact, await SelectionCurrentAsync(owner, artifact, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EvolutionImage>> ListAsync(
        OwnerScope owner,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return evolutions.ListAsync(
            owner.ScopeId,
            projectId,
            Math.Clamp(limit, 1, ListQueryPolicy.MaxPageSize),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApproveEvolutionOutcome> ApproveAsync(
        OwnerScope owner,
        Guid evolutionId,
        string expectedSelectionHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSelectionHash);

        return await unitOfWork.ExecuteAsync(
            token => ApproveCoreAsync(owner, evolutionId, expectedSelectionHash, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EvolutionExport> PreviewAsync(
        OwnerScope owner,
        Guid evolutionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var artifact = await evolutions.FindAsync(owner.ScopeId, evolutionId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("evolution image");

        var bytes = await OpenArtifactAsync(owner, artifact, cancellationToken).ConfigureAwait(false);
        var live = await SelectionCurrentAsync(owner, artifact, cancellationToken).ConfigureAwait(false);

        return new EvolutionExport(bytes, artifact.ArtifactSha256, ApprovalLive: artifact.IsApproved && live);
    }

    /// <inheritdoc />
    public async Task<EvolutionExport> ExportAsync(
        OwnerScope owner,
        Guid evolutionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var artifact = await evolutions.FindAsync(owner.ScopeId, evolutionId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("evolution image");

        if (!artifact.IsApproved)
        {
            throw new ApprovalRequiredException();
        }

        if (!await SelectionCurrentAsync(owner, artifact, cancellationToken).ConfigureAwait(false))
        {
            throw new ApprovalStaleException("selection");
        }

        var bytes = await OpenArtifactAsync(owner, artifact, cancellationToken).ConfigureAwait(false);

        return new EvolutionExport(bytes, artifact.ArtifactSha256, ApprovalLive: true);
    }

    // -----------------------------------------------------------------------------------------
    // Rendering.
    // -----------------------------------------------------------------------------------------

    private async Task<ApproveEvolutionOutcome> ApproveCoreAsync(
        OwnerScope owner,
        Guid evolutionId,
        string expectedSelectionHash,
        CancellationToken cancellationToken)
    {
        var artifact = await evolutions.FindAsync(owner.ScopeId, evolutionId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("evolution image");

        if (artifact.IsApproved)
        {
            return new ApproveEvolutionOutcome(artifact, AlreadyApproved: true);
        }

        if (!await SelectionCurrentAsync(owner, artifact, cancellationToken).ConfigureAwait(false))
        {
            // The selection moved since the render: no approval can authorize a manifest nobody
            // reviewed, so the owner reads the current one and approves that instead.
            throw new SelectionSupersededException();
        }

        if (!string.Equals(expectedSelectionHash, artifact.SelectionHash, StringComparison.Ordinal))
        {
            throw new SelectionSupersededException();
        }

        var approvedAt = clock.GetUtcNow();

        if (!await evolutions
            .TryApproveAsync(owner.ScopeId, evolutionId, owner.ActorId, approvedAt, cancellationToken)
            .ConfigureAwait(false))
        {
            var reread = await evolutions.FindAsync(owner.ScopeId, evolutionId, cancellationToken).ConfigureAwait(false)
                ?? throw new ResourceNotFoundException("evolution image");

            if (reread.IsApproved)
            {
                return new ApproveEvolutionOutcome(reread, AlreadyApproved: true);
            }

            throw new SelectionSupersededException();
        }

        audit.Record(new DomainAudit(
            owner.ActorId,
            EvolutionApprovedAction,
            evolutionId,
            1,
            EvolutionImage.Label(evolutionId)));

        logger.LogInformation(
            "Evolution image {EvolutionId} approved by {ActorId}.",
            evolutionId,
            owner.ActorId);

        return new ApproveEvolutionOutcome(
            await evolutions.FindAsync(owner.ScopeId, evolutionId, cancellationToken).ConfigureAwait(false)
                ?? throw new ResourceNotFoundException("evolution image"),
            AlreadyApproved: false);
    }

    private static RenderEvolutionOutcome ReplayOrConflict(
        EvolutionImage replay,
        string selectionHash,
        string key)
    {
        if (string.Equals(replay.SelectionHash, selectionHash, StringComparison.Ordinal))
        {
            return new RenderEvolutionOutcome(replay, Created: false);
        }

        throw new OperationIdempotencyConflictException(key, replay.SelectionHash);
    }

    private async Task RetireQuietlyAsync(OwnerScope owner, Guid mediaId, CancellationToken cancellationToken)
    {
        try
        {
            await media.RetireAsync(owner, mediaId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ApiException)
        {
            logger.LogInformation(
                "A superseded evolution render left media {MediaId} for reconciliation: {Code}.",
                mediaId,
                ((ApiException)failure).Code);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Loading.
    // -----------------------------------------------------------------------------------------

    private static string RequireLayout(string? layout)
    {
        if (!EvolutionLayouts.IsKnown(layout))
        {
            throw new ValidationFailedException(
                "layout",
                "The layout is one of before-after, horizontal-timeline, vertical-long or gallery.");
        }

        return layout!;
    }

    private static IReadOnlyList<Guid> RequireMilestoneIds(IReadOnlyList<Guid>? milestoneIds, string layout)
    {
        if (milestoneIds is null || milestoneIds.Count == 0)
        {
            throw new ValidationFailedException("milestoneIds", "An evolution compares named milestones.");
        }

        if (milestoneIds.Any(id => id == Guid.Empty))
        {
            throw new ValidationFailedException("milestoneIds", "Every milestone id must be non-empty.");
        }

        if (milestoneIds.Distinct().Count() != milestoneIds.Count)
        {
            throw new ValidationFailedException("milestoneIds", "Every milestone is named once.");
        }

        if (milestoneIds.Count < EvolutionImageLimits.MinMilestones ||
            milestoneIds.Count > EvolutionImageLimits.MaxMilestones)
        {
            throw new ValidationFailedException(
                "milestoneIds",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"An evolution compares {EvolutionImageLimits.MinMilestones} to {EvolutionImageLimits.MaxMilestones} milestones."));
        }

        if (string.Equals(layout, EvolutionLayouts.BeforeAfter, StringComparison.Ordinal) &&
            milestoneIds.Count != 2)
        {
            throw new ValidationFailedException(
                "layout",
                "The before-after layout compares exactly two milestones.");
        }

        return milestoneIds;
    }

    private static HashSet<Guid> RequirePins(IReadOnlyList<Guid>? pins)
    {
        if (pins is null)
        {
            return [];
        }

        if (pins.Any(id => id == Guid.Empty) || pins.Distinct().Count() != pins.Count)
        {
            throw new ValidationFailedException(
                "selectedScreenshotIds",
                "Every named picture is a distinct non-empty id.");
        }

        return [.. pins];
    }

    private static IReadOnlyList<Guid> RequireOrder(IReadOnlyList<Guid>? manualOrder, IReadOnlyList<Guid> milestoneIds)
    {
        if (manualOrder is null)
        {
            return milestoneIds;
        }

        if (manualOrder.Count != milestoneIds.Count ||
            manualOrder.Distinct().Count() != manualOrder.Count ||
            !manualOrder.All(id => milestoneIds.Contains(id)))
        {
            throw new ValidationFailedException(
                "manualOrder",
                "A manual order names exactly the requested milestones, once each.");
        }

        return manualOrder;
    }

    private static string? RequireIdempotencyKey(string? key)
    {
        if (key is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > EvolutionImageLimits.IdempotencyKeyMaxLength)
        {
            throw new ValidationFailedException(
                "idempotencyKey",
                "An idempotency key is short non-blank text.");
        }

        return key;
    }

    private async Task<IReadOnlyList<Milestone>> LoadMilestonesAsync(
        OwnerScope owner,
        Guid projectId,
        IReadOnlyList<Guid> order,
        CancellationToken cancellationToken)
    {
        var loaded = new List<Milestone>(order.Count);

        foreach (var milestoneId in order)
        {
            var milestone = await milestones
                .FindAsync(owner.ScopeId, milestoneId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ResourceNotFoundException("milestone");

            if (milestone.ProjectId != projectId)
            {
                throw new ResourceNotFoundException("milestone");
            }

            if (!milestone.IsConfirmed)
            {
                throw new ValidationFailedException(
                    "milestoneIds",
                    "An evolution compares confirmed milestones.");
            }

            loaded.Add(milestone);
        }

        return loaded;
    }

    private sealed record DepictedSource(
        Milestone Milestone,
        CuratedScreenshot? Picture,
        string? MediaSha256,
        long EvidenceRevision,
        EvolutionSlot Slot);

    private async Task<IReadOnlyList<DepictedSource>> LoadDepictedAsync(
        OwnerScope owner,
        IReadOnlyList<Milestone> canvasOrder,
        HashSet<Guid> pins,
        CancellationToken cancellationToken)
    {
        var sources = new List<DepictedSource>(canvasOrder.Count);
        var pinned = new HashSet<Guid>(pins);

        foreach (var milestone in canvasOrder)
        {
            var pictures = await curation
                .ListAsync(owner.ScopeId, milestone.Id, ListQueryPolicy.MaxPageSize, cancellationToken)
                .ConfigureAwait(false);

            var selected = pictures.Where(picture => picture.IsSelected).ToList();
            var head = await evidence
                .FindHeadAsync(owner.ScopeId, milestone.Id, cancellationToken)
                .ConfigureAwait(false);

            CuratedScreenshot? chosen = null;

            var pin = selected.FirstOrDefault(picture => pinned.Contains(picture.Id));
            if (pin is not null)
            {
                chosen = pin;
                pinned.Remove(pin.Id);
            }
            else if (pins.Count > 0)
            {
                // Pins name pictures explicitly: a milestone with no pin falls back to its primary,
                // and only a milestone with no selected picture at all is a gap.
                chosen = selected.FirstOrDefault(picture => picture.IsPrimary)
                    ?? selected.OrderBy(picture => picture.DisplayOrder).ThenBy(picture => picture.CreatedAt).FirstOrDefault();
            }
            else
            {
                chosen = selected.FirstOrDefault(picture => picture.IsPrimary)
                    ?? selected.OrderBy(picture => picture.DisplayOrder).ThenBy(picture => picture.CreatedAt).FirstOrDefault();
            }

            if (chosen is null)
            {
                sources.Add(new DepictedSource(milestone, null, null, head?.Revision ?? 0, EvolutionSlot.Gap()));
                continue;
            }

            var media = await OpenSourceAsync(owner, chosen, cancellationToken).ConfigureAwait(false);

            if (!EvolutionPng.TryDecodeToGrey(media, out var grey) || grey is null)
            {
                throw new ValidationFailedException(
                    "selectedScreenshotIds",
                    "A selected screenshot's bytes are not a supported image.");
            }

            sources.Add(new DepictedSource(
                milestone,
                chosen,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(media)).ToLowerInvariant(),
                head?.Revision ?? 0,
                EvolutionSlot.Depicted(grey)));
        }

        if (pinned.Count > 0)
        {
            // Every pin must be a selected picture of a requested milestone. Anything else is
            // either a candidate - which is never compared - or a picture from another
            // milestone, and both are refused rather than silently dropped.
            throw new ValidationFailedException(
                "selectedScreenshotIds",
                "Every named picture must be selected for one of the requested milestones.");
        }

        return sources;
    }

    private async Task<byte[]> OpenSourceAsync(
        OwnerScope owner,
        CuratedScreenshot picture,
        CancellationToken cancellationToken)
    {
        await using var content = await OpenMediaAsync(owner, picture.MediaId, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await content.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private async Task<MediaContent> OpenMediaAsync(
        OwnerScope owner,
        Guid mediaId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await media.OpenAsync(owner, mediaId, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaNotFoundException)
        {
            throw new EvolutionMediaUnavailableException(mediaId);
        }
    }

    private async Task<byte[]> OpenArtifactAsync(
        OwnerScope owner,
        EvolutionImage artifact,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        await using (var content = await OpenMediaAsync(owner, artifact.ArtifactMediaId, cancellationToken).ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await content.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        if (!string.Equals(sha, artifact.ArtifactSha256, StringComparison.Ordinal))
        {
            throw new InternalFailureException("The stored artifact bytes do not match their digest.");
        }

        return bytes;
    }

    private static void RequireDepicted(IReadOnlyList<DepictedSource> depicted, string layout)
    {
        var count = depicted.Count(source => !source.Slot.IsGap);

        if (count < EvolutionImageLimits.MinMilestones)
        {
            var gaps = string.Join(
                ",",
                depicted.Where(source => source.Slot.IsGap).Select(source => source.Milestone.Id.ToString("D")));

            throw new InsufficientSelectedException(gaps);
        }

        if (string.Equals(layout, EvolutionLayouts.BeforeAfter, StringComparison.Ordinal) &&
            depicted.Any(source => source.Slot.IsGap))
        {
            throw new InsufficientSelectedException("before-after");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Selection hash and manifest.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The canonical identity of what was compared: the layout, the canvas order, and per slot
    /// either the depicted picture's media, revision and caption revisions or the gap marker -
    /// each beside the evidence revision it was read against.
    /// </summary>
    private static string SelectionHash(string layout, IReadOnlyList<DepictedSource> depicted)
    {
        var lines = new List<string>(depicted.Count + 1) { layout };

        foreach (var source in depicted)
        {
            if (source.Slot.IsGap)
            {
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"gap|{source.Milestone.Id:D}|{source.EvidenceRevision.ToString(CultureInfo.InvariantCulture)}"));
            }
            else
            {
                var picture = source.Picture!;
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{source.Milestone.Id:D}|{picture.Id:D}|{picture.Revision.ToString(CultureInfo.InvariantCulture)}" +
                    $"|{picture.MediaId:D}|{source.MediaSha256}|{picture.Caption}" +
                    $"|{source.EvidenceRevision.ToString(CultureInfo.InvariantCulture)}"));
            }
        }

        return ContentApproval.Hash(string.Join("\n", lines));
    }

    private static string ManifestJson(
        Guid projectId,
        string layout,
        IReadOnlyList<Milestone> requested,
        IReadOnlyList<DepictedSource> depicted,
        string selectionHash,
        RenderedEvolution rendered)
    {
        var chronological = depicted
            .OrderBy(source => source.Milestone.OccurredAt)
            .ThenBy(source => source.Milestone.Id)
            .Select(source => source.Milestone.Id.ToString("D"))
            .ToArray();

        var manifest = new
        {
            version = ManifestVersion,
            projectId = projectId.ToString("D"),
            layout,
            requestedMilestones = requested.Select(milestone => milestone.Id.ToString("D")).ToArray(),
            chronologicalMilestones = chronological,
            sources = depicted.Select(source => source.Slot.IsGap
                ? new
                {
                    milestoneId = source.Milestone.Id.ToString("D"),
                    screenshotId = (string?)null,
                    mediaId = (string?)null,
                    screenshotRevision = (long?)null,
                    evidenceRevision = source.EvidenceRevision,
                    mediaSha256 = (string?)null,
                    caption = (string?)null,
                    viewport = (string?)null,
                    capturedAt = (string?)null,
                    status = "unavailable",
                    reason = (string?)"no-selected-screenshot",
                }
                : new
                {
                    milestoneId = source.Milestone.Id.ToString("D"),
                    screenshotId = (string?)source.Picture!.Id.ToString("D"),
                    mediaId = (string?)source.Picture.MediaId.ToString("D"),
                    screenshotRevision = (long?)source.Picture.Revision,
                    evidenceRevision = source.EvidenceRevision,
                    mediaSha256 = source.MediaSha256,
                    caption = (string?)source.Picture.Caption,
                    viewport = (string?)source.Picture.Viewport.Name,
                    capturedAt = (string?)source.Picture.CapturedAt.ToString("o"),
                    status = "depicted",
                    reason = (string?)null,
                }).ToArray(),
            selectionHash,
            artifactSha256 = rendered.Sha256,
            width = rendered.Width,
            height = rendered.Height,
        };

        return JsonSerializer.Serialize(manifest);
    }

    private async Task<bool> SelectionCurrentAsync(
        OwnerScope owner,
        EvolutionImage artifact,
        CancellationToken cancellationToken)
    {
        using var manifest = JsonDocument.Parse(artifact.ManifestJson);
        var root = manifest.RootElement;

        if (!root.TryGetProperty("requestedMilestones", out var requested) ||
            !root.TryGetProperty("layout", out var layoutProperty))
        {
            return false;
        }

        var milestoneIds = requested.EnumerateArray()
            .Select(id => Guid.TryParse(id.GetString(), out var parsed) ? parsed : Guid.Empty)
            .ToList();

        if (milestoneIds.Any(id => id == Guid.Empty))
        {
            return false;
        }

        var layout = layoutProperty.GetString();
        var pins = root.GetProperty("sources").EnumerateArray()
            .Where(source => string.Equals(source.GetProperty("status").GetString(), "depicted", StringComparison.Ordinal))
            .Select(source => Guid.Parse(source.GetProperty("screenshotId").GetString()!))
            .ToHashSet();

        IReadOnlyList<Milestone> ordered;
        try
        {
            ordered = await LoadMilestonesAsync(owner, artifact.ProjectId, milestoneIds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ApiException or InvalidOperationException)
        {
            return false;
        }

        // The canvas order is the manifest's, not the request's: a manual order is part of what
        // was reviewed, and re-deriving it from the request would compare against the wrong
        // sequence. The sources array is already in canvas order.
        var canvasMilestones = root.GetProperty("sources").EnumerateArray()
            .Select(source => Guid.Parse(source.GetProperty("milestoneId").GetString()!))
            .ToList();

        ordered = [.. canvasMilestones.Select(id => ordered.Single(milestone => milestone.Id == id))];

        IReadOnlyList<DepictedSource> depicted;
        try
        {
            depicted = await LoadDepictedAsync(owner, ordered, pins, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ApiException or InvalidOperationException)
        {
            return false;
        }

        return string.Equals(SelectionHash(layout!, depicted), artifact.SelectionHash, StringComparison.Ordinal);
    }
}
