namespace Chronicleaf.Core;

/// <summary>
/// The layouts an evolution image can be rendered in (SCREENSHOT-EVOLUTION-IMAGES-001).
/// </summary>
/// <remarks>
/// Every layout is a deterministic arrangement of the depicted source pictures, never a
/// reinterpretation of them: cells hold source pixels at their native size centered on neutral
/// padding, so no layout invents a before screenshot that was never captured.
/// </remarks>
public static class EvolutionLayouts
{
    /// <summary>Two pictures side by side, earliest left.</summary>
    public const string BeforeAfter = "before-after";

    /// <summary>Every depicted picture side by side in chronological order.</summary>
    public const string HorizontalTimeline = "horizontal-timeline";

    /// <summary>Every depicted picture stacked top to bottom in chronological order.</summary>
    public const string VerticalLong = "vertical-long";

    /// <summary>
    /// Pictures in a two-column grid. The only layout that admits mixed viewports: the manifest
    /// labels each cell's viewport rather than implying an identical comparison.
    /// </summary>
    public const string Gallery = "gallery";

    public static IReadOnlyList<string> All { get; } =
        [BeforeAfter, HorizontalTimeline, VerticalLong, Gallery];

    /// <summary>Bounded to the width of the <c>evolution_images.layout</c> column.</summary>
    public const int MaxLength = 32;

    public static bool IsKnown(string? layout) =>
        layout is not null && All.Contains(layout, StringComparer.Ordinal);
}

/// <summary>
/// The lifecycle states of a rendered evolution image (SCREENSHOT-EVOLUTION-IMAGES-002).
/// </summary>
/// <remarks>
/// A rendered artifact is a draft until a human approves the exact manifest it was rendered
/// from. Approval is this package's own row state, bound to the selection hash and the artifact
/// hash it authorized - never a text approval inherited from another package, because a signature
/// over Markdown says nothing about pixels.
/// </remarks>
public static class EvolutionImageStatus
{
    /// <summary>Rendered, stored, and not approved. The only value rendering ever writes.</summary>
    public const string Draft = "draft";

    /// <summary>A human approved the exact selection hash and artifact hash on this row.</summary>
    public const string Approved = "approved";

    public static IReadOnlyList<string> All { get; } = [Draft, Approved];

    public static bool IsKnown(string? status) =>
        status is not null && All.Contains(status, StringComparer.Ordinal);
}

/// <summary>
/// The bounds the evolution package enforces, beside the record that has to satisfy them
/// (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
public static class EvolutionImageLimits
{
    /// <summary>An evolution compares at least two milestones: one picture is not an evolution.</summary>
    public const int MinMilestones = 2;

    /// <summary>At most twelve milestones in one artifact, so the manifest stays reviewable.</summary>
    public const int MaxMilestones = 12;

    /// <summary>
    /// At most 40 megapixels of rendered canvas, the same ceiling the capture package applies to
    /// one picture. Counted as a 64-bit product so the check itself cannot overflow.
    /// </summary>
    public const long MaxPixelCount = 40_000_000;

    /// <summary>At most 20 MiB of encoded PNG, the same ceiling the media package stores.</summary>
    public const long MaxByteLength = 20L * 1024 * 1024;

    /// <summary>Bounded to the width of the <c>evolution_images.idempotency_key</c> column.</summary>
    public const int IdempotencyKeyMaxLength = 200;
}

