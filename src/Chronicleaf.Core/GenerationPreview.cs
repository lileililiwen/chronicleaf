namespace Chronicleaf.Core;

/// <summary>
/// One frozen, owner-authorized redacted input snapshot
/// (LLM-PROVIDER-CONTRACT-002).
/// </summary>
/// <remarks>
/// <para>
/// This row is the consent. It is written once, at the moment the owner asked to see exactly what
/// would be sent, and it is never edited: if anything the snapshot depends on changes afterwards,
/// the answer is a stale consent and a new preview, not a silently corrected row. That is why it
/// stores its own <see cref="Sha256"/> instead of trusting that a later reader will not need to
/// recompute anything.
/// </para>
/// <para>
/// <see cref="SnapshotJson"/> is the complete redacted input, and it is the only source a generation
/// call is ever built from. A provider call that re-read the evidence, the voice profile or the
/// credential after consent would be sending bytes the owner never saw, so the call path takes this
/// string and nothing else.
/// </para>
/// <para>
/// <b>No secret is ever stored here.</b> The snapshot names the credential it was authorized
/// against by identifier and metadata revision only, which is what lets the stored bytes be
/// compared against a vault ciphertext and a secret byte for byte in a test.
/// </para>
/// <para>
/// <see cref="Purpose"/> and <see cref="AggregateRef"/> are not nullable even though the milestone
/// route needs only the milestone: a generic preview has to say what it is a preview <i>of</i>, and
/// a row that could not would make a future owner lookup ambiguous. <see cref="MilestoneId"/> stays
/// nullable so the same row shape serves previews that are not about a milestone once the
/// extension that owns them exists; until then only the milestone purpose is accepted.
/// </para>
/// </remarks>
public sealed class GenerationPreview
{
    /// <param name="id">This preview's identifier. Presented to the owner, never a credential.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="milestoneId">The milestone, when the preview is milestone-scoped.</param>
    /// <param name="purpose">What the generation is for; see <see cref="GenerationPreviewPurposes"/>.</param>
    /// <param name="aggregateRef">The id the purpose applies to, as text.</param>
    /// <param name="snapshotJson">The complete redacted input, canonical JSON, as it will be sent.</param>
    /// <param name="sha256">Lower-case hex SHA-256 over the UTF-8 bytes of <see cref="SnapshotJson"/>.</param>
    /// <param name="expiresAt">When the consent stops being usable. An expired row is kept, not deleted.</param>
    public GenerationPreview(
        Guid id,
        Guid scopeId,
        Guid? milestoneId,
        string purpose,
        string aggregateRef,
        string snapshotJson,
        string sha256,
        DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A preview needs an id.", nameof(id));
        }

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A preview needs a scope.", nameof(scopeId));
        }

        Id = id;
        ScopeId = scopeId;
        MilestoneId = milestoneId;
        Purpose = purpose;
        AggregateRef = aggregateRef;
        SnapshotJson = snapshotJson;
        Sha256 = sha256;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid? MilestoneId { get; }

    public string Purpose { get; }

    public string AggregateRef { get; }

    public string SnapshotJson { get; }

    public string Sha256 { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>True once the injected clock has passed <see cref="ExpiresAt"/>.</summary>
    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;
}

/// <summary>
/// The generation purposes this product has. Only the ones named here can be previewed.
/// </summary>
/// <remarks>
/// The set is deliberately explicit rather than open. A free-text purpose would let a caller ask for
/// a snapshot in a shape no extension has specified - an export, a social post, a publication
/// draft - and the right answer to each of those is a different package with its own approval and
/// redaction rules, not this one guessing. An unrecognised purpose is refused here, and that refusal
/// is a promise: when a purpose is added, the rule for what goes into its snapshot is added with it.
/// </remarks>
public static class GenerationPreviewPurposes
{
    /// <summary>
    /// A milestone story draft. The only purpose enabled, and the reason the milestone route
    /// <c>POST /api/milestones/{id}/generation-preview</c> exists: it is exactly this purpose with
    /// <c>aggregateRef</c> set to the milestone id.
    /// </summary>
    public const string MilestoneStory = "milestone-story";

    public static readonly IReadOnlyList<string> Enabled = [MilestoneStory];

    public static bool IsEnabled(string? purpose) =>
        purpose is not null && Enabled.Contains(purpose, StringComparer.Ordinal);

    /// <summary>Bounded to the same width as the milestone title column it points at.</summary>
    public const int MaxLength = 64;
}
