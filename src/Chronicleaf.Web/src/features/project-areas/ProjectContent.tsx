import { NarrativeMemoryPanel } from '../narrative-memory/NarrativeMemoryPanel';
import { VoiceArea } from '../voice-profiles/VoiceArea';
import { MilestoneList } from './MilestoneRows';
import { WorkflowNext } from './WorkflowNext';

/**
 * The Content area (PROJECT-AREA-IA-001): narrative, voice, generation,
 * drafts, variants, and review.
 *
 * Project-level panels first (the explicit history and the writing
 * voice, both about the project rather than any one milestone), then
 * the milestone rows with exactly this area's detail panels. The
 * per-row panels are intentionally not rendered here: rows keep only
 * this area's generation-to-review flow, and the evidence those drafts
 * were written from stays in Evidence where the bundle lives.
 *
 * NOTE: the row-level panels (generation, drafts, review, variants)
 * render through MilestoneList with detail="content"; the project-level
 * narrative and voice panels render once above the rows.
 */
export function ProjectContent({
  projectId,
  archived,
  onEditMilestone,
  onNavigate,
}: {
  readonly projectId: string;
  readonly archived: boolean;
  readonly onEditMilestone: (projectId: string, milestoneId: string) => void;
  readonly onNavigate: (to: string) => void;
}) {
  return (
    <>
      {/*
        NARRATIVE-MEMORY-001/002. The explicit history the owner wrote and the generated
        summary the last refresh concluded, rendered as different facts in separate sections.
        It sits at the top of Content because a narrative is about the project, not about
        any one milestone: nothing in it can be confirmed from here, so there is no milestone
        to scope it to. An archived project still shows what was recorded, read-only, because
        history somebody did not act on is still history.
      */}
      <NarrativeMemoryPanel projectId={projectId} archived={archived} />

      {/*
        VOICE-PROFILES-001/002. The project's writing voice, a check of text against it and the
        drafts already written under it. It sits beside the narrative because both shape what a
        draft becomes: the narrative is what the project remembers, the voice is how it speaks.
        An archived project still shows it read-only: a voice is a fact about how this
        project was written, and the drafts it produced are kept.
      */}
      <VoiceArea projectId={projectId} />

      <MilestoneList
        projectId={projectId}
        archived={archived}
        detail="content"
        showFilter={false}
        onEditMilestone={onEditMilestone}
        onNavigate={onNavigate}
      />

      {/*
        WORKFLOW-WAYFINDING-001. The next step in the loop: an approved story
        is ready to leave the product. The variant rows above already publish
        in place through the delivered publish form — this step reaches the
        Publish area for the journey, the export and the receipts, and adds
        no second publish control. Navigation only — no request fires.
      */}
      <WorkflowNext projectId={projectId} current="content" onNavigate={onNavigate} />
    </>
  );
}
