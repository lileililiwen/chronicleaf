using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Application.Timeline;

/// <summary>
/// The private chronological project timeline over the milestone store
/// (MILESTONE-TIMELINE-001, MILESTONE-TIMELINE-002).
/// </summary>
/// <remarks>
/// <para><b>Confirmed history by default.</b> The timeline shows confirmed records only. The owner
/// may ask for candidates as well, and those arrive labeled as candidates and never as confirmed,
/// because the status on the row is the stored status rather than something this read assigns. A
/// dismissed record is omitted in both views: dismissal keeps the record, and the timeline is the
/// history the owner stands behind rather than everything the database holds.</para>
/// <para><b>Chronology, not recency.</b> The milestone list this package reads from orders newest
/// first; the timeline orders oldest first, because a timeline is read from the beginning. Rows
/// that share an instant are ordered by id, so two records at the same timestamp still have one
/// deterministic position each, and a backdated edit simply moves its row on the next fresh
/// read.</para>
/// <para><b>An archived project stays readable.</b> Archiving makes a project read-only, and a read
/// is what this is: the history is preserved exactly so it can still be told.</para>
/// </remarks>
public sealed class TimelineService(
    IProjectStore projects,
    IMilestoneStore milestones) : ITimelineService
{
    /// <inheritdoc />
    public async Task<TimelinePage> GetTimelineAsync(
        OwnerScope owner,
        Guid projectId,
        TimelineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        // The project is read first, so a project outside the owner's scope is a 404 whether or
        // not it happens to have milestones.
        _ = await projects
            .FindAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false) ?? throw new ResourceNotFoundException("project");

        var limit = MilestonePolicy.ClampPageSize(request.Limit);

        DateTimeOffset? occurredAfter = null;
        Guid? idAfter = null;

        if (request.Cursor is { } cursor)
        {
            (occurredAfter, idAfter) = TimelineCursor.Decode(
                cursor,
                owner.ScopeId,
                projectId,
                request.IncludeCandidates);
        }

        var rows = await milestones
            .ListChronologicalAsync(
                owner.ScopeId,
                projectId,
                request.IncludeCandidates,
                occurredAfter,
                idAfter,
                limit + 1,
                cancellationToken)
            .ConfigureAwait(false);

        var page = rows.Take(limit).Select(TimelineItem.From).ToArray();

        // One row beyond the page, so the caller is only told there is a next page when there is
        // genuinely another row to read.
        string? nextCursor = rows.Count > limit && page.Length > 0
            ? TimelineCursor.Encode(
                owner.ScopeId,
                projectId,
                request.IncludeCandidates,
                page[^1].OccurredAt,
                page[^1].Id)
            : null;

        return new TimelinePage(page, nextCursor);
    }
}
