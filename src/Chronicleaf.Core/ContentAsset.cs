using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Chronicleaf.Core;

/// <summary>
/// The kinds of content asset this product stores (GROUNDED-MARKDOWN-DRAFTS-001).
/// </summary>
/// <remarks>
/// <para>
/// The set is closed and every member names a storage shape this repository has rules for.
/// <see cref="MarkdownStory"/> is the generated outcome the drafts package delivers; the seven
/// variant kinds are deterministic derivations of an approved story owned by
/// <c>content-asset-variants</c>. A variant is a new asset, never a mutation of its source, and
/// every variant row is a draft until the review package approves it separately: approval of the
/// source never travels to a derivative.
/// </para>
/// <para>
/// A type is never chosen by the draft route. The route that creates a draft always writes
/// <see cref="MarkdownStory"/>, so a caller cannot ask for a kind of content whose rules nobody has
/// written down - the same reasoning the evidence package used to keep owner-writable kinds to a
/// fixed list. The variant kinds are chosen only by the variant derive route, which refuses an
/// unknown type before any row exists.
/// </para>
/// </remarks>
public static class ContentAssetTypes
{
    /// <summary>
    /// A grounded Markdown story for one milestone: a structured summary, its citations and the
    /// prose that cites them.
    /// </summary>
    public const string MarkdownStory = "markdown-story";

    /// <summary>
    /// A deterministic changelog derived from an approved story (CONTENT-ASSET-VARIANTS-001).
    /// </summary>
    public const string Changelog = "changelog";

    /// <summary>
    /// A deterministic technical blog derived from an approved story (CONTENT-ASSET-VARIANTS-001).
    /// </summary>
    public const string TechnicalBlog = "technical-blog";

    /// <summary>
    /// A deterministic concise progress note derived from an approved story
    /// (CONTENT-ASSET-VARIANTS-001).
    /// </summary>
    public const string SocialShort = "social-short";

    /// <summary>
    /// A deterministic release note derived from an approved story (CONTENT-ASSET-VARIANTS-001).
    /// </summary>
    public const string GithubRelease = "github-release";

    /// <summary>
    /// A deterministic README proposal derived from an approved story (CONTENT-ASSET-VARIANTS-001).
    /// A proposal block only: it never writes a repository.
    /// </summary>
    public const string ReadmeUpdate = "readme-update";

    /// <summary>
    /// A deterministic portfolio entry derived from an approved story (CONTENT-ASSET-VARIANTS-001).
    /// </summary>
    public const string Portfolio = "portfolio";

    /// <summary>
    /// A deterministic project-local weekly note derived from an approved story
    /// (CONTENT-ASSET-VARIANTS-001). Client aggregation belongs to the agency report package.
    /// </summary>
    public const string WeeklyProgress = "weekly-progress";

    /// <summary>
    /// Copy-ready LinkedIn post text derived from an approved story (LINKEDIN-COPY-PACKAGE-001).
    /// </summary>
    /// <remarks>
    /// A draft until the review package approves it separately, exactly like every other
    /// derivative: approval of the source never travels here. The text cites only the footnote
    /// ids the source summary's claim map assigned, and the pictures it may carry are named by
    /// the LinkedIn package's own selection rows rather than by this type.
    /// </remarks>
    public const string LinkedinCopy = "linkedin-copy";

    /// <summary>
    /// A copy-ready Xiaohongshu note derived from an approved story
    /// (XIAOHONGSHU-CONTENT-PACKAGE-001).
    /// </summary>
    /// <remarks>
    /// A draft until the review package approves it separately, exactly like every other
    /// derivative: approval of the source never travels here. The text cites only the footnote
    /// ids the source summary's claim map assigned; the title is the owner's choice from the
    /// derived candidates, and the pictures it may carry are named by the Xiaohongshu
    /// package's own ordered selection rows rather than by this type. Publication stays the
    /// owner's own hand: no account, no login and no transport behind it.
    /// </remarks>
    public const string XiaohongshuPack = "xiaohongshu-pack";

    /// <summary>
    /// An evidence-based video storyboard derived from an approved story
    /// (VIDEO-STORY-SCRIPTS-001).
    /// </summary>
    /// <remarks>
    /// A draft until the review package approves it separately, exactly like every other
    /// derivative: approval of the source never travels here. Every shot cites evidence the
    /// frozen bundle holds, and the pictures the shots depict are named by media id rather
    /// than by this type. The revision body is the storyboard JSON document itself: a reviewed
    /// script a human acts on, never a synthesized video, never rendered footage, never a post.
    /// </remarks>
    public const string VideoStory = "video-story";