/// <summary>
/// One rendered version comparison: the derived PNG's media reference, the manifest that makes
/// it re-derivable, and the approval that authorizes its export
/// (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A derived artifact, not evidence.</b> The manifest cites the media and caption revisions
/// every depicted cell was built from, so the PNG can be rebuilt byte-identically from the same
/// inputs. Nothing here is appended to an evidence bundle and no candidate is promoted by
/// rendering: selection stays the curation package's, and approval stays a decision about this
/// row's two hashes rather than about any bundle.
/// </para>
/// <para>
/// <b>Source bytes stay pinned by evidence, not by this row.</b> Every depicted picture is a
/// selected screenshot, and a selected screenshot is an evidence item referencing its media -
/// which is what stops the media store retiring those bytes. This row pins only its own artifact
/// media through <see cref="ArtifactMediaId"/>: losing the artifact is losing a derivation, and
/// the remedy is rendering again rather than a reference this row cannot honestly hold on
/// somebody else's bytes.
/// </para>
/// </remarks>
/// <param name="id">This artifact's identifier.</param>
/// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
/// <param name="projectId">The project every compared milestone belongs to.</param>
/// <param name="layout">One of <see cref="EvolutionLayouts"/>.</param>
/// <param name="manifestJson">The canonical manifest: order, sources, gaps and provenance.</param>
/// <param name="selectionHash">Lowercase SHA-256 hex over the canonical depicted-source list.</param>
/// <param name="artifactMediaId">The stored PNG this artifact is.</param>
/// <param name="artifactSha256">Lowercase SHA-256 hex over the exact rendered bytes.</param>
/// <param name="width">The rendered canvas width in pixels.</param>
/// <param name="height">The rendered canvas height in pixels.</param>
/// <param name="status">One of <see cref="EvolutionImageStatus"/>.</param>
/// <param name="approvedBy">The session actor that approved, or null while a draft.</param>
/// <param name="approvedAt">When the approval was recorded, or null while a draft.</param>
/// <param name="idempotencyKey">The caller's key, or null when the call named none.</param>
/// <param name="createdAt">When the row was inserted; UTC.</param>
public sealed class EvolutionImage
{
    public EvolutionImage(
        Guid id,
        Guid scopeId,
        Guid projectId,
        string layout,
        string manifestJson,
        string selectionHash,
        Guid artifactMediaId,
        string artifactSha256,
        int width,
        int height,
        string status,
        Guid? approvedBy,
        DateTimeOffset? approvedAt,
        string? idempotencyKey,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An evolution image needs a non-empty id.", nameof(id));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("An evolution image needs a non-empty project.", nameof(projectId));
        }

        if (artifactMediaId == Guid.Empty)
        {
            throw new ArgumentException("An evolution image needs the media it rendered.", nameof(artifactMediaId));
        }

        if (!EvolutionLayouts.IsKnown(layout))
        {
            throw new ArgumentException("The layout is not one this product renders.", nameof(layout));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactSha256);

        if (!IsSha256Hex(selectionHash))
        {
            throw new ArgumentException(
                "The selection hash must be lowercase SHA-256 hex.", nameof(selectionHash));
        }

        if (!IsSha256Hex(artifactSha256))
        {
            throw new ArgumentException(
                "The artifact hash must be lowercase SHA-256 hex.", nameof(artifactSha256));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), "A rendered canvas has positive dimensions.");
        }

        if (!EvolutionImageStatus.IsKnown(status))
        {
            throw new ArgumentException("The status is not one this product stores.", nameof(status));
        }

        if (status == EvolutionImageStatus.Draft && (approvedBy is not null || approvedAt is not null))
        {
            throw new ArgumentException("A draft carries no approval.", nameof(status));
        }

        if (status == EvolutionImageStatus.Approved && (approvedBy is null || approvedAt is null))
        {
            throw new ArgumentException("An approval names its actor and instant.", nameof(status));
        }

        if (idempotencyKey is not null)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey) ||
                idempotencyKey.Length > EvolutionImageLimits.IdempotencyKeyMaxLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(idempotencyKey), "An idempotency key is short non-blank text.");
            }
        }

        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        Layout = layout;
        ManifestJson = manifestJson;
        SelectionHash = selectionHash;
        ArtifactMediaId = artifactMediaId;
        ArtifactSha256 = artifactSha256;
        Width = width;
        Height = height;
        Status = status;
        ApprovedBy = approvedBy;
        ApprovedAt = approvedAt;
        IdempotencyKey = idempotencyKey;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ProjectId { get; }

    public string Layout { get; }

    public string ManifestJson { get; }

    public string SelectionHash { get; }

    public Guid ArtifactMediaId { get; }

    public string ArtifactSha256 { get; }

    public int Width { get; }

    public int Height { get; }

    public string Status { get; }

    public Guid? ApprovedBy { get; }

    public DateTimeOffset? ApprovedAt { get; }

    public string? IdempotencyKey { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>True while this row authorizes an export of the artifact it names.</summary>
    public bool IsApproved => string.Equals(Status, EvolutionImageStatus.Approved, StringComparison.Ordinal);

    /// <summary>A stable diagnostic label for an evolution row. It names the row, never the prose.</summary>
    public static string Label(Guid evolutionId) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"evolution-images/{evolutionId:N}");

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isHex = (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
