using System.Globalization;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Curation;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Evolution;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.EvolutionGifs;

/// <summary>
/// The evolution GIF use cases: what an owner may animate, and what that animation authorizes
/// (EVOLUTION-GIF-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Selected only, always.</b> Depicted pictures are read through the curation store and every
/// named id must already be selected: a candidate is never decoded, never placed and never cited,
/// and naming one is a 422 rather than a promotion. The human curation bindings are preserved
/// because this package moves none of them - it reads the selection the curation package owns.
/// </para>
/// <para>
/// <b>The row and its bytes are written together.</b> The delivered media store is PNG-only by
/// its own contract, so this package carries its GIF bytes in its own row, written in the same
/// INSERT as the manifest: a row can never point at bytes that were not stored, and a render
/// that fails - a missing picture, an oversized canvas, a cancelled encoder - stores nothing at
/// all. A partial GIF is never completable because no partial state exists to complete.
/// </para>
/// </remarks>
public sealed class EvolutionGifService(
    IProjectStore projects,
    IMilestoneStore milestones,
    IScreenshotCurationStore curation,
    IEvidenceStore evidence,
    IMediaStore media,
    IEvolutionGifStore gifs,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<EvolutionGifService> logger) : IEvolutionGifService
{
    /// <summary>The audit action recorded when a GIF is rendered.</summary>
    public const string EvolutionGifRenderedAction = "evolution-gif.rendered";

    /// <summary>The audit action recorded when a GIF is approved.</summary>
    public const string EvolutionGifApprovedAction = "evolution-gif.approved";

    /// <summary>The manifest version every row in this package carries.</summary>
    public const string ManifestVersion = "evolution-gif-manifest/1";

    /// <inheritdoc />
    public async Task<RenderEvolutionGifOutcome> RenderAsync(
        OwnerScope owner,
        Guid projectId,
        RenderEvolutionGifRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        var milestoneIds = RequireMilestoneIds(request.MilestoneIds);
        var pins = RequirePins(request.SelectedScreenshotIds);

        var project = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        if (project.IsArchived)
        {
            throw new ProjectArchivedException();
        }

        var requested = await LoadMilestonesAsync(owner, projectId, milestoneIds, cancellationToken).ConfigureAwait(false);

        // Chronological by occurredAt/id unless the owner fixed the sequence in the manifest:
        // the frame order is part of what was reviewed, so the default is derived, never asked.
        var ordered = request.ManualOrder is null
            ? requested.OrderBy(milestone => milestone.OccurredAt).ThenBy(milestone => milestone.Id).ToList()
            : RequireOrder(request.ManualOrder, milestoneIds)
                .Select(id => requested.Single(milestone => milestone.Id == id))
                .ToList();

        var depicted = await LoadDepictedAsync(owner, ordered, pins, cancellationToken).ConfigureAwait(false);

        RequireDepicted(depicted);

        var durations = RequireDurations(request.DurationsMs, depicted.Count);

        var selectionHash = SelectionHash(depicted, durations, request.Loop);

        if (request.ExpectedSelectionHash is not null &&
            !string.Equals(request.ExpectedSelectionHash, selectionHash, StringComparison.Ordinal))
        {
            throw new EvolutionGifSelectionSupersededException();
        }

        var key = RequireIdempotencyKey(request.IdempotencyKey);

        if (key is not null)
        {
            var replay = await gifs
                .FindByIdempotencyKeyAsync(owner.ScopeId, projectId, key, cancellationToken)
                .ConfigureAwait(false);

            if (replay is not null)
            {
                return ReplayOrConflict(replay, selectionHash, key);
            }
        }

        // Encoded before anything is stored: an oversized canvas or a cancelled encoder leaves
        // no row and no bytes, never a usable partial artifact.
        cancellationToken.ThrowIfCancellationRequested();
        var frames = depicted.Select(source => source.Frame).ToList();
        var encoded = EvolutionGifCodec.Encode(frames, durations, request.Loop, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var artifactSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(encoded));
        var manifest = ManifestJson(projectId, ordered, depicted, durations, request.Loop, selectionHash, artifactSha256, encoded.Length);

        var canvasWidth = frames.Max(frame => frame.Width);
        var canvasHeight = frames.Max(frame => frame.Height);

        var artifact = new EvolutionGif(
            Guid.NewGuid(),
            owner.ScopeId,
            projectId,
            manifest,
            selectionHash,
            artifactSha256,
            encoded,
            canvasWidth,
            canvasHeight,
            frames.Count,
            durations.Sum(),
            request.Loop,
            EvolutionGifStatus.Draft,
            approvedBy: null,
            approvedAt: null,
            key,
            clock.GetUtcNow());

        var artifactId = artifact.Id;
        var inserted = await unitOfWork.ExecuteAsync(
            async token =>
            {
                if (!await gifs.InsertAsync(artifact, token).ConfigureAwait(false))
                {
                    return false;
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    EvolutionGifRenderedAction,
                    artifactId,
                    1,
                    EvolutionGif.Label(artifactId)));

                return true;
            },
            cancellationToken).ConfigureAwait(false);

        if (!inserted)
        {
            if (key is not null)
            {
                var raced = await gifs
                    .FindByIdempotencyKeyAsync(owner.ScopeId, projectId, key, cancellationToken)
                    .ConfigureAwait(false);

                if (raced is not null)
                {
                    return ReplayOrConflict(raced, selectionHash, key);
                }
            }

            throw new InternalFailureException("The rendered animation could not be recorded.");
        }

        logger.LogInformation(
            "Evolution GIF {GifId} rendered for project {ProjectId}: {Frames} frames, {TotalMs}ms, loop {Loop}.",
            artifactId,
            projectId,
            frames.Count,
            durations.Sum(),
            request.Loop);

        return new RenderEvolutionGifOutcome(artifact, Created: true);
    }

    /// <inheritdoc />
    public async Task<(EvolutionGif Artifact, bool SelectionCurrent)> ReadAsync(
        OwnerScope owner,
        Guid gifId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var artifact = await gifs.FindAsync(owner.ScopeId, gifId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("evolution GIF");

        return (artifact, await SelectionCurrentAsync(owner, artifact, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EvolutionGif>> ListAsync(
        OwnerScope owner,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return gifs.ListAsync(
            owner.ScopeId,
            projectId,
            Math.Clamp(limit, 1, ListQueryPolicy.MaxPageSize),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApproveEvolutionGifOutcome> ApproveAsync(
        OwnerScope owner,
        Guid gifId,
        string expectedSelectionHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSelectionHash);

        return await unitOfWork.ExecuteAsync(
            token => ApproveCoreAsync(owner, gifId, expectedSelectionHash, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(byte[] Png, int Width, int Height)> PreviewStaticAsync(
        OwnerScope owner,
        Guid gifId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var artifact = await gifs.FindAsync(owner.ScopeId, gifId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("evolution GIF");

        // Derived from the stored bytes, not re-rendered: the static preview is exactly the
        // frame playback starts with, so what a reduced-motion owner sees is what approval saw.
        if (!EvolutionGifCodec.TryDecodeFirstFrame(artifact.ArtifactBytes, out var first, out var width, out var height) ||
            first is null)
        {
            throw new InternalFailureException("The stored animation bytes do not decode.");
        }

        return (EvolutionGifCodec.EncodeGreyPng(first), width, height);
    }

    /// <inheritdoc />
    public async Task<EvolutionGifExport> PreviewAsync(
        OwnerScope owner,
        Guid gifId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var artifact = await gifs.FindAsync(owner.ScopeId, gifId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("evolution GIF");

        var live = await SelectionCurrentAsync(owner, artifact, cancellationToken).ConfigureAwait(false);

        return new EvolutionGifExport(
            artifact.ArtifactBytes, artifact.ArtifactSha256, ApprovalLive: artifact.IsApproved && live);
    }

    /// <inheritdoc />
    public async Task<EvolutionGifExport> ExportAsync(
        OwnerScope owner,
        Guid gifId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var artifact = await gifs.FindAsync(owner.ScopeId, gifId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("evolution GIF");

        if (!artifact.IsApproved)
        {
            throw new ApprovalRequiredException();
        }

        if (!await SelectionCurrentAsync(owner, artifact, cancellationToken).ConfigureAwait(false))
        {
            throw new ApprovalStaleException("selection");
        }

        return new EvolutionGifExport(artifact.ArtifactBytes, artifact.ArtifactSha256, ApprovalLive: true);
    }

    // -----------------------------------------------------------------------------------------
    // Rendering.
    // -----------------------------------------------------------------------------------------

    private async Task<ApproveEvolutionGifOutcome> ApproveCoreAsync(
        OwnerScope owner,
        Guid gifId,
        string expectedSelectionHash,
        CancellationToken cancellationToken)
    {
        var artifact = await gifs.FindAsync(owner.ScopeId, gifId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("evolution GIF");

        if (artifact.IsApproved)
        {
            return new ApproveEvolutionGifOutcome(artifact, AlreadyApproved: true);
        }

        if (!await SelectionCurrentAsync(owner, artifact, cancellationToken).ConfigureAwait(false))
        {
            // The selection moved since the render: no approval can authorize a manifest nobody
            // reviewed, so the owner reads the current one and approves that instead.
            throw new EvolutionGifSelectionSupersededException();
        }

        if (!string.Equals(expectedSelectionHash, artifact.SelectionHash, StringComparison.Ordinal))
        {
            throw new EvolutionGifSelectionSupersededException();
        }

        var approvedAt = clock.GetUtcNow();

        if (!await gifs
            .TryApproveAsync(owner.ScopeId, gifId, owner.ActorId, approvedAt, cancellationToken)
            .ConfigureAwait(false))
        {
            var reread = await gifs.FindAsync(owner.ScopeId, gifId, cancellationToken).ConfigureAwait(false)
                ?? throw new ResourceNotFoundException("evolution GIF");

            if (reread.IsApproved)
            {
                return new ApproveEvolutionGifOutcome(reread, AlreadyApproved: true);
            }

            throw new EvolutionGifSelectionSupersededException();
        }

        audit.Record(new DomainAudit(
            owner.ActorId,
            EvolutionGifApprovedAction,
            gifId,
            1,
            EvolutionGif.Label(gifId)));

        logger.LogInformation(
            "Evolution GIF {GifId} approved by {ActorId}.",
            gifId,
            owner.ActorId);

        return new ApproveEvolutionGifOutcome(
            await gifs.FindAsync(owner.ScopeId, gifId, cancellationToken).ConfigureAwait(false)
                ?? throw new ResourceNotFoundException("evolution GIF"),
            AlreadyApproved: false);
    }

    private static RenderEvolutionGifOutcome ReplayOrConflict(
        EvolutionGif replay,
        string selectionHash,
        string key)
    {
        if (string.Equals(replay.SelectionHash, selectionHash, StringComparison.Ordinal))
        {
            return new RenderEvolutionGifOutcome(replay, Created: false);
        }

        throw new OperationIdempotencyConflictException(key, replay.SelectionHash);
    }

    // -----------------------------------------------------------------------------------------
    // Loading.
    // -----------------------------------------------------------------------------------------

    private static IReadOnlyList<Guid> RequireMilestoneIds(IReadOnlyList<Guid>? milestoneIds)
    {
        if (milestoneIds is null || milestoneIds.Count == 0)
        {
            throw new ValidationFailedException("milestoneIds", "An evolution GIF animates named milestones.");
        }

        if (milestoneIds.Any(id => id == Guid.Empty))
        {
            throw new ValidationFailedException("milestoneIds", "Every milestone id must be non-empty.");
        }

        if (milestoneIds.Distinct().Count() != milestoneIds.Count)
        {
            throw new ValidationFailedException("milestoneIds", "Every milestone is named once.");
        }

        if (milestoneIds.Count < EvolutionGifLimits.MinFrames ||
            milestoneIds.Count > EvolutionGifLimits.MaxFrames)
        {
            throw new ValidationFailedException(
                "milestoneIds",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"An evolution GIF animates {EvolutionGifLimits.MinFrames} to {EvolutionGifLimits.MaxFrames} milestones."));
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

    private static IReadOnlyList<int> RequireDurations(IReadOnlyList<int>? durationsMs, int frameCount)
    {
        IReadOnlyList<int> resolved = durationsMs switch
        {
            null => Enumerable.Repeat(EvolutionGifLimits.DefaultFrameDurationMs, frameCount).ToList(),
            { Count: 1 } => Enumerable.Repeat(durationsMs[0], frameCount).ToList(),
            _ => durationsMs,
        };

        if (resolved.Count != frameCount)
        {
            throw new ValidationFailedException(
                "durationsMs",
                "Durations name one value for every frame, a single value for all frames, or nothing.");
        }

        foreach (var duration in resolved)
        {
            if (duration < EvolutionGifLimits.MinFrameDurationMs ||
                duration > EvolutionGifLimits.MaxFrameDurationMs ||
                duration % 10 != 0)
            {
                throw new ValidationFailedException(
                    "durationsMs",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Every frame shows {EvolutionGifLimits.MinFrameDurationMs} to {EvolutionGifLimits.MaxFrameDurationMs} whole-centisecond milliseconds."));
            }
        }

        if (resolved.Sum() > EvolutionGifLimits.MaxTotalDurationMs)
        {
            throw new ValidationFailedException(
                "durationsMs",
                "The total playback fits sixty seconds.");
        }

        return resolved;
    }

    private static string? RequireIdempotencyKey(string? key)
    {
        if (key is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > EvolutionGifLimits.IdempotencyKeyMaxLength)
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
                    "An evolution GIF animates confirmed milestones.");
            }

            loaded.Add(milestone);
        }

        return loaded;
    }

    private sealed record DepictedFrame(
        Milestone Milestone,
        CuratedScreenshot Picture,
        string MediaSha256,
        long EvidenceRevision,
        GreyImage Frame);

    private async Task<IReadOnlyList<DepictedFrame>> LoadDepictedAsync(
        OwnerScope owner,
        IReadOnlyList<Milestone> frameOrder,
        HashSet<Guid> pins,
        CancellationToken cancellationToken)
    {
        var sources = new List<DepictedFrame>(frameOrder.Count);
        var pinned = new HashSet<Guid>(pins);

        foreach (var milestone in frameOrder)
        {
            var pictures = await curation
                .ListAsync(owner.ScopeId, milestone.Id, ListQueryPolicy.MaxPageSize, cancellationToken)
                .ConfigureAwait(false);

            var selected = pictures.Where(picture => picture.IsSelected).ToList();
            var head = await evidence
                .FindHeadAsync(owner.ScopeId, milestone.Id, cancellationToken)
                .ConfigureAwait(false);

            CuratedScreenshot? chosen = selected.FirstOrDefault(picture => pinned.Contains(picture.Id));
            if (chosen is not null)
            {
                pinned.Remove(chosen.Id);
            }
            else
            {
                chosen = selected.FirstOrDefault(picture => picture.IsPrimary)
                    ?? selected.OrderBy(picture => picture.DisplayOrder).ThenBy(picture => picture.CreatedAt).FirstOrDefault();
            }

            if (chosen is null)
            {
                // An animation has no gap band: a milestone with no selected picture cannot be a
                // frame, so the request fails rather than fabricating one.
                throw new InsufficientSelectedException(Insufficiency(milestone.Id));
            }

            var media = await OpenSourceAsync(owner, chosen, cancellationToken).ConfigureAwait(false);

            if (!EvolutionPng.TryDecodeToGrey(media, out var grey) || grey is null)
            {
                throw new ValidationFailedException(
                    "selectedScreenshotIds",
                    "A selected screenshot's bytes are not a supported image.");
            }

            sources.Add(new DepictedFrame(
                milestone,
                chosen,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(media)),
                head?.Revision ?? 0,
                grey));
        }

        if (pinned.Count > 0)
        {
            // Every pin must be a selected picture of a requested milestone. Anything else is
            // either a candidate - which is never depicted - or a picture from another
            // milestone, and both are refused rather than silently dropped.
            throw new ValidationFailedException(
                "selectedScreenshotIds",
                "Every named picture must be selected for one of the requested milestones.");
        }

        return sources;
    }

    private static string Insufficiency(Guid milestoneId) =>
        string.Create(CultureInfo.InvariantCulture, $"milestone {milestoneId:D} depicts no selected screenshot");

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
            throw new EvolutionGifMediaUnavailableException(mediaId);
        }
    }

    private static void RequireDepicted(IReadOnlyList<DepictedFrame> depicted)
    {
        if (depicted.Count < EvolutionGifLimits.MinFrames)
        {
            throw new InsufficientSelectedException(
                string.Join(",", depicted.Select(source => source.Milestone.Id.ToString("D"))));
        }
    }

    // -----------------------------------------------------------------------------------------
    // Selection hash and manifest.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The canonical identity of what was animated: the frame order, per frame the depicted
    /// picture's media, revision and caption revisions beside the evidence revision it was read
    /// against, and the playback - durations and loop - because those are reviewed too.
    /// </summary>
    private static string SelectionHash(
        IReadOnlyList<DepictedFrame> depicted,
        IReadOnlyList<int> durations,
        bool loop)
    {
        var lines = new List<string>(depicted.Count + 1)
        {
            string.Create(CultureInfo.InvariantCulture, $"gif|loop:{(loop ? "true" : "false")}")
        };

        for (var index = 0; index < depicted.Count; index++)
        {
            var source = depicted[index];
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{index.ToString(CultureInfo.InvariantCulture)}|{source.Milestone.Id:D}|{source.Picture.Id:D}" +
                $"|{source.Picture.Revision.ToString(CultureInfo.InvariantCulture)}" +
                $"|{source.Picture.MediaId:D}|{source.MediaSha256}|{source.Picture.Caption}" +
                $"|{durations[index].ToString(CultureInfo.InvariantCulture)}" +
                $"|{source.EvidenceRevision.ToString(CultureInfo.InvariantCulture)}"));
        }

        return ContentApproval.Hash(string.Join("\n", lines));
    }

    private static string ManifestJson(
        Guid projectId,
        IReadOnlyList<Milestone> requested,
        IReadOnlyList<DepictedFrame> depicted,
        IReadOnlyList<int> durations,
        bool loop,
        string selectionHash,
        string artifactSha256,
        long byteLength)
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
            requestedMilestones = requested.Select(milestone => milestone.Id.ToString("D")).ToArray(),
            chronologicalMilestones = chronological,
            frameOrder = depicted.Select(source => source.Milestone.Id.ToString("D")).ToArray(),
            durationsMs = durations.ToArray(),
            loop,
            frameCount = depicted.Count,
            totalDurationMs = durations.Sum(),
            byteLength,
            sources = depicted.Select((source, index) => new
            {
                frameIndex = index,
                milestoneId = source.Milestone.Id.ToString("D"),
                screenshotId = source.Picture.Id.ToString("D"),
                mediaId = source.Picture.MediaId.ToString("D"),
                screenshotRevision = source.Picture.Revision,
                evidenceRevision = source.EvidenceRevision,
                mediaSha256 = source.MediaSha256,
                caption = source.Picture.Caption,
                viewport = source.Picture.Viewport.Name,
                capturedAt = source.Picture.CapturedAt.ToString("o"),
                delayMs = durations[index],
                width = source.Frame.Width,
                height = source.Frame.Height,
            }).ToArray(),
            selectionHash,
            artifactSha256,
        };

        return JsonSerializer.Serialize(manifest);
    }

    private async Task<bool> SelectionCurrentAsync(
        OwnerScope owner,
        EvolutionGif artifact,
        CancellationToken cancellationToken)
    {
        using var manifest = JsonDocument.Parse(artifact.ManifestJson);
        var root = manifest.RootElement;

        if (!root.TryGetProperty("frameOrder", out var frameOrder) ||
            !root.TryGetProperty("durationsMs", out var durationsProperty) ||
            !root.TryGetProperty("loop", out var loopProperty))
        {
            return false;
        }

        var milestoneIds = frameOrder.EnumerateArray()
            .Select(id => Guid.TryParse(id.GetString(), out var parsed) ? parsed : Guid.Empty)
            .ToList();

        if (milestoneIds.Any(id => id == Guid.Empty))
        {
            return false;
        }

        var pins = root.GetProperty("sources").EnumerateArray()
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

        IReadOnlyList<DepictedFrame> depicted;
        try
        {
            depicted = await LoadDepictedAsync(owner, ordered, pins, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ApiException or InvalidOperationException)
        {
            return false;
        }

        var durations = durationsProperty.EnumerateArray().Select(delay => delay.GetInt32()).ToList();
        var loop = loopProperty.GetBoolean();

        if (durations.Count != depicted.Count)
        {
            return false;
        }

        return string.Equals(SelectionHash(depicted, durations, loop), artifact.SelectionHash, StringComparison.Ordinal);
    }
}