    /// <summary>Every kind, in the order a surface would list them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        MarkdownStory,
        Changelog,
        TechnicalBlog,
        SocialShort,
        GithubRelease,
        ReadmeUpdate,
        Portfolio,
        WeeklyProgress,
        LinkedinCopy,
        XiaohongshuPack,
        VideoStory,
    ];

    /// <summary>Bounded to the width of the <c>content_assets.type</c> column.</summary>
    public const int MaxLength = 40;

    /// <summary>
    /// True for a kind this build stores. Compared ordinally: a type is an identifier a stored row is
    /// read back by, so a folded spelling would select a kind nobody wrote.
    /// </summary>
    public static bool IsKnown(string? type) => type is not null && All.Contains(type, StringComparer.Ordinal);
}

/// <summary>
/// The statuses a content asset can hold (GROUNDED-MARKDOWN-DRAFTS-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>One value, and the absence of a second is the requirement.</b> A generated revision is a
/// draft, always, and this package has no transition out of that state: no approval route, no
/// approve verb, no field a future edit could set. Human approval belongs to
/// <c>markdown-review-export</c>, which will add its own approval table bound to a
/// <c>sourceRevision</c>, a <c>bodyHash</c> and an evidence revision. Nothing in this file is
/// retroactively treated as approved, because a row that had been approved without anybody approving
/// it would be the exact defect the split exists to prevent.
/// </para>
/// <para>
/// The status is also stored as a column rather than left implicit, so a reader of the table can see
/// the word and not have to infer the state from the fact that nothing else writes it.
/// </para>
/// </remarks>
public static class ContentAssetStatus
{
    /// <summary>Generated, stored, and not approved. The only value this package ever writes.</summary>
    public const string Draft = "draft";

    public static IReadOnlyList<string> All { get; } = [Draft];

    /// <summary>Bounded to the width of the <c>content_assets.status</c> column.</summary>
    public const int MaxLength = 32;

    public static bool IsKnown(string? status) =>
        status is not null && All.Contains(status, StringComparer.Ordinal);
}

/// <summary>
/// The bounds this package's own storage imposes, beside the records that have to satisfy them
/// (GROUNDED-MARKDOWN-DRAFTS-002).
/// </summary>
/// <remarks>
/// These are the storage bounds, not the generation bounds. The generation policy - how many items a
/// summary may carry, how long a string in it may be, how many repair calls a call may spend - lives
/// in the application layer with the code that enforces it, and the one number here is the Markdown
/// body, because that is the value a column and a revision read both have to hold.
/// </remarks>
public static class ContentAssetLimits
{
    /// <summary>
    /// 100 KiB of UTF-8 for one Markdown body. Measured in bytes, because bytes is what a column
    /// holds and what an export writes; a cap counted in characters would be up to four times larger
    /// in UTF-8 and would let a body past the size the limit exists to keep.
    /// </summary>
    public const int MaxBodyBytes = 100 * 1024;

    /// <summary>How long an evidence bundle id, provider id or model name may be as a stored label.</summary>
    public const int LabelMaxLength = 200;
}

