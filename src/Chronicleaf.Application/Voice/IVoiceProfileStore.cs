using Chronicleaf.Core;

namespace Chronicleaf.Application.Voice;

/// <summary>
/// The Project side of the voice storage contract (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// Every method is scoped and every statement that writes carries the scope in its WHERE clause, so
/// a forged parameter cannot reach another installation's profile or draft. Reads answer
/// <c>null</c> for a row that is absent <em>or</em> out of scope, which is what makes a
/// foreign-scoped id a 404 rather than a leak.
/// </remarks>
public interface IVoiceProfileStore
{
    /// <summary>Reads a project's profile in a scope, or <c>null</c> when absent or out of scope.</summary>
    Task<VoiceProfile?> FindProfileAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a project's first profile. Returns <c>false</c> when another writer inserted one
    /// first, which the unique <c>(scope_id, project_id)</c> index decides atomically.
    /// </summary>
    Task<bool> InsertProfileAsync(VoiceProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a profile only when the stored revision is exactly
    /// <paramref name="expectedRevision"/>. Returns how many rows changed: 0 means the write lost a
    /// race or the project is gone, and the caller re-reads to say which.
    /// </summary>
    Task<int> ReplaceProfileAsync(
        Guid scopeId,
        Guid projectId,
        long expectedRevision,
        string presetId,
        VoiceOverrides overrides,
        long newRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one draft in a scope, or <c>null</c> when absent or out of scope.</summary>
    Task<VoiceDraft?> FindDraftAsync(Guid scopeId, Guid draftId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a project's drafts ordered <c>createdAt desc, id desc</c>, starting after the cursor
    /// position, returning at most <paramref name="limit"/> rows.
    /// </summary>
    /// <remarks>
    /// The store does not decide whether another page exists: the service asks for one row more than
    /// it intends to return and compares the two counts itself, so the page size has exactly one
    /// owner and a "has more" answer can never be off by the limit that produced it.
    /// </remarks>
    Task<IReadOnlyList<VoiceDraft>> ListDraftsAsync(
        Guid scopeId,
        Guid projectId,
        DateTimeOffset? createdBefore,
        Guid? idBefore,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a draft together with the voice snapshot it was created under.</summary>
    Task<bool> InsertDraftAsync(VoiceDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores new title and body over a draft at exactly <paramref name="expectedRevision"/>. The
    /// stored <c>voice_snapshot</c> is deliberately absent from the SET list: an edit changes the
    /// text an owner wrote and never the voice that text is judged under.
    /// </summary>
    Task<int> ReplaceDraftTextAsync(
        Guid scopeId,
        Guid draftId,
        long expectedRevision,
        string title,
        string body,
        long newRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a draft to <c>approved</c> at exactly <paramref name="expectedRevision"/>. The caller
    /// has already established that no finding remains; this statement only records that fact.
    /// </summary>
    Task<int> ApproveDraftAsync(
        Guid scopeId,
        Guid draftId,
        long expectedRevision,
        long newRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}
