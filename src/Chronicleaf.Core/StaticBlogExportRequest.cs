using System.Globalization;

namespace Chronicleaf.Core;

/// <summary>
/// The words an owner typed when they asked for a static blog export, kept where the job that
/// runs can read them (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This row exists for the same reason <c>content_draft_requests</c> does: a queued job payload
/// carries an operation id and nothing else, so every value the worker needs has to be durable
/// somewhere it can be read back. The row is written once and never updated, because a request
/// that can be edited after it was accepted is a request the worker might act on a second time
/// with different content.
/// </para>
/// <para>
/// <b>What is here is a reference, never the content.</b> <see cref="AssetIdsJson"/> names the
/// approved assets in canonical order, and <see cref="FrozenJson"/> carries the per-asset bindings
/// the request was accepted against - content revision, body hash, evidence bundle and revision,
/// and the media selection hash. The approved bytes themselves are read back from the stored
/// revisions at run time, which is what makes "only approved-while-bindings-hold content is
/// exported" a fact about the stores rather than about this table.
/// </para>
/// </remarks>
/// <param name="id">This request's identifier.</param>
/// <param name="scopeId">The owning scope; the worker's lookup is scoped by it.</param>
/// <param name="operationId">The job that will perform the export.</param>
/// <param name="projectId">The project every selected asset belongs to.</param>
/// <param name="basePath">The normalized deployment base path, such as <c>/journey</c>.</param>
/// <param name="assetIdsJson">The canonical JSON array of selected asset ids, sorted.</param>
/// <param name="selectionHash">Lowercase SHA-256 hex over the canonical selection.</param>
/// <param name="frozenJson">The per-asset approval bindings frozen at request time.</param>
/// <param name="createdAt">When the request was accepted; UTC.</param>
public sealed class StaticBlogExportRequestRow
{
    public StaticBlogExportRequestRow(
        Guid id,
        Guid scopeId,
        Guid operationId,
        Guid projectId,
        string basePath,
        string assetIdsJson,
        string selectionHash,
        string frozenJson,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An export request needs a non-empty id.", nameof(id));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An export request needs the job that will run it.", nameof(operationId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("An export request needs a non-empty project.", nameof(projectId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetIdsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(frozenJson);

        if (!IsSha256Hex(selectionHash))
        {
            throw new ArgumentException(
                "An export request needs the lowercase SHA-256 hex of its canonical selection.",
                nameof(selectionHash));
        }

        Id = id;
        ScopeId = scopeId;
        OperationId = operationId;
        ProjectId = projectId;
        BasePath = basePath;
        AssetIdsJson = assetIdsJson;
        SelectionHash = selectionHash;
        FrozenJson = frozenJson;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    /// <summary>The job, and half of the uniqueness tuple the database enforces.</summary>
    public Guid OperationId { get; }

    public Guid ProjectId { get; }

    public string BasePath { get; }

    /// <summary>The selected asset ids, canonical JSON, sorted ascending.</summary>
    public string AssetIdsJson { get; }

    /// <summary>Lowercase SHA-256 hex over the canonical selection.</summary>
    public string SelectionHash { get; }

    /// <summary>The per-asset approval bindings the request was accepted against.</summary>
    public string FrozenJson { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>A stable diagnostic label for a request row. It names the job, never the content.</summary>
    public static string Label(Guid requestId) =>
        string.Create(CultureInfo.InvariantCulture, $"static-blog-export-requests/{requestId:N}");

    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64)
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