/// <summary>
/// One generated piece of content for one milestone, and the revision head it points at
/// (GROUNDED-MARKDOWN-DRAFTS-002).
/// </summary>
/// <remarks>
/// <para>
/// An asset is the stable identity and the versioned thing is its revision. That split is what makes
/// "preserve prior revisions" a storage fact rather than a promise: a second generation appends a
/// <see cref="ContentRevision"/> and advances <see cref="CurrentRevision"/>, and no statement in this
/// repository ever overwrites a revision or moves an asset backwards.
/// </para>
/// <para>
/// <b><see cref="Status"/> is a readonly field bound to one value</b>, exactly as
/// <see cref="ScreenshotCandidate.Status"/> is, and for the same reason: "this package never
/// approves a draft" is then a property of the type rather than a rule a later edit could forget. The
/// review package will own approval, and it will do so by owning a new table bound to a revision -
/// not by learning to write this field.
/// </para>
/// <para>
/// <see cref="CurrentRevision"/> is 0 between the asset row and its first revision. A generation
/// creates the asset and its first revision in one transaction, so a reader never observes a
/// committed asset whose head revision does not exist; the zero is what the row carries inside that
/// statement, and it is the value a second generation's compare-and-set starts from.
/// </para>
/// </remarks>
/// <param name="id">This asset's identifier.</param>
/// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
/// <param name="projectId">The project the milestone belongs to.</param>
/// <param name="milestoneId">The milestone this content is about.</param>
/// <param name="type">One of <see cref="ContentAssetTypes"/>; this package only writes markdown-story.</param>
/// <param name="currentRevision">The newest committed revision, or 0 before the first one lands.</param>
/// <param name="createdAt">When the row was inserted; UTC.</param>
/// <param name="updatedAt">When the head revision last advanced; UTC.</param>
public sealed class ContentAsset
{
    public ContentAsset(
        Guid id,
        Guid scopeId,
        Guid projectId,
        Guid milestoneId,
        string type,
        long currentRevision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A content asset needs a non-empty id.", nameof(id));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A content asset needs a non-empty project.", nameof(projectId));
        }

        if (milestoneId == Guid.Empty)
        {
            throw new ArgumentException("A content asset needs a non-empty milestone.", nameof(milestoneId));
        }

        if (!ContentAssetTypes.IsKnown(type))
        {
            throw new ArgumentException("The type is not one this product stores.", nameof(type));
        }

        if (currentRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentRevision),
                currentRevision,
                "A content asset's head revision is zero before its first revision and positive after.");
        }

        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        MilestoneId = milestoneId;
        Type = type;
        CurrentRevision = currentRevision;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ProjectId { get; }

    public Guid MilestoneId { get; }

    /// <summary>One of <see cref="ContentAssetTypes"/>.</summary>
    public string Type { get; }

    /// <summary>
    /// The newest committed revision. The compare-and-set for a new generation is
    /// <c>current_revision = @expected + 1</c>, so two generations that read the same head produce
    /// one committed revision and one conflict rather than a merge.
    /// </summary>
    public long CurrentRevision { get; }

    /// <summary>Always <c>draft</c>. See the type remarks: approval is another package's table.</summary>
    public string Status => _status;

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    private readonly string _status = ContentAssetStatus.Draft;

    /// <summary>The asset a milestone's first draft belongs to, at revision zero.</summary>
    public static ContentAsset Create(
        Guid id,
        Guid scopeId,
        Guid projectId,
        Guid milestoneId,
        DateTimeOffset now) =>
        new(id, scopeId, projectId, milestoneId, ContentAssetTypes.MarkdownStory, 0, now, now);

    /// <summary>A stable diagnostic label for an asset row. It names the asset, never the milestone text.</summary>
    public static string Label(Guid assetId) =>
        string.Create(CultureInfo.InvariantCulture, $"content-assets/{assetId:N}");
}

