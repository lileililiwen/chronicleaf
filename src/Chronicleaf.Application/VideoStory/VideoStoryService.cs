using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Voice;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.VideoStory;

/// <summary>
/// The video-story use cases over the delivered evidence, voice, asset, review, media and
/// audit contracts (VIDEO-STORY-SCRIPTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A script is validated, never rendered, and never sent.</b> The source is an approved
/// <c>markdown-story</c> revision; the shots are the owner's own list, checked shot by shot
/// against the frozen evidence head the owner named. There is no provider behind this service,
/// so there is no consent to check and no key to lease - and no renderer, encoder or transport
/// behind it either, so there is nothing a caller could supply that would reach a screen
/// recorder, a video API or a publishing endpoint. The finished scripts are text a human acts
/// on: the storyboard, its narration and its captions.
/// </para>
/// <para>
/// <b>Approval is reused, never redefined.</b> The source must carry a live approval that still
/// describes the stored state, under exactly the bindings the review package's export re-checks.
/// The derivative itself starts with no approval row at all, and the export re-checks every one
/// of those bindings again before serving a byte - never approved is
/// <c>approval_required</c>, once approved but moved is <c>approval_stale</c>. A newer
/// unapproved revision ends the approval too: a script the owner already rewrote is no longer
/// the text anybody reviewed, even when the approved bytes are still on disk.
/// </para>
/// <para>
/// <b>Voice precedence is structural.</b> The narration and captions are checked against the
/// profile's resolved effective values before anything is stored, so a later profile edit
/// cannot reach them - and the stored revision carries that snapshot as a value, so the approval
/// judges the words under the voice they were written in.
/// </para>
/// <para>
/// <b>Nothing here logs prose.</b> The logger receives identifiers, codes and counts. The shots,
/// the captions, the narration and the evidence texts travel from a stored document into a
/// stored revision; none appears in a log line, in an exception message or in an audit detail.
/// </para>
/// </remarks>
public sealed class VideoStoryService(
    IMilestoneStore milestones,
    IProjectStore projects,
    IEvidenceStore evidence,
    IVoiceProfileService voice,
    IContentAssetStore assets,
    IContentReviewStore reviews,
    IMediaStore media,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<VideoStoryService> logger) : IVideoStoryService
{
    /// <summary>The audit action recorded when a storyboard is generated.</summary>
    public const string StoryGeneratedAction = "video-story.generated";

    /// <summary>The audit action recorded when an approved storyboard is exported.</summary>
    public const string StoryExportedAction = "video-story.exported";

    /// <summary>The fixed timestamp every ZIP entry carries, so the same script packs identically.</summary>
    public static readonly DateTimeOffset ArchiveTimestamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<GeneratedVideoStory> GenerateAsync(
        OwnerScope owner,
        Guid milestoneId,
        GenerateVideoStoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (milestoneId == Guid.Empty)
        {
            throw new ValidationFailedException("milestoneId", "A video story belongs to a milestone, so name one.");
        }

        if (request.SourceAssetId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "sourceAssetId",
                "A video story is derived from the milestone's approved Markdown story, so name it.");
        }

        // The shape first: a list that does not cover the runtime exactly is refused before the
        // database is read, because no stored state could make gaps or overlaps cover anything.
        // The limitations travel inside the stored document, but they are the summary's, not the
        // request's: the document below is rebuilt with the approved source's own limitations
        // once the source is read, rather than trusting a caller to quote them.
        var board = VideoStoryBoard.Validate(request.DurationSeconds, request.Shots, []);

        var prepared = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var milestone = await milestones
                    .FindAsync(owner.ScopeId, milestoneId, token)
                    .ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("milestone");

                var project = await projects
                    .FindAsync(owner.ScopeId, milestone.ProjectId, token)
                    .ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                return new PreparedStory(milestone.ProjectId, milestone.Title, milestoneId);
            },
            cancellationToken).ConfigureAwait(false);

        var sourceHead = await assets
            .FindAsync(owner.ScopeId, request.SourceAssetId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        // A script is derived from the milestone's approved Markdown story, never from another
        // derivative: deriving one script from another would let provenance drift one hop at a
        // time until no row names the evidence anything was checked against.
        if (!string.Equals(sourceHead.Type, ContentAssetTypes.MarkdownStory, StringComparison.Ordinal))
        {
            throw new ValidationFailedException(
                "sourceAssetId",
                "A video story is derived from the milestone's approved Markdown story, "
                + "not from another asset.");
        }

        if (sourceHead.MilestoneId != milestoneId)
        {
            throw new ResourceNotFoundException("content asset");
        }

        if (sourceHead.CurrentRevision < 1)
        {
            throw new ResourceNotFoundException("content asset");
        }

        var sourceRevision = await assets
            .FindRevisionAsync(owner.ScopeId, request.SourceAssetId, sourceHead.CurrentRevision, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        // The approval bindings, re-checked rather than redefined: a live row must name the head
        // revision and its body hash, and the evidence head must still be the one the approval was
        // bound to. Never approved and once approved are two different answers, exactly as on the
        // export route.
        var sourceApprovals = await reviews
            .ListForAssetAsync(owner.ScopeId, request.SourceAssetId, cancellationToken)
            .ConfigureAwait(false);

        var live = sourceApprovals.FirstOrDefault(approval => approval.IsLive);

        if (live is null)
        {
            if (sourceApprovals.Count == 0)
            {
                throw new ApprovalRequiredException();
            }

            throw new ApprovalStaleException("invalidated");
        }

        if (live.ContentRevision != sourceHead.CurrentRevision ||
            !string.Equals(live.BodyHash, sourceRevision.BodyHash, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("revision");
        }

        var head = await evidence
            .FindHeadAsync(owner.ScopeId, milestoneId, cancellationToken)
            .ConfigureAwait(false);

        if (head is null ||
            head.Id != sourceRevision.EvidenceBundleId ||
            head.Revision != live.EvidenceRevision)
        {
            throw new ApprovalStaleException("evidence");
        }

        // The revisions the owner was looking at must still be current. An owner who scripted
        // against a head that has since moved gets the same stale answer the export gives, not a
        // script bound to provenance they did not see.
        if (request.EvidenceRevision != head.Revision)
        {
            throw new RevisionConflictException(head.Revision);
        }

        var profile = await voice
            .GetAsync(owner.ScopeId, prepared.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        if (request.VoiceRevision != profile.Revision)
        {
            throw new RevisionConflictException(profile.Revision);
        }

        var summary = GroundedSummary.FromCanonicalJson(sourceRevision.SummaryJson)
            ?? throw UnreadableContentRow();

        var claims = ClaimMap.Build(summary);

        // The stored revision's own grounding, recomputed from the frozen bundle it names. A claim
        // citing an item the bundle does not hold means the source moved under its approval, and a
        // script of it would cite nothing.
        var availability = await assets.ResolveAvailabilityAsync(
                owner.ScopeId,
                sourceRevision.EvidenceBundleId,
                [.. claims.SelectMany(claim => claim.EvidenceIds).Distinct()],
                cancellationToken)
            .ConfigureAwait(false);

        var stored = GroundingValidator.ForStoredRevision(claims, availability);

        if (stored.Any(finding =>
                string.Equals(finding.Rule, GroundingRule.ClaimEvidenceMissing, StringComparison.Ordinal)))
        {
            throw new ApprovalStaleException("evidence");
        }

        var cited = new HashSet<Guid>(claims.SelectMany(claim => claim.EvidenceIds));
        var corpus = string.Join(
            "\n",
            head.Items
                .Where(item => cited.Contains(item.Id))
                .Select(item => item.Text));

        // Every depicted picture must be a complete screenshot with stored bytes in the frozen
        // bundle the script is grounded in. A name that matches nothing depictable is an invented
        // picture, not a manifest with a hole in it - and the bytes are opened now, before
        // anything is stored, rather than discovered missing at export.
        var depictable = head.Items
            .Where(item => string.Equals(item.Kind, EvidenceKinds.Screenshot, StringComparison.Ordinal)
                && item.MediaId is not null
                && string.Equals(item.Availability, EvidenceAvailability.Complete, StringComparison.Ordinal))
            .Select(item => item.MediaId!.Value)
            .ToHashSet();

        var bundleEvidenceIds = head.Items.Select(item => item.Id).ToHashSet();

        for (var index = 0; index < board.Shots.Count; index++)
        {
            var shot = board.Shots[index];
            string field = $"shots[{index}]";

            if (!depictable.Contains(shot.ScreenshotMediaId))
            {
                throw new InvalidStorySchemaException(
                    $"{field}.screenshotMediaId",
                    "A shot depicts a selected screenshot from the frozen evidence: "
                    + "a missing picture is selected or omitted, never invented.");
            }

            try
            {
                var content = await media.OpenAsync(owner, shot.ScreenshotMediaId, cancellationToken).ConfigureAwait(false);
                await using (content.ConfigureAwait(false))
                {
                    if (!string.Equals(content.Ref.MediaType, MediaTypes.Png, StringComparison.Ordinal))
                    {
                        throw new UnsupportedMediaTypeException();
                    }
                }
            }
            catch (MediaNotFoundException)
            {
                throw new InvalidStorySchemaException(
                    $"{field}.screenshotMediaId",
                    "A shot depicts a selected screenshot from the frozen evidence: "
                    + "a missing picture is selected or omitted, never invented.");
            }

            foreach (var evidenceId in shot.SourceEvidenceIds)
            {
                if (!bundleEvidenceIds.Contains(evidenceId))
                {
                    throw new InvalidStorySchemaException(
                        $"{field}.sourceEvidenceIds",
                        "A shot cites evidence the frozen bundle holds: "
                        + "an unknown id is omitted, never cited.");
                }
            }

            // The owner's own words are checked shot by shot before anything is stored, so a
            // figure the evidence never stated, or a word the voice forbids, is refused naming
            // the shot that carried it rather than the script as a whole. The timings beside
            // them are machine values, not claims, so they are not read as figures.
            ThrowWhenUnprovenNumber($"{field}.caption", shot.Caption, corpus);
            ThrowWhenUnprovenNumber($"{field}.narration", shot.Narration, corpus);
            ThrowWhenVoiceFinding($"{field}.narration", shot.Narration, profile.Resolved.Effective);
            ThrowWhenVoiceFinding($"{field}.caption", shot.Caption, profile.Resolved.Effective);
        }

        var document = new VideoStoryBoard.Document(
            board.DurationSeconds,
            board.Shots,
            [.. summary.KnownLimitations.Select(limitation => limitation.Text)]);

        var body = VideoStoryBoard.ToCanonicalJson(document);
        var now = clock.GetUtcNow();

        var committed = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var asset = await assets.FindForMilestoneAsync(
                        owner.ScopeId, milestoneId, ContentAssetTypes.VideoStory, token)
                    .ConfigureAwait(false);

                long expected;

                if (asset is null)
                {
                    var created = new ContentAsset(
                        Guid.NewGuid(),
                        owner.ScopeId,
                        prepared.ProjectId,
                        milestoneId,
                        ContentAssetTypes.VideoStory,
                        0,
                        now,
                        now);

                    if (await assets.TryCreateAsync(created, token).ConfigureAwait(false))
                    {
                        asset = created;
                        expected = 0;
                    }
                    else
                    {
                        asset = await assets.FindForMilestoneAsync(
                                owner.ScopeId, milestoneId, ContentAssetTypes.VideoStory, token)
                            .ConfigureAwait(false)
                            ?? throw new ResourceNotFoundException("content asset");
                        expected = asset.CurrentRevision;
                    }
                }
                else
                {
                    expected = asset.CurrentRevision;
                }

                var revision = new ContentRevision(
                    Guid.NewGuid(),
                    asset.Id,
                    owner.ScopeId,
                    expected + 1,
                    sourceRevision.EvidenceBundleId,
                    sourceRevision.EvidenceRevision,
                    profile.Revision,
                    VoiceSnapshotCodec.Serialize(new VoiceSnapshot(
                        profile.Resolved.PresetId,
                        profile.Revision,
                        now,
                        profile.Resolved.Effective)),
                    sourceRevision.SummaryJson,
                    body,
                    ContentRevision.Hash(body),
                    "local-template",
                    VideoStoryBoard.TemplateVersion,
                    VideoStoryBoard.TemplateVersion,
                    sourceRevision.SchemaVersion,
                    now);

                if (!await assets
                        .TryAppendRevisionAsync(revision, expected, now, token)
                        .ConfigureAwait(false))
                {
                    var reread = await assets.FindAsync(owner.ScopeId, asset.Id, token).ConfigureAwait(false);
                    throw new RevisionConflictException(reread?.CurrentRevision ?? asset.CurrentRevision);
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    StoryGeneratedAction,
                    asset.Id,
                    revision.Revision,
                    // Identifiers, revisions and the derivation chain. Not the script, not one byte
                    // of the evidence, not a caption and not a spoken line: this row outlives
                    // the request.
                    $"source={request.SourceAssetId:D};"
                        + $"sourceRevision={sourceRevision.Revision.ToString(CultureInfo.InvariantCulture)};"
                        + $"evidenceRevision={sourceRevision.EvidenceRevision.ToString(CultureInfo.InvariantCulture)};"
                        + $"voiceRevision={profile.Revision.ToString(CultureInfo.InvariantCulture)};"
                        + $"durationSeconds={document.DurationSeconds.ToString(CultureInfo.InvariantCulture)};"
                        + $"shots={document.Shots.Count.ToString(CultureInfo.InvariantCulture)};"));

                return (asset.Id, revision.Revision, revision.BodyHash);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Video story generated for milestone {MilestoneId} as asset {AssetId} revision {Revision}.",
            milestoneId,
            committed.Item1,
            committed.Item2);

        return new GeneratedVideoStory(
            committed.Item1,
            committed.Item2,
            committed.Item3,
            document.DurationSeconds,
            document.Shots.Count,
            document.TotalMs,
            request.SourceAssetId,
            sourceRevision.Revision,
            sourceRevision.EvidenceRevision,
            profile.Revision,
            document.ImageOrder);
    }

    /// <inheritdoc />
    public async Task<VideoStorySummary> ReadForMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (milestoneId == Guid.Empty)
        {
            throw new ValidationFailedException("milestoneId", "A video story belongs to a milestone, so name one.");
        }

        // The milestone is read first and scoped, so a milestone in another installation is a 404
        // rather than an empty script that happens to look like "nothing scripted yet".
        _ = await milestones.FindAsync(owner.ScopeId, milestoneId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("milestone");

        var asset = await assets
            .FindForMilestoneAsync(owner.ScopeId, milestoneId, ContentAssetTypes.VideoStory, cancellationToken)
            .ConfigureAwait(false);

        if (asset is null || asset.CurrentRevision < 1)
        {
            return new VideoStorySummary(
                null, 0, string.Empty, 0, 0, 0, 0, clock.GetUtcNow());
        }

        var revision = await assets
            .FindRevisionAsync(owner.ScopeId, asset.Id, asset.CurrentRevision, cancellationToken)
            .ConfigureAwait(false);

        // The discovery read names what the row holds without interpreting it: a row this build
        // cannot parse is still a stored script, and its body stays previewable through the
        // asset read rather than hidden here.
        var document = VideoStoryBoard.FromCanonicalJson(revision?.BodyMarkdown);

        return new VideoStorySummary(
            asset.Id,
            asset.CurrentRevision,
            revision?.BodyHash ?? string.Empty,
            document?.DurationSeconds ?? 0,
            document?.Shots.Count ?? 0,
            revision?.EvidenceRevision ?? 0,
            revision?.VoiceRevision ?? 0,
            asset.UpdatedAt);
    }

    /// <inheritdoc />
    public async Task<VideoStoryExport> ExportAsync(
        OwnerScope owner,
        Guid assetId,
        long? revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (assetId == Guid.Empty)
        {
            throw new ValidationFailedException("assetId", "An asset id is required.");
        }

        var asset = await assets.FindAsync(owner.ScopeId, assetId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        // The video-story shape is served for video stories only: any other asset already has
        // its own export, and answering it here in a second shape would let two formats disagree
        // about what is authorized.
        if (!string.Equals(asset.Type, ContentAssetTypes.VideoStory, StringComparison.Ordinal))
        {
            throw new ValidationFailedException(
                "format",
                "The video-story package serves video stories. Name format=markdown or format=html "
                + "for any other asset.");
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

        // A newer unapproved revision ends the approval even when the approved bytes are still
        // on disk: the owner already rewrote the script, so the reviewed text is no longer the
        // text they are looking at. The caption or order change the design names always lands as
        // a new revision - shots are immutable - so this one comparison covers both.
        if (asset.CurrentRevision != live.ContentRevision)
        {
            throw new ApprovalStaleException("revision");
        }

        // An export names the revision it wants, or takes the approved one. Either way the
        // approval has to name exactly that revision.
        var wanted = revision ?? live.ContentRevision;
        if (wanted != live.ContentRevision)
        {
            throw new ApprovalStaleException("revision");
        }

        var storedRevision = await assets
            .FindRevisionAsync(owner.ScopeId, assetId, wanted, cancellationToken)
            .ConfigureAwait(false)
            ?? throw UnreadableContentRow();

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

        if (!string.Equals(
                ContentApproval.MediaSelectionHashFor(SelectedPictures(head)),
                live.MediaSelectionHash,
                StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("media");
        }

        var document = VideoStoryBoard.FromCanonicalJson(storedRevision.BodyMarkdown)
            ?? throw UnreadableContentRow();

        // The pictures the shots depict, in first-appearance order, with the bytes the media
        // store holds right now. A picture whose bytes are gone ends the export with
        // media_unavailable: the approval bound pictures the artifact would have to carry, so
        // serving the scripts without them would be a silent claim of completeness. The scripts
        // themselves stay previewable through the review package's own routes.
        var images = new List<ExportedImage>();
        var seen = new HashSet<Guid>();

        foreach (var shot in document.Shots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seen.Add(shot.ScreenshotMediaId))
            {
                continue;
            }

            MediaContent content;
            try
            {
                content = await media.OpenAsync(owner, shot.ScreenshotMediaId, cancellationToken).ConfigureAwait(false);
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

                var bytes = await ReadFullyAsync(content.Content, cancellationToken).ConfigureAwait(false);

                images.Add(new ExportedImage(shot.ScreenshotMediaId, shot.Caption, content.Ref.MediaType, bytes));
            }
        }

        var storyboardBytes = Encoding.UTF8.GetBytes(storedRevision.BodyMarkdown);
        var narrationBytes = Encoding.UTF8.GetBytes(
            VideoStoryBoard.RenderNarrationMarkdown(
                (GroundedSummary.FromCanonicalJson(storedRevision.SummaryJson)
                    ?? throw UnreadableContentRow()).MilestoneTitle,
                document));
        var srtBytes = Encoding.UTF8.GetBytes(VideoStoryBoard.RenderSrt(document));
        var manifest = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                assetId = asset.Id,
                revision = storedRevision.Revision,
                bodyHash = storedRevision.BodyHash,
                evidenceRevision = storedRevision.EvidenceRevision,
                durationSeconds = document.DurationSeconds,
                storyboardFile = StoryboardFileName,
                narrationFile = NarrationFileName,
                captionFile = CaptionFileName,
                imageOrder = images.Select(image => image.MediaId).ToArray(),
                images = images.Select(image => new
                {
                    mediaId = image.MediaId,
                    caption = image.Caption,
                    file = ImageFileName(image.MediaId),
                    sha256 = Convert.ToHexString(SHA256.HashData(image.Bytes)).ToLowerInvariant(),
                    mediaType = image.MediaType,
                }).ToArray(),
                files = new[]
                {
                    new { file = StoryboardFileName, sha256 = Convert.ToHexString(SHA256.HashData(storyboardBytes)).ToLowerInvariant() },
                    new { file = NarrationFileName, sha256 = Convert.ToHexString(SHA256.HashData(narrationBytes)).ToLowerInvariant() },
                    new { file = CaptionFileName, sha256 = Convert.ToHexString(SHA256.HashData(srtBytes)).ToLowerInvariant() },
                },
            },
            ManifestJson);

        var packed = PackArchive(storyboardBytes, narrationBytes, srtBytes, manifest, images, cancellationToken);

        await unitOfWork.ExecuteAsync(
            token =>
            {
                // The audit row names the revision and the approval, never the prose: an export
                // is a record that bytes left the building, not a copy of them.
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    StoryExportedAction,
                    asset.Id,
                    storedRevision.Revision,
                    $"revision={storedRevision.Revision.ToString(CultureInfo.InvariantCulture)};"
                    + $"approval={live.Id:D};format=video-story;"));
                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Video story asset {AssetId} revision {Revision} exported.",
            assetId,
            storedRevision.Revision);

        return new VideoStoryExport(
            $"chronicleaf-{assetId:N}-r{storedRevision.Revision}-video-story.zip",
            "application/zip",
            packed,
            Convert.ToHexString(SHA256.HashData(packed)).ToLowerInvariant(),
            asset.Id,
            storedRevision.Revision);
    }

    /// <inheritdoc />
    public Task PublishAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // Existence is the only state publication consults, so that a foreign id is still the
        // delivered 404 rather than a transport refusal that confirms nothing. Everything else
        // about the asset - approved, edited, moved - is irrelevant: there is no renderer, no
        // encoder, no account and no transport for any of it to travel on.
        return PublishAfterResolveAsync(owner, assetId, cancellationToken);
    }

    private async Task PublishAfterResolveAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        _ = await assets.FindAsync(owner.ScopeId, assetId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        // No credential is leased, no video is synthesized, no job is enqueued, no audit row
        // is written and no socket is opened: a refusal that changed anything would be an
        // account action of its own.
        throw new VideoStoryPublishUnsupportedException();
    }

    /// <summary>The storyboard file inside the export package.</summary>
    public const string StoryboardFileName = "storyboard.json";

    /// <summary>The narration file inside the export package.</summary>
    public const string NarrationFileName = "narration.md";

    /// <summary>The caption file inside the export package.</summary>
    public const string CaptionFileName = "caption.srt";

    /// <summary>The manifest file inside the export package.</summary>
    public const string ManifestFileName = "manifest.json";

    private static string ImageFileName(Guid mediaId) => $"images/{mediaId:N}.png";

    private static byte[] PackArchive(
        byte[] storyboardBytes,
        byte[] narrationBytes,
        byte[] srtBytes,
        byte[] manifestBytes,
        IReadOnlyList<ExportedImage> images,
        CancellationToken cancellationToken)
    {
        using var packed = new MemoryStream();

        using (var archive = new ZipArchive(packed, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The fixed order and the fixed timestamp are what make two exports of the same
            // approved revision agree byte for byte: nothing here records when the export ran.
            // The images follow the shots' first-appearance order, not a sorted one: the
            // arrangement is the decision the approval covers.
            WriteEntry(archive, StoryboardFileName, storyboardBytes);
            WriteEntry(archive, NarrationFileName, narrationBytes);
            WriteEntry(archive, CaptionFileName, srtBytes);

            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteEntry(archive, ImageFileName(image.MediaId), image.Bytes);
            }

            WriteEntry(archive, ManifestFileName, manifestBytes);

            cancellationToken.ThrowIfCancellationRequested();
        }

        return packed.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = ArchiveTimestamp;
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<byte[]> ReadFullyAsync(Stream content, CancellationToken cancellationToken)
    {
        // Stored media is already bounded at write time by the media package's own byte limit,
        // so reading it fully here holds no more than that package was willing to store.
        using var buffered = new MemoryStream();
        await content.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
        return buffered.ToArray();
    }

    /// <summary>
    /// The selected pictures of one evidence head, as the media hash binds them. The review
    /// package's own reading, repeated rather than redefined, so the two packages cannot
    /// disagree about what an approval covers.
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

    /// <summary>
    /// Refuses an owner's text that states a figure the cited evidence never stated. The numeral
    /// rule's own reading, applied shot by shot so the refusal names the shot that carried the
    /// figure rather than the script as a whole.
    /// </summary>
    private static void ThrowWhenUnprovenNumber(string field, string text, string corpus)
    {
        foreach (var number in ClaimMap.NumbersInProse(text))
        {
            if (!ClaimMap.ContainsNumber(corpus, number))
            {
                throw new InvalidStorySchemaException(
                    field,
                    $"The {field} states a figure the evidence never stated, so nothing was stored.");
            }
        }
    }

    /// <summary>
    /// Refuses an owner's text that breaks the voice it was written under. The voice checker's
    /// own reading, applied shot by shot so the refusal names the shot that carried the finding
    /// rather than the script as a whole.
    /// </summary>
    private static void ThrowWhenVoiceFinding(string field, string text, VoiceValues effective)
    {
        var findings = VoiceCheckService.Check(effective, string.Empty, text);

        if (findings.Count > 0)
        {
            var first = findings[0];
            throw new InvalidStorySchemaException(
                field,
                $"The {field} breaks the voice rule {first.Rule}, so nothing was stored.");
        }
    }

    private sealed record PreparedStory(Guid ProjectId, string MilestoneTitle, Guid MilestoneId);
    private sealed record ExportedImage(Guid MediaId, string Caption, string MediaType, byte[] Bytes);

    /// <summary>
    /// A stored row this build cannot read (500).
    /// </summary>
    private static ApiException UnreadableContentRow() =>
        new InternalFailureException(
            "A stored content revision could not be read by this build, so no story can be "
            + "generated from it and nothing can be exported. The revision itself is unchanged.");
}
