using Chronicleaf.Core;

namespace Chronicleaf.Application.Timeline;

/// <summary>
/// One row of the private chronological project timeline (MILESTONE-TIMELINE-001).
/// </summary>
/// <remarks>
/// A timeline row is a projection of a stored milestone, not a second record: the id is the
/// milestone's own id, so selecting a row opens the milestone editor for exactly that record.
/// Only <see cref="MilestoneStatus.Confirmed"/> rows are shown unless the owner asks for
/// candidates, and <see cref="MilestoneStatus.Dismissed"/> rows are never shown, because a
/// dismissed record is kept storage the timeline does not narrate.
/// </remarks>
public sealed record TimelineItem(
    Guid Id,
    string Title,
    string? Version,
    DateTimeOffset OccurredAt,
    string Type,
    string Status)
{
    public static TimelineItem From(Milestone milestone) =>
        new(
            milestone.Id,
            milestone.Title,
            milestone.Version,
            milestone.OccurredAt,
            milestone.Type,
            milestone.Status);
}

/// <summary>One page of the timeline, oldest first, plus the next cursor.</summary>
public sealed record TimelinePage(IReadOnlyList<TimelineItem> Items, string? NextCursor);

/// <summary>
/// A timeline read: the page size, the opaque position to continue from, and whether
/// candidate records are shown beside the confirmed history.
/// </summary>
public sealed record TimelineRequest(int? Limit, string? Cursor, bool IncludeCandidates);
