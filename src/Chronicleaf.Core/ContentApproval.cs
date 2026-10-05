using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Chronicleaf.Core;

/// <summary>
/// The payload kinds an approval can bind (MARKDOWN-REVIEW-EXPORT-001).
/// </summary>
/// <remarks>
/// <para>
/// The common approval extension point the design names: an approval binds an immutable payload
/// hash, and the kind says what the hash is over. MVP implements <see cref="Text"/> only - the
/// UTF-8 bytes of one Markdown revision. A structured JSON or visual derivative binds its own
/// checksum through this same table and cannot inherit a text approval, because a signature over
/// one payload says nothing about another.
/// </para>
/// </remarks>
public static class ContentPayloadKinds
{
    /// <summary>A Markdown body, hashed as its exact UTF-8 bytes.</summary>
    public const string Text = "text";

    public static IReadOnlyList<string> All { get; } = [Text];

    public const int MaxLength = 32;

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// One human approval of one content revision (MARKDOWN-REVIEW-EXPORT-001).
/// </summary>
/// <remarks>
/// <para>
/// An approval is a decision about a specific revision and a specific body, never about an asset in
/// general. <see cref="ContentRevision"/> names the revision, <see cref="BodyHash"/> is the
/// digest of exactly the bytes that were approved, and <see cref="EvidenceRevision"/> names the
/// evidence head the approver was looking at. Editing the body, the title, a screenshot caption or
/// selection, or the evidence moves one of those values and the approval stops authorizing exports -
/// which is the requirement that editing approved text or evidence requires re-approval.
/// </para>
/// <para>
/// <see cref="VoiceSnapshotHash"/> and <see cref="MediaSelectionHash"/> are the two provenance
/// values the export re-checks without trusting the revision row: the voice the text was judged
/// under and the set of selected pictures the export would reference. <see cref="ArtifactHash"/>
/// is null for a text approval and is the slot a structured or visual derivative binds through.
/// </para>
/// <para>
/// A row is never deleted. Withdrawing an approval sets <see cref="InvalidatedAt"/> with a reason,
/// so the history of what was once exportable survives the withdrawal - the same reasoning the
/// evidence package used for superseded items.
/// </para>
/// </remarks>
/// <param name="id">This approval's identifier.</param>
/// <param name="scopeId">The owning scope, carried on the row so a scoped read never trusts a join.</param>
/// <param name="assetId">The asset the approved revision belongs to.</param>
/// <param name="contentRevision">The approved revision number, from 1.</param>
/// <param name="actorId">The session actor that approved, or null for a job-owned decision.</param>
/// <param name="payloadKind">One of <see cref="ContentPayloadKinds"/>; this package only writes text.</param>
/// <param name="bodyHash">Lowercase SHA-256 hex over the UTF-8 bytes of the approved body.</param>
/// <param name="evidenceRevision">The evidence head revision the approval was bound to.</param>
/// <param name="voiceSnapshotHash">Lowercase SHA-256 hex over the frozen voice snapshot JSON.</param>
/// <param name="mediaSelectionHash">Lowercase SHA-256 hex over the canonical selected-picture list.</param>
/// <param name="artifactHash">The derivative checksum, or null for a text approval.</param>
/// <param name="approvedAt">When the approval was recorded; UTC.</param>
/// <param name="invalidatedAt">When the approval stopped authorizing exports, or null while live.</param>
/// <param name="reason">Why the approval was invalidated, or null while live.</param>
public sealed class ContentApproval
{
    public ContentApproval(
        Guid id,
        Guid scopeId,
        Guid assetId,
        long contentRevision,
        Guid? actorId,
        string payloadKind,
        string bodyHash,
        long evidenceRevision,
        string voiceSnapshotHash,
        string mediaSelectionHash,
        string? artifactHash,
        DateTimeOffset approvedAt,
        DateTimeOffset? invalidatedAt,
        string? reason)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An approval needs a non-empty id.", nameof(id));
        }

        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("An approval needs an asset.", nameof(assetId));
        }

        if (contentRevision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentRevision), contentRevision, "An approved revision is 1 or greater.");
        }

        if (evidenceRevision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidenceRevision), evidenceRevision, "An evidence revision is 1 or greater.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(payloadKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceSnapshotHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaSelectionHash);

        if (!ContentPayloadKinds.IsKnown(payloadKind))
        {
            throw new ArgumentException("The payload kind is not one this product approves.", nameof(payloadKind));
        }

        if (!IsSha256Hex(bodyHash))
        {
            throw new ArgumentException("The body hash must be lowercase SHA-256 hex.", nameof(bodyHash));
        }

        if (!IsSha256Hex(voiceSnapshotHash))
        {
            throw new ArgumentException(
                "The voice snapshot hash must be lowercase SHA-256 hex.", nameof(voiceSnapshotHash));
        }

        if (!IsSha256Hex(mediaSelectionHash))
        {
            throw new ArgumentException(
                "The media selection hash must be lowercase SHA-256 hex.", nameof(mediaSelectionHash));
        }

        if (artifactHash is not null && !IsSha256Hex(artifactHash))
        {
            throw new ArgumentException(
                "The artifact hash must be lowercase SHA-256 hex when present.", nameof(artifactHash));
        }

        if (invalidatedAt is not null && invalidatedAt < approvedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(invalidatedAt), invalidatedAt, "An approval cannot stop before it started.");
        }

        if (invalidatedAt is null && reason is not null)
        {
            throw new ArgumentException(
                "A live approval carries no invalidation reason.", nameof(reason));
        }

        Id = id;
        ScopeId = scopeId;
        AssetId = assetId;
        ContentRevision = contentRevision;
        ActorId = actorId;
        PayloadKind = payloadKind;
        BodyHash = bodyHash;
        EvidenceRevision = evidenceRevision;
        VoiceSnapshotHash = voiceSnapshotHash;
        MediaSelectionHash = mediaSelectionHash;
        ArtifactHash = artifactHash;
        ApprovedAt = approvedAt;
        InvalidatedAt = invalidatedAt;
        Reason = reason;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid AssetId { get; }

    public long ContentRevision { get; }

    public Guid? ActorId { get; }

    public string PayloadKind { get; }

    public string BodyHash { get; }

    public long EvidenceRevision { get; }

    public string VoiceSnapshotHash { get; }

    public string MediaSelectionHash { get; }

    public string? ArtifactHash { get; }

    public DateTimeOffset ApprovedAt { get; }

    public DateTimeOffset? InvalidatedAt { get; }

    public string? Reason { get; }

    /// <summary>True while this approval authorizes an export of the revision it names.</summary>
    public bool IsLive => InvalidatedAt is null;

    /// <summary>Lowercase hex SHA-256 over the UTF-8 bytes of a value.</summary>
    public static string Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    /// <summary>
    /// The media selection hash for one evidence head: the selected pictures, ordered by item id,
    /// each as <c>mediaId|caption</c>, joined by newlines.
    /// </summary>
    /// <remarks>
    /// The caption is the owner's words on the picture, so it is part of what an export would
    /// reference: editing a caption without reselecting the picture still changes what the export
    /// carries. An empty selection hashes the empty string, which is a defined value rather than an
    /// absence - a draft with no pictures is a state an approval can bind, not a state it cannot see.
    /// </remarks>
    public static string MediaSelectionHashFor(IReadOnlyList<(Guid MediaId, string Caption)> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);

        var canonical = string.Join(
            "\n",
            selected
                .OrderBy(entry => entry.MediaId)
                .Select(entry => string.Create(
                    CultureInfo.InvariantCulture, $"{entry.MediaId:D}|{entry.Caption}")));

        return Hash(canonical);
    }

    /// <summary>A stable diagnostic label for an approval row.</summary>
    public static string Label(Guid approvalId) =>
        string.Create(CultureInfo.InvariantCulture, $"content-approvals/{approvalId:N}");

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
