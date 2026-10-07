import { EvolutionGifPanel } from '../evolution-gifs/EvolutionGifPanel';
import { EvolutionImagePanel } from '../evolution-images/EvolutionImagePanel';
import { MilestoneList } from './MilestoneRows';

/**
 * The Evolution area (PROJECT-AREA-IA-001): rendered version
 * comparisons, chronological animations, and the captures behind them.
 *
 * Project-level panels first (an evolution is about the project, not
 * about any one milestone), then the milestone rows with exactly this
 * area's capture panel. Candidates are never compared and nothing here
 * promotes one, because curation owns that.
 */
export function ProjectEvolution({
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
      {/*
        FEATURE-TRIGGER-PANELS-003. Rendered version comparisons of this project's selected
        screenshots. Candidates are never compared and nothing here promotes
        one, because curation owns that.
      */}
      <EvolutionImagePanel projectId={projectId} />

      {/*
        FEATURE-TRIGGER-PANELS-004. Rendered chronological animations of this project's
        selected screenshots. It sits beside the image panel because it consumes the same
        selection contract - selected screenshots in canvas order with the authorized
        playback - and adds the one thing a still comparison has not: motion. No animation
        is synthesised client-side.
      */}
      <EvolutionGifPanel projectId={projectId} />

      <MilestoneList
        projectId={projectId}
        archived={archived}
        detail="evolution"
        showFilter={false}
        onEditMilestone={onEditMilestone}
      />
    </>
  );
}
