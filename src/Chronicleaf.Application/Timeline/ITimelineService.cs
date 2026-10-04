using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Application.Timeline;

/// <summary>
/// The private chronological project timeline (MILESTONE-TIMELINE-001, MILESTONE-TIMELINE-002).
/// </summary>
/// <remarks>
/// This is a read over the milestone records the projects-and-milestones package owns. It keeps no
/// table of its own, writes nothing and changes no status: the confirmed history is what the owner
/// confirmed, in the order it happened.
/// </remarks>
public interface ITimelineService
{
    /// <summary>
    /// One page of a project's timeline, oldest first.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The project does not exist in the owner's scope.</exception>
    Task<TimelinePage> GetTimelineAsync(
        OwnerScope owner,
        Guid projectId,
        TimelineRequest request,
        CancellationToken cancellationToken = default);
}
