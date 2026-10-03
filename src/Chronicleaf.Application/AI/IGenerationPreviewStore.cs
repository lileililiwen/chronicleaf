using Chronicleaf.Core;

namespace Chronicleaf.Application.AI;

/// <summary>
/// The store for owner-authorized generation previews (LLM-PROVIDER-CONTRACT-002).
/// </summary>
/// <remarks>
/// <para>
/// Two methods, and the shape of both is the requirement. <see cref="InsertAsync"/> writes a row
/// once and nothing ever changes it, because a consent that can be edited in place is a consent the
/// owner never gave. <see cref="FindAsync"/> is scoped, so a preview belonging to another scope
/// answers <c>null</c> and the route turns that into a 404 rather than a 409 - a caller must not be
/// able to learn that somebody else's preview exists, still less that it is expired.
/// </para>
/// <para>
/// There is deliberately no delete. An expired preview is a real state with a real answer, 409, and
/// a store that swept expired rows would turn that into a 404 and tell the caller to go looking for
/// another preview instead of to authorize a new one. When these rows are eventually cleaned up it
/// will be a decided lifecycle question, not a side effect of somebody's next request.
/// </para>
/// <para>
/// The store holds the snapshot and never interprets it. Nothing here parses the JSON, so a row
/// cannot be half-understood by one component and fully understood by another.
/// </para>
/// </remarks>
public interface IGenerationPreviewStore
{
    /// <summary>
    /// Writes one preview. Returns false when a row with this id already exists, which is the
    /// uniqueness constraint answering rather than an exception.
    /// </summary>
    Task<bool> InsertAsync(GenerationPreview preview, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one preview inside a scope, or <c>null</c> when it is absent or owned by another scope.
    /// </summary>
    Task<GenerationPreview?> FindAsync(
        Guid scopeId,
        Guid previewId,
        CancellationToken cancellationToken = default);
}