/// <summary>
/// One immutable version of a content asset (GROUNDED-MARKDOWN-DRAFTS-002).
/// </summary>
/// <remarks>
/// <para>
/// A revision is written once. There is no update statement against <c>content_revisions</c> anywhere
/// in this repository, exactly as there is none against the evidence tables - a provenance snapshot
/// that can be edited is not a snapshot, and an approval bound to a <c>bodyHash</c> over text that
/// then changed is a signature over nothing.
/// </para>
/// <para>
/// <b>Three provenance values travel with the text.</b> <see cref="EvidenceBundleId"/> and
/// <see cref="EvidenceRevision"/> say which immutable snapshot the claims were checked against;
/// <see cref="VoiceSnapshotJson"/> is the voice the prose was written under, copied in as a value so
/// a later profile edit cannot reach it; and <see cref="BodyHash"/> is the digest of exactly the bytes
/// in <see cref="BodyMarkdown"/>. The last is what lets the review package bind an approval to a
/// specific text without re-deriving what the text was.
/// </para>
/// <para>
/// <see cref="PromptVersion"/> and <see cref="SchemaVersion"/> are recorded because a draft outlives
/// the code that made it. "Record prompt/schema version" is what lets a reader of a two-year-old
/// revision know which instruction and which shape produced it, instead of guessing from the text.
/// </para>
/// </remarks>
/// <param name="id">This revision's identifier.</param>
/// <param name="assetId">The asset this version belongs to.</param>
/// <param name="scopeId">The owning scope, carried on the row so a scoped read never trusts a join.</param>
/// <param name="revision">The version number within the asset, from 1.</param>
/// <param name="evidenceBundleId">The frozen evidence bundle every claim was checked against.</param>
/// <param name="evidenceRevision">That bundle's revision.</param>
/// <param name="voiceRevision">The project voice profile revision at the moment of generation.</param>
/// <param name="voiceSnapshotJson">
/// The copied voice, as the delivered canonical snapshot JSON. Self-contained: there is no profile id
/// in it for a later edit to follow.
/// </param>
/// <param name="summaryJson">The accepted structured summary, canonical JSON. The claim map's source.</param>
/// <param name="bodyMarkdown">The generated Markdown, exactly as it will be exported and reviewed.</param>
/// <param name="bodyHash">Lowercase SHA-256 hex over the UTF-8 bytes of <paramref name="bodyMarkdown"/>.</param>
/// <param name="providerId">The provider slot the call was made to.</param>
/// <param name="model">The model the call named.</param>
/// <param name="promptVersion">This package's instruction version.</param>
/// <param name="schemaVersion">The structured-summary schema version.</param>
/// <param name="createdAt">When the revision was committed; UTC.</param>
public sealed class ContentRevision
{
    public ContentRevision(
        Guid id,
        Guid assetId,
        Guid scopeId,
        long revision,
        Guid evidenceBundleId,
        long evidenceRevision,
        long voiceRevision,
        string voiceSnapshotJson,
        string summaryJson,
        string bodyMarkdown,
        string bodyHash,
        string providerId,
        string model,
        string promptVersion,
        string schemaVersion,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A content revision needs a non-empty id.", nameof(id));
        }

        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("A content revision needs an asset.", nameof(assetId));
        }

        if (evidenceBundleId == Guid.Empty)
        {
            // Without the bundle there is no claim map and no grounding, so a revision that could not
            // be constructed is refused here rather than inserted as an ungrounded promise.
            throw new ArgumentException("A content revision needs the evidence bundle it was checked against.", nameof(evidenceBundleId));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "The first stored revision is 1.");
        }

        if (evidenceRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceRevision), evidenceRevision, "An evidence revision is 1 or greater.");
        }

        if (voiceRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(voiceRevision), voiceRevision, "A voice revision is zero or greater.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(voiceSnapshotJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyMarkdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);

        if (providerId.Length > ContentAssetLimits.LabelMaxLength ||
            model.Length > ContentAssetLimits.LabelMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerId),
                "The provider and model labels are bounded by the column width.");
        }

        // The bound is enforced by the record rather than only by the service, for the reason the
        // candidate record gives: a column that is too narrow truncates a value in the database, which
        // would leave a row describing a body nobody ever wrote. A refused write beats a plausible lie.
        var bodyBytes = Encoding.UTF8.GetByteCount(bodyMarkdown);
        if (bodyBytes > ContentAssetLimits.MaxBodyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bodyMarkdown),
                bodyBytes,
                $"A draft body is at most {ContentAssetLimits.MaxBodyBytes} bytes of UTF-8.");
        }

        // The digest is recomputed rather than trusted, so a stored hash can never describe text other
        // than the text beside it. An approval bound to this value is only as good as that.
        if (!string.Equals(Hash(bodyMarkdown), bodyHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The body hash does not describe the body it is stored with.",
                nameof(bodyHash));
        }

        Id = id;
        AssetId = assetId;
        ScopeId = scopeId;
        Revision = revision;
        EvidenceBundleId = evidenceBundleId;
        EvidenceRevision = evidenceRevision;
        VoiceRevision = voiceRevision;
        VoiceSnapshotJson = voiceSnapshotJson;
        SummaryJson = summaryJson;
        BodyMarkdown = bodyMarkdown;
        BodyHash = bodyHash;
        ProviderId = providerId;
        Model = model;
        PromptVersion = promptVersion;
        SchemaVersion = schemaVersion;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid AssetId { get; }

    public Guid ScopeId { get; }

    public long Revision { get; }

    public Guid EvidenceBundleId { get; }

    public long EvidenceRevision { get; }

    public long VoiceRevision { get; }

    public string VoiceSnapshotJson { get; }

    public string SummaryJson { get; }

    public string BodyMarkdown { get; }

    /// <summary>
    /// Lowercase SHA-256 hex over the UTF-8 bytes of <see cref="BodyMarkdown"/>. Recomputed on
    /// construction, never accepted on trust.
    /// </summary>
    public string BodyHash { get; }

    public string ProviderId { get; }

    public string Model { get; }

    /// <summary>This package's instruction version, so a stored draft names the instruction that made it.</summary>
    public string PromptVersion { get; }

    /// <summary>The structured-summary schema version, so a stored draft names the shape it satisfied.</summary>
    public string SchemaVersion { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Lowercase hex SHA-256 over the UTF-8 bytes of a Markdown body.</summary>
    public static string Hash(string bodyMarkdown)
    {
        ArgumentNullException.ThrowIfNull(bodyMarkdown);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bodyMarkdown)))
            .ToLowerInvariant();
    }

    /// <summary>A stable diagnostic label for a revision row.</summary>
    public static string Label(Guid revisionId) =>
        string.Create(CultureInfo.InvariantCulture, $"content-revisions/{revisionId:N}");
}
