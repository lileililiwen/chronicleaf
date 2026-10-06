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

namespace Chronicleaf.Application.LinkedIn;

/// <summary>
/// The LinkedIn copy use cases over the delivered evidence, voice, asset, review, media and audit
/// contracts (LINKEDIN-COPY-PACKAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A copy is derived, never generated, and never sent.</b> The source is an approved
/// <c>markdown-story</c> revision; the derivation re-renders that revision's accepted summary
/// through a deterministic template, plus the owner's own call to action, hashtags and picture
/// words. There is no provider behind this service, so there is no consent to check and no key
/// to lease - and no transport behind it either, so there is nothing a caller could supply that
/// would reach a LinkedIn endpoint.
/// </para>
/// <para>
/// <b>Approval is reused, never redefined.</b> The source must carry a live approval that still
/// describes the stored state, under exactly the bindings the review package's export re-checks.
/// The derivative itself starts with no approval row at all, and the export re-checks every one
/// of those bindings again before serving a byte - never approved is
/// <c>approval_required</c>, once approved but moved is <c>approval_stale</c>.
/// </para>
/// <para>
/// <b>Voice precedence is structural.</b> The renderer receives the profile's resolved effective
/// values, in which project overrides have already replaced preset values. The stored revision
/// carries that snapshot as a value, so a later profile edit cannot reach it.
/// </para>
/// <para>
/// <b>Nothing here logs prose.</b> The logger receives identifiers, codes and counts. The summary,
/// the copy, the call to action and the accessibility words travel from a stored document into a
/// stored revision; none appears in a log line, in an exception message or in an audit detail.
/// </para>
/// </remarks>
public sealed class LinkedInCopyService(
    IMilestoneStore milestones,
    IProjectStore projects,
    IEvidenceStore evidence,
    IVoiceProfileService voice,
    IContentAssetStore assets,
    IContentReviewStore reviews,
    ILinkedInCopyMediaStore pictures,
    IMediaStore media,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<LinkedInCopyService> logger) : ILinkedInCopyService
{
    /// <summary>The audit action recorded when a copy is adapted.</summary>
    public const string CopyAdaptedAction = "linkedin-copy.adapted";

    /// <summary>The audit action recorded when an approved copy is exported.</summary>
    public const string CopyExportedAction = "linkedin-copy.exported";

    /// <summary>The fixed timestamp every ZIP entry carries, so the same selection packs identically.</summary>
    public static readonly DateTimeOffset ArchiveTimestamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<AdaptedLinkedInCopy> AdaptAsync(
        OwnerScope owner,
        Guid sourceAssetId,
        AdaptLinkedInCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        var callToAction = NormalizeCallToAction(request.CallToAction);
        var hashtags = NormalizeHashtags(request.Hashtags);
        var selections = NormalizeSelections(request.Selections);

        var prepared = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var source = await assets
                    .FindAsync(owner.ScopeId, sourceAssetId, token)
                    .ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("content asset");

                // A copy is derived from the milestone's approved Markdown story, never from
                // another derivative: deriving one copy from another would let provenance drift
                // one hop at a time until no row names the evidence anything was checked against.
                if (!string.Equals(source.Type, ContentAssetTypes.MarkdownStory, StringComparison.Ordinal))
                {
                    throw new ValidationFailedException(
                        "sourceAssetId",
                        "LinkedIn copy is adapted from the milestone's approved Markdown story, "
                        + "not from another asset.");
                }

                var milestone = await milestones
                    .FindAsync(owner.ScopeId, source.MilestoneId, token)
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

                return new PreparedCopy(milestone.ProjectId, milestone.Title, source.MilestoneId);
            },
            cancellationToken).ConfigureAwait(false);

        var sourceHead = await assets
            .FindAsync(owner.ScopeId, sourceAssetId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        if (sourceHead.CurrentRevision < 1)
        {
            throw new ResourceNotFoundException("content asset");
        }

        var sourceRevision = await assets
            .FindRevisionAsync(owner.ScopeId, sourceAssetId, sourceHead.CurrentRevision, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        // The approval bindings, re-checked rather than redefined: a live row must name the head
        // revision and its body hash, and the evidence head must still be the one the approval was
        // bound to. Never approved and once approved are two different answers, exactly as on the
        // export route.
        var sourceApprovals = await reviews
            .ListForAssetAsync(owner.ScopeId, sourceAssetId, cancellationToken)
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
            .FindHeadAsync(owner.ScopeId, prepared.MilestoneId, cancellationToken)
            .ConfigureAwait(false);

        if (head is null ||
            head.Id != sourceRevision.EvidenceBundleId ||
            head.Revision != live.EvidenceRevision)
        {
            throw new ApprovalStaleException("evidence");
        }

        // The revisions the owner was looking at must still be current. An owner who adapted
        // against a head that has since moved gets the same stale answer the export gives, not a
        // copy bound to provenance they did not see.
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
        // copy of it would carry a citation to nothing.
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

        // Every selected picture must be a complete screenshot with stored bytes in the frozen
        // bundle the copy is grounded in. A name that matches nothing selectable is
        // media_unavailable, not a manifest with a hole in it - and the bytes are opened now,
        // before anything is stored, rather than discovered missing at export.
        var depictable = head.Items
            .Where(item => string.Equals(item.Kind, EvidenceKinds.Screenshot, StringComparison.Ordinal)
                && item.MediaId is not null
                && string.Equals(item.Availability, EvidenceAvailability.Complete, StringComparison.Ordinal))
            .Select(item => item.MediaId!.Value)
            .ToHashSet();

        foreach (var selection in selections)
        {
            if (!depictable.Contains(selection.MediaId))
            {
                throw new MediaUnavailableException();
            }

            try
            {
                var content = await media.OpenAsync(owner, selection.MediaId, cancellationToken).ConfigureAwait(false);
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
                throw new MediaUnavailableException();
            }
        }

        var cited = new HashSet<Guid>(claims.SelectMany(claim => claim.EvidenceIds));
        var corpus = string.Join(
            "\n",
            head.Items
                .Where(item => cited.Contains(item.Id))
                .Select(item => item.Text));

        LinkedInCopyRenderer.RenderedCopy rendered;

        try
        {
            rendered = LinkedInCopyRenderer.Render(
                prepared.MilestoneTitle,
                summary,
                claims,
                profile.Resolved.Effective,
                callToAction,
                hashtags);
        }
        catch (LinkedInCopyRenderer.TooLongException tooLong)
        {
            throw new LinkedInCopyTooLongException(tooLong.CharCount, LinkedInCopyRenderer.MaxCopyChars);
        }

        // The rendered copy is held to the same local citation rules as a generated draft: every
        // footnote must name the claim map, the known limitations must be carried, and no figure
        // may appear that the cited evidence never stated. The owner's own call to action and
        // hashtags are inside the checked bytes, so a digit they carry that the evidence never
        // stated refuses the adaptation rather than storing an ungrounded promise.
        var markdown = MarkdownDraftValidator.Validate(rendered.Text, claims, summary.KnownLimitations, corpus);

        if (markdown.FailureCode is not null)
        {
            logger.LogWarning(
                "Event=linkedin.copy_invalid Code={Code}",
                markdown.FailureCode);

            throw new ValidationFailedException(
                "callToAction",
                $"The LinkedIn copy failed its own grounding check ({markdown.FailureCode}), "
                + "so nothing was stored.");
        }

        var now = clock.GetUtcNow();

        var committed = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var asset = await assets.FindForMilestoneAsync(
                        owner.ScopeId, prepared.MilestoneId, ContentAssetTypes.LinkedinCopy, token)
                    .ConfigureAwait(false);

                long expected;

                if (asset is null)
                {
                    var created = new ContentAsset(
                        Guid.NewGuid(),
                        owner.ScopeId,
                        prepared.ProjectId,
                        prepared.MilestoneId,
                        ContentAssetTypes.LinkedinCopy,
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
                                owner.ScopeId, prepared.MilestoneId, ContentAssetTypes.LinkedinCopy, token)
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
                    summary.ToCanonicalJson(),
                    rendered.Text,
                    ContentRevision.Hash(rendered.Text),
                    "local-template",
                    LinkedInCopyRenderer.TemplateVersion,
                    LinkedInCopyRenderer.TemplateVersion,
                    sourceRevision.SchemaVersion,
                    now);

                if (!await assets
                        .TryAppendRevisionAsync(revision, expected, now, token)
                        .ConfigureAwait(false))
                {
                    var reread = await assets.FindAsync(owner.ScopeId, asset.Id, token).ConfigureAwait(false);
                    throw new RevisionConflictException(reread?.CurrentRevision ?? asset.CurrentRevision);
                }

                // The selection rows land in the same unit of work as the revision they belong
                // to: a revision without its pictures, or pictures without their revision, would
                // each be half a decision.
                await pictures.AddAsync(owner.ScopeId, asset.Id, revision.Revision, selections, token)
                    .ConfigureAwait(false);

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    CopyAdaptedAction,
                    asset.Id,
                    revision.Revision,
                    // Identifiers, revisions and the derivation chain. Not the copy, not one byte
                    // of the evidence, not the call to action and not the accessibility words:
                    // this row outlives the request.
                    $"source={sourceAssetId:D};"
                        + $"sourceRevision={sourceRevision.Revision.ToString(CultureInfo.InvariantCulture)};"
                        + $"evidenceRevision={sourceRevision.EvidenceRevision.ToString(CultureInfo.InvariantCulture)};"
                        + $"voiceRevision={profile.Revision.ToString(CultureInfo.InvariantCulture)};"
                        + $"charCount={rendered.CharCount.ToString(CultureInfo.InvariantCulture)};"
                        + $"media={string.Join(",", selections.Select(selection => selection.MediaId.ToString("D")))};"));

                return (asset.Id, revision.Revision, revision.BodyHash);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "LinkedIn copy adapted for milestone {MilestoneId} as asset {AssetId} revision {Revision}.",
            prepared.MilestoneId,
            committed.Item1,
            committed.Item2);

        return new AdaptedLinkedInCopy(
            committed.Item1,
            committed.Item2,
            committed.Item3,
            rendered.CharCount,
            rendered.LengthWarning,
            sourceAssetId,
            sourceRevision.Revision,
            sourceRevision.EvidenceRevision,
            profile.Revision,
            [.. selections.Select(selection => selection.MediaId)],
            callToAction is not null);
    }

    /// <inheritdoc />
    public async Task<LinkedInCopyExport> ExportAsync(
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

        // The LinkedIn shape is served for LinkedIn copies only: any other asset already has its
        // own export, and answering it here in a second shape would let two formats disagree
        // about what is authorized.
        if (!string.Equals(asset.Type, ContentAssetTypes.LinkedinCopy, StringComparison.Ordinal))
        {
            throw new ValidationFailedException(
                "format",
                "The LinkedIn package serves LinkedIn copies. Name format=markdown or format=html "
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

        // An export names the revision it wants, or takes the approved one. Either way the
        // approval has to name exactly that revision: a concurrent edit appends a newer head and
        // the older approval can no longer authorize anything.
        var wanted = revision ?? live.ContentRevision;
        if (wanted != live.ContentRevision)
        {
            throw new ApprovalStaleException("revision");
        }

        var stored = await assets
            .FindRevisionAsync(owner.ScopeId, assetId, wanted, cancellationToken)
            .ConfigureAwait(false)
            ?? throw UnreadableContentRow();

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

        if (!string.Equals(
                ContentApproval.MediaSelectionHashFor(SelectedPictures(head)),
                live.MediaSelectionHash,
                StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("media");
        }

        // The pictures this revision was adapted with, with the bytes the media store holds right
        // now. A picture whose bytes are gone ends the export with media_unavailable: the
        // approval bound pictures the artifact would have to carry, so serving the text without
        // them would be a silent claim of completeness.
        var selection = await pictures.ListAsync(owner.ScopeId, assetId, wanted, cancellationToken)
            .ConfigureAwait(false);

        var images = new List<ExportedImage>();

        foreach (var selected in selection)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MediaContent content;
            try
            {
                content = await media.OpenAsync(owner, selected.MediaId, cancellationToken).ConfigureAwait(false);
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

                images.Add(new ExportedImage(
                    selected.MediaId,
                    selected.AltText,
                    content.Ref.MediaType,
                    bytes));
            }
        }

        var ordered = images.OrderBy(image => image.MediaId).ToList();
        var copyBytes = Encoding.UTF8.GetBytes(stored.BodyMarkdown);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                assetId = asset.Id,
                revision = stored.Revision,
                bodyHash = stored.BodyHash,
                evidenceRevision = stored.EvidenceRevision,
                copyFile = CopyFileName,
                images = ordered.Select(image => new
                {
                    mediaId = image.MediaId,
                    altText = image.AltText,
                    file = ImageFileName(image.MediaId),
                    sha256 = Convert.ToHexString(SHA256.HashData(image.Bytes)).ToLowerInvariant(),
                    mediaType = image.MediaType,
                }).ToArray(),
            },
            ManifestJson);

        var packed = PackArchive(copyBytes, manifest, ordered, cancellationToken);

        await unitOfWork.ExecuteAsync(
            token =>
            {
                // The audit row names the revision and the approval, never the prose: an export
                // is a record that bytes left the building, not a copy of them.
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    CopyExportedAction,
                    asset.Id,
                    stored.Revision,
                    $"revision={stored.Revision.ToString(CultureInfo.InvariantCulture)};"
                    + $"approval={live.Id:D};format=linkedin;"));
                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "LinkedIn copy asset {AssetId} revision {Revision} exported.",
            assetId,
            stored.Revision);

        return new LinkedInCopyExport(
            $"chronicleaf-{assetId:N}-r{stored.Revision}-linkedin.zip",
            "application/zip",
            packed,
            Convert.ToHexString(SHA256.HashData(packed)).ToLowerInvariant(),
            asset.Id,
            stored.Revision);
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
        // about the asset - approved, edited, moved - is irrelevant: there is no transport for
        // any of it to travel on.
        return PublishAfterResolveAsync(owner, assetId, cancellationToken);
    }

    private async Task PublishAfterResolveAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        _ = await assets.FindAsync(owner.ScopeId, assetId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        // No credential is leased, no job is enqueued, no audit row is written and no socket is
        // opened: a refusal that changed anything would be an account action of its own.
        throw new PublisherUnsupportedException();
    }

    /// <summary>The copy text file inside the export package.</summary>
    public const string CopyFileName = "copy.txt";

    /// <summary>The manifest file inside the export package.</summary>
    public const string ManifestFileName = "manifest.json";

    private static string ImageFileName(Guid mediaId) => $"images/{mediaId:N}.png";

    private static byte[] PackArchive(
        byte[] copyBytes,
        byte[] manifestBytes,
        IReadOnlyList<ExportedImage> images,
        CancellationToken cancellationToken)
    {
        using var packed = new MemoryStream();

        using (var archive = new ZipArchive(packed, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The fixed order and the fixed timestamp are what make two exports of the same
            // approved revision agree byte for byte: nothing here records when the export ran.
            WriteEntry(archive, CopyFileName, copyBytes);

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
    /// The owner's closing line, verbatim, or null when they supplied none. An informational post
    /// without one is a complete answer, not a missing field.
    /// </summary>
    private static string? NormalizeCallToAction(string? callToAction)
    {
        if (string.IsNullOrWhiteSpace(callToAction))
        {
            return null;
        }

        var trimmed = callToAction.Trim();

        if (trimmed.Length > LinkedInCopyRenderer.MaxCtaChars)
        {
            throw new ValidationFailedException(
                "callToAction",
                $"A call to action is at most {LinkedInCopyRenderer.MaxCtaChars} characters.");
        }

        return trimmed;
    }

    /// <summary>
    /// The owner's tags, verbatim and in order, with exact duplicates removed. None invented.
    /// </summary>
    private static IReadOnlyList<string> NormalizeHashtags(IReadOnlyList<string>? hashtags)
    {
        if (hashtags is null || hashtags.Count == 0)
        {
            return [];
        }

        if (hashtags.Count > LinkedInCopyRenderer.MaxHashtags)
        {
            throw new ValidationFailedException(
                "hashtags",
                $"A copy carries at most {LinkedInCopyRenderer.MaxHashtags} hashtags.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<string>(hashtags.Count);

        foreach (var hashtag in hashtags)
        {
            if (string.IsNullOrWhiteSpace(hashtag) || hashtag.Length > 64 || !IsHashtag(hashtag))
            {
                throw new ValidationFailedException(
                    "hashtags",
                    "A hashtag starts with # followed by a letter, then letters, digits or "
                    + "underscores, up to 64 characters in total.");
            }

            if (seen.Add(hashtag))
            {
                accepted.Add(hashtag);
            }
        }

        return accepted;
    }

    private static bool IsHashtag(string hashtag)
    {
        if (hashtag.Length < 2 || hashtag[0] != '#')
        {
            return false;
        }

        if (!char.IsAsciiLetter(hashtag[1]))
        {
            return false;
        }

        for (var index = 2; index < hashtag.Length; index++)
        {
            var character = hashtag[index];
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The pictures the copy carries, each with its accessibility words. shape first, existence
    /// later: unknown ids are the media package's own 409 once the bundle is read.
    /// </summary>
    private static IReadOnlyList<LinkedInMediaSelection> NormalizeSelections(
        IReadOnlyList<LinkedInMediaSelection>? selections)
    {
        if (selections is null || selections.Count == 0)
        {
            return [];
        }

        if (selections.Count > LinkedInCopyMedia.MaxPictures)
        {
            throw new ValidationFailedException(
                "selectedMediaIds",
                $"A copy carries at most {LinkedInCopyMedia.MaxPictures} pictures.");
        }

        var seen = new HashSet<Guid>();
        var accepted = new List<LinkedInMediaSelection>(selections.Count);

        foreach (var selection in selections)
        {
            if (selection.MediaId == Guid.Empty)
            {
                throw new ValidationFailedException(
                    "selectedMediaIds", "A selected picture is named by its media id.");
            }

            if (!seen.Add(selection.MediaId))
            {
                throw new ValidationFailedException(
                    "selectedMediaIds", "A picture selected twice is still one picture; name it once.");
            }

            if (string.IsNullOrWhiteSpace(selection.AltText) ||
                selection.AltText.Length > LinkedInCopyMedia.MaxAltTextChars)
            {
                throw new ValidationFailedException(
                    "altTexts",
                    $"Every selected picture carries accessibility words, up to "
                    + $"{LinkedInCopyMedia.MaxAltTextChars} characters.");
            }

            accepted.Add(new LinkedInMediaSelection(selection.MediaId, selection.AltText.Trim()));
        }

        return accepted;
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

    private sealed record PreparedCopy(Guid ProjectId, string MilestoneTitle, Guid MilestoneId);

    private sealed record ExportedImage(Guid MediaId, string AltText, string MediaType, byte[] Bytes);

    /// <summary>
    /// A stored row this build cannot read (500).
    /// </summary>
    private static ApiException UnreadableContentRow() =>
        new InternalFailureException(
            "A stored content revision could not be read by this build, so no copy can be "
            + "adapted from it and nothing can be exported. The revision itself is unchanged.");
}
