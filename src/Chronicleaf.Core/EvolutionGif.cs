namespace Chronicleaf.Core;

/// <summary>
/// The lifecycle states of a rendered evolution GIF (EVOLUTION-GIF-EXPORT-002).
/// </summary>
/// <remarks>
/// A rendered artifact is a draft until a human approves the exact manifest it was rendered
/// from. Approval is this package's own row state, bound to the selection hash and the artifact
/// hash it authorized - never a text approval inherited from another package, because a signature
/// over Markdown says nothing about pixels or motion.
/// </remarks>
public static class EvolutionGifStatus
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
/// The bounds the evolution GIF package enforces, beside the record that has to satisfy them
/// (EVOLUTION-GIF-EXPORT-001/002).
/// </summary>
public static class EvolutionGifLimits
{
    /// <summary>A GIF animates at least two frames: one picture is not an evolution.</summary>
    public const int MinFrames = 2;

    /// <summary>At most twelve frames in one artifact, so the manifest stays reviewable.</summary>
    public const int MaxFrames = 12;

    /// <summary>Each frame shows at least half a second: faster is a flicker, not a review.</summary>
    public const int MinFrameDurationMs = 500;

    /// <summary>Each frame shows at most five seconds: longer is a slideshow, not an animation.</summary>
    public const int MaxFrameDurationMs = 5000;

    /// <summary>The default frame duration, one second, when the request names none.</summary>
    public const int DefaultFrameDurationMs = 1000;

    /// <summary>At most sixty seconds of total playback, so the artifact stays portable.</summary>
    public const int MaxTotalDurationMs = 60_000;

    /// <summary>
    /// At most 1280 pixels on either canvas side, the same ceiling the design fixes for the GIF
    /// canvas. Counted per side rather than as a product, because a timeline canvas is one frame
    /// wide and one frame tall.
    /// </summary>
    public const int MaxDimension = 1280;

    /// <summary>At most 20 MiB of encoded GIF, the same ceiling the media package stores.</summary>
    public const long MaxByteLength = 20L * 1024 * 1024;

    /// <summary>Bounded to the width of the <c>evolution_gifs.idempotency_key</c> column.</summary>
    public const int IdempotencyKeyMaxLength = 200;
}

