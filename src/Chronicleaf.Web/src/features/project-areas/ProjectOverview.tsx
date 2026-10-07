import { ProjectTimeline } from '../milestone-timeline/ProjectTimeline';
import { MilestoneList } from './MilestoneRows';

/**
 * The Overview area (PROJECT-AREA-IA-001): milestones + timeline excerpt.
 *
 * The milestone list keeps its status filter and the owner's
 * confirm/dismiss actions, because confirming a record is the decision
 * everything below it consumes. The timeline reads the timeline route
 * rather than re-sorting the list, because the list is newest first and
 * filtered by status while this is chronology.
 */
export function ProjectOverview({
  projectId,
  archived,
  onEditMilestone,
}: {
  readonly projectId: string;
  readonly archived: boolean;
  readonly onEditMilestone: (projectId: string, milestoneId: string) => void;
}) {
  return (
    <>
      <MilestoneList
        projectId={projectId}
        archived={archived}
        detail="none"
        showFilter
        onEditMilestone={onEditMilestone}
      />

      {/*
        MILESTONE-TIMELINE-001/002. The confirmed history oldest first, under the date each
        record happened on. Selecting a row opens the milestone editor for that record.
      */}
      <ProjectTimeline projectId={projectId} onEditMilestone={onEditMilestone} />
    </>
  );
}
