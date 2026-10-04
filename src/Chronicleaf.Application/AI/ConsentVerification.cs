using Chronicleaf.Core;

namespace Chronicleaf.Application.AI;

/// <summary>
/// Answers one question about a stored consent: is the thing the owner authorized still the thing that
/// would be sent? (LLM-PROVIDER-CONTRACT-002)
/// </summary>
/// <remarks>
/// <para>
/// This is the one place that answer is computed, and it is shared rather than duplicated for a reason.
/// The generate call and every later extension that consumes a consent need exactly the same three
/// checks - not expired, the presented hash is this preview's, and the stored bytes are the bytes that
/// were hashed - and a second copy of the recomputation is a second thing that can drift. When the
/// binding document gains a member and the first copy is updated, a draft job running the stale copy
/// would accept a consent whose stored snapshot had been altered, and nothing about the failure would
/// look unusual.
/// </para>
/// <para>
/// <b>It reads only the stored row.</b> Nothing here re-reads the evidence, the voice profile or the
/// credential, because a check that re-reads those is the "make it look current" path this whole
/// contract exists to prevent. The revisions the authorization was bound to are in the snapshot, and a
/// caller that needs them compared against the world does that itself - and owns the reason it gives
/// when they differ.
/// </para>
/// </remarks>
public static class ConsentVerification
{
    /// <summary>
    /// Recomputes the consent binding from a stored preview, so the recomputation is over the row
    /// rather than over a value some process is holding.
    /// </summary>
    /// <remarks>
    /// The preview is stored without its consent hash and the hash is derived, so there is no second
    /// column that could disagree with the bytes it covers. If the two ever did differ, the derived
    /// value is what a call compares against, so an edited row is stale by construction rather than
    /// because a second field was missed.
    /// </remarks>
    public static string StoredHash(GenerationPreview preview, Guid scopeId)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var snapshot = GenerationSnapshot.Deserialize(preview.SnapshotJson);

        return ConsentBinding.Compute(
            scopeId,
            snapshot.Purpose,
            snapshot.AggregateRef,
            snapshot.ProviderId,
            snapshot.Model,
            snapshot.CredentialRevision,
            snapshot.EvidenceRevision,
            snapshot.VoiceRevision,
            snapshot.InputEvidenceIds,
            snapshot.NarrativeSelections,
            preview.Sha256);
    }

    /// <summary>
    /// Decides whether a stored consent may still be used, and says which part of it is stale.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the consent is usable, or one of the <see cref="ConsentStaleReasons"/> words
    /// when it is not. A reason and never a message, so the caller can build its own text and so nothing
    /// here can leak the stored snapshot's contents.
    /// </returns>
    public static string? Revoke(
        GenerationPreview preview,
        Guid scopeId,
        string? presentedHash,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(preview);

        if (preview.IsExpiredAt(now))
        {
            return ConsentStaleReasons.Expired;
        }

        if (!ConsentBinding.Matches(presentedHash, StoredHash(preview, scopeId)))
        {
            return ConsentStaleReasons.HashMismatch;
        }

        // The stored bytes no longer hash to the digest they were stored with. Either the row or the
        // snapshot was altered, and in both cases the owner is no longer looking at what would be sent.
        if (!ConsentBinding
            .Sha256Hex(ConsentBinding.CanonicalJson(GenerationSnapshot.Deserialize(preview.SnapshotJson).Json))
            .Equals(preview.Sha256, StringComparison.Ordinal))
        {
            return ConsentStaleReasons.SnapshotAltered;
        }

        return null;
    }

    /// <summary>
    /// The preview's snapshot, read once, for a caller that has already verified the consent.
    /// </summary>
    public static GenerationSnapshot SnapshotOf(GenerationPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        return GenerationSnapshot.Deserialize(preview.SnapshotJson);
    }
}
