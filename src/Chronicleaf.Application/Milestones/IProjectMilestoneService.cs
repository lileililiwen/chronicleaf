using Chronicleaf.Core;

namespace Chronicleaf.Application.Milestones;

/// <summary>
/// The Project and Milestone use cases (PROJECTS-AND-MILESTONES-001, PROJECTS-AND-MILESTONES-002).
/// </summary>
/// <remarks>
/// Every call carries the owner's scope, taken from the access package's validated session, and every
/// call runs inside one unit of work so the record, its revision and its audit row are committed
/// together or not at all. The three rules this interface exists to keep are stated once here and
/// implemented once in <see cref="ProjectMilestoneService"/>:
/// <list type="bullet">
/// <item><description>A record that arrives from a source is a candidate until the owner confirms it,
/// and nothing except the owner's confirm action can make it confirmed.</description></item>
/// <item><description>An edit presents the revision it read. A missing revision is refused, a stale
/// one loses to the edit that got there first, and no accepted edit can overwrite another.</description></item>
/// <item><description>An archived project is read-only and its history is preserved; archiving is a
/// stamp, never an erase.</description></item>
/// </list>
/// </remarks>
public interface IProjectMilestoneService
{
    /// <summary>One page of the owner's projects, with an opaque scope-bound cursor.</summary>
    Task<ProjectPage> ListProjectsAsync(OwnerScope owner, PageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates a project in the owner's scope.</summary>
    Task<Project> CreateProjectAsync(OwnerScope owner, CreateProjectInput input, CancellationToken cancellationToken = default);

    /// <summary>Reads one project.</summary>
    /// <exception cref="ResourceNotFoundException">The project does not exist in the owner's scope.</exception>
    Task<Project> GetProjectAsync(OwnerScope owner, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Applies a partial edit at the revision the caller read.</summary>
    /// <exception cref="RevisionConflictException">The stored revision is no longer the one presented.</exception>
    /// <exception cref="ProjectArchivedException">The project is archived, so it is read-only.</exception>
    Task<Project> UpdateProjectAsync(
        OwnerScope owner,
        Guid projectId,
        long expectedRevision,
        ProjectPatch patch,
        CancellationToken cancellationToken = default);

    /// <summary>Archives a project. Nothing under it is erased, and the history stays readable.</summary>
    Task<Project> ArchiveProjectAsync(
        OwnerScope owner,
        Guid projectId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>One page of a project's milestones, newest first.</summary>
    Task<MilestonePage> ListMilestonesAsync(
        OwnerScope owner,
        Guid projectId,
        MilestoneListRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a milestone the owner typed. The explicit save is the confirmation, so the record is
    /// stored as confirmed: it was not imported and there is nothing to review.
    /// </summary>
    Task<Milestone> CreateManualMilestoneAsync(
        OwnerScope owner,
        Guid projectId,
        MilestoneDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes upstream records in as candidates. Every record that comes in is a candidate whatever the
    /// source claims, because the source is not what decides status: the owner is. A key that is
    /// already present is reported as the candidate that already exists.
    /// </summary>
    Task<ImportedCandidates> ImportCandidatesAsync(
        OwnerScope owner,
        Guid projectId,
        ImportCandidatesInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one milestone.</summary>
    /// <exception cref="ResourceNotFoundException">The milestone does not exist in the owner's scope.</exception>
    Task<Milestone> GetMilestoneAsync(OwnerScope owner, Guid milestoneId, CancellationToken cancellationToken = default);

    /// <summary>Applies a partial field edit. The status is not editable here, by construction.</summary>
    Task<Milestone> UpdateMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        long expectedRevision,
        MilestonePatch patch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The owner's confirmation. A candidate becomes confirmed; a dismissed record returns to
    /// confirmed because the owner asked for it; confirming something already confirmed changes
    /// nothing and reports the same record back.
    /// </summary>
    Task<Milestone> ConfirmMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The owner's dismissal. A dismissed record is kept, not erased, and only the owner can bring it
    /// back.
    /// </summary>
    Task<Milestone> DismissMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}