/// <summary>
/// One rendered chronological GIF: the derived bytes beside the manifest that makes them
/// re-derivable, and the approval that authorizes their export
/// (EVOLUTION-GIF-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A derived artifact, not evidence.</b> The manifest cites the media and caption revisions
/// every depicted frame was built from, in canvas order, so the GIF can be rebuilt
/// byte-identically from the same inputs. Nothing here is appended to an evidence bundle and no
/// candidate is promoted by rendering: selection stays the curation package's, and approval stays
/// a decision about this row's two hashes rather than about any bundle.
/// </para>
/// <para>
/// <b>GIF bytes live here because the media store is PNG-only by its own contract.</b> The
/// delivered media package accepts exactly <c>image/png</c> (LOCAL-MEDIA-STORAGE-001), and
/// widening that vocabulary is that package's boundary, not this one's. This row therefore
/// carries its own artifact bytes beside the manifest, written in the same INSERT as the row -
/// so a row can never point at bytes that were not stored, and a render that fails stores
/// nothing at all.
/// </para>
/// </remarks>
/// <param name="id">This artifact's identifier.</param>
/// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
/// <param name="projectId">The project every depicted milestone belongs to.</param>
/// <param name="manifestJson">The canonical manifest: frame order, sources, timings and provenance.</param>
/// <param name="selectionHash">Lowercase SHA-256 hex over the canonical frame list and timings.</param>
/// <param name="artifactSha256">Lowercase SHA-256 hex over the exact rendered bytes.</param>
/// <param name="artifactBytes">The exact rendered GIF bytes.</param>
/// <param name="width">The GIF canvas width in pixels.</param>
/// <param name="height">The GIF canvas height in pixels.</param>
/// <param name="frameCount">How many frames the GIF carries.</param>
/// <param name="totalDurationMs">The sum of the per-frame durations in milliseconds.</param>
/// <param name="loop">Whether the GIF loops indefinitely.</param>
/// <param name="status">One of <see cref="EvolutionGifStatus"/>.</param>
/// <param name="approvedBy">The session actor that approved, or null while a draft.</param>
/// <param name="approvedAt">When the approval was recorded, or null while a draft.</param>
/// <param name="idempotencyKey">The caller's key, or null when the call named none.</param>
/// <param name="createdAt">When the row was inserted; UTC.</param>
public sealed class EvolutionGif
{
    public EvolutionGif(
        Guid id,
        Guid scopeId,
        Guid projectId,
        string manifestJson,
        string selectionHash,
        string artifactSha256,
        byte[] artifactBytes,
        int width,
        int height,
        int frameCount,
        int totalDurationMs,
        bool loop,
        string status,
        Guid? approvedBy,
        DateTimeOffset? approvedAt,
        string? idempotencyKey,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An evolution GIF needs a non-empty id.", nameof(id));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("An evolution GIF needs a non-empty project.", nameof(projectId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactSha256);
        ArgumentNullException.ThrowIfNull(artifactBytes);

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

        if (artifactBytes.Length == 0)
        {
            throw new ArgumentException("An evolution GIF carries rendered bytes.", nameof(artifactBytes));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), "A rendered canvas has positive dimensions.");
        }

        if (frameCount < EvolutionGifLimits.MinFrames || frameCount > EvolutionGifLimits.MaxFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameCount), "An evolution GIF animates two to twelve frames.");
        }

        if (totalDurationMs <= 0 || totalDurationMs > EvolutionGifLimits.MaxTotalDurationMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalDurationMs), "An evolution GIF plays at most sixty seconds.");
        }

        if (!EvolutionGifStatus.IsKnown(status))
        {
            throw new ArgumentException("The status is not one this product stores.", nameof(status));
        }

        if (status == EvolutionGifStatus.Draft && (approvedBy is not null || approvedAt is not null))
        {
            throw new ArgumentException("A draft carries no approval.", nameof(status));
        }

        if (status == EvolutionGifStatus.Approved && (approvedBy is null || approvedAt is null))
        {
            throw new ArgumentException("An approval names its actor and instant.", nameof(status));
        }

        if (idempotencyKey is not null)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey) ||
                idempotencyKey.Length > EvolutionGifLimits.IdempotencyKeyMaxLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(idempotencyKey), "An idempotency key is short non-blank text.");
            }
        }

        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        ManifestJson = manifestJson;
        SelectionHash = selectionHash;
        ArtifactSha256 = artifactSha256;
        ArtifactBytes = artifactBytes;
        Width = width;
        Height = height;
        FrameCount = frameCount;
        TotalDurationMs = totalDurationMs;
        Loop = loop;
        Status = status;
        ApprovedBy = approvedBy;
        ApprovedAt = approvedAt;
        IdempotencyKey = idempotencyKey;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ProjectId { get; }

    public string ManifestJson { get; }

    public string SelectionHash { get; }

    public string ArtifactSha256 { get; }

    public byte[] ArtifactBytes { get; }

    public int Width { get; }

    public int Height { get; }

    public int FrameCount { get; }

    public int TotalDurationMs { get; }

    public bool Loop { get; }

    public string Status { get; }

    public Guid? ApprovedBy { get; }

    public DateTimeOffset? ApprovedAt { get; }

    public string? IdempotencyKey { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>True while this row authorizes an export of the artifact it names.</summary>
    public bool IsApproved => string.Equals(Status, EvolutionGifStatus.Approved, StringComparison.Ordinal);

    /// <summary>A stable diagnostic label for a GIF row. It names the row, never the prose.</summary>
    public static string Label(Guid gifId) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"evolution-gifs/{gifId:N}");

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
