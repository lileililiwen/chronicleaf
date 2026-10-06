using System.Globalization;

namespace Chronicleaf.Core;

/// <summary>
/// One project's explicit public Journey projection (PUBLIC-PROJECT-JOURNEY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A journey is a projection, not a source of truth.</b> The milestones, the approved bodies,
/// the evidence bundles and the media bytes all live in the stores that already own them; this
/// row carries a slug, an enabled flag, a revision, the canonical selection hash and the frozen
/// snapshot the owner explicitly published. Publication is an explicit act per selection - never
/// a default - and unpublishing removes the projection while every underlying record stays
/// where it was.
/// </para>
/// <para>
/// <b>Nothing private is stored here.</b> <see cref="SnapshotJson"/> carries only what the
/// public routes serve: confirmed milestone titles and dates, approved body text and
/// approval-covered picture references with their captions. Private milestone notes, candidate
/// milestones, unselected pictures and credentials are never read into it, so they cannot leak
/// out of it either. A snapshot whose bindings moved is suspended at read time rather than
/// refreshed, because silently updating public text would be a publication nobody reviewed.
/// </para>
/// </remarks>
/// <param name="id">This journey's identifier.</param>
/// <param name="scopeId">The owning scope; every lookup is scoped by it except the global slug.</param>
/// <param name="projectId">The project this journey projects. One journey per project.</param>
/// <param name="slug">The global public slug, lowercase kebab case.</param>
/// <param name="enabled">Whether the public routes serve this snapshot right now.</param>
/// <param name="revision">The optimistic revision every mutation compares against.</param>
/// <param name="selectionHash">Lowercase SHA-256 hex over the canonical published selection.</param>
/// <param name="snapshotRef">Lowercase SHA-256 hex over the canonical published snapshot.</param>
/// <param name="snapshotJson">The immutable published projection the public routes serve.</param>
/// <param name="revokedAt">When the owner last revoked this journey, if ever; UTC.</param>
/// <param name="createdAt">When the journey row was created; UTC.</param>
/// <param name="updatedAt">When the journey row was last changed; UTC.</param>
public sealed class ProjectJourneyRow
{
    public ProjectJourneyRow(
        Guid id,
        Guid scopeId,
        Guid projectId,
        string slug,
        bool enabled,
        long revision,
        string selectionHash,
        string snapshotRef,
        string snapshotJson,
        DateTimeOffset? revokedAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A journey needs a non-empty id.", nameof(id));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A journey needs a non-empty project.", nameof(projectId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "A journey revision is 0 or greater.");
        }

        if (!IsSha256Hex(selectionHash))
        {
            throw new ArgumentException(
                "A journey needs the lowercase SHA-256 hex of its canonical selection.",
                nameof(selectionHash));
        }

        if (!IsSha256Hex(snapshotRef))
        {
            throw new ArgumentException(
                "A journey needs the lowercase SHA-256 hex of its canonical snapshot.",
                nameof(snapshotRef));
        }

        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        Slug = slug;
        Enabled = enabled;
        Revision = revision;
        SelectionHash = selectionHash;
        SnapshotRef = snapshotRef;
        SnapshotJson = snapshotJson;
        RevokedAt = revokedAt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ProjectId { get; }

    public string Slug { get; }

    public bool Enabled { get; }

    public long Revision { get; }

    /// <summary>Lowercase SHA-256 hex over the canonical published selection.</summary>
    public string SelectionHash { get; }

    /// <summary>Lowercase SHA-256 hex over the canonical published snapshot.</summary>
    public string SnapshotRef { get; }

    /// <summary>The immutable published projection the public routes serve.</summary>
    public string SnapshotJson { get; }

    public DateTimeOffset? RevokedAt { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>A stable diagnostic label for a journey row. It names the project, never the content.</summary>
    public static string Label(Guid projectId) =>
        string.Create(CultureInfo.InvariantCulture, $"project-journeys/{projectId:N}");

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
