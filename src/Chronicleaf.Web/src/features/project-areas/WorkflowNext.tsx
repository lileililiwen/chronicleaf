import { areaPath, type ProjectAreaId } from './areas';

/**
 * The workflow-order next step for one project area
 * (WORKFLOW-WAYFINDING-001).
 *
 * The sub-nav reaches every area in one step from every other area, but it
 * does not say which step comes next in the work itself. This footer does:
 * Overview → Evidence → Evolution → Content → Publish → Overview, which is
 * requirement.md §9's loop (milestone → evidence → screenshot → draft →
 * approve → export). Automations has no footer: it is the trigger/status
 * surface beside the loop, and its own sections already link back to
 * Evidence. Every target is a real area route built by `areaPath` — there
 * is no address this component invents, and pressing the link fires no
 * request (navigation is not a mutation, so footers render on archived
 * projects too).
 */
export function WorkflowNext({
  projectId,
  current,
  onNavigate,
}: {
  readonly projectId: string;
  readonly current: Exclude<ProjectAreaId, 'automations'>;
  readonly onNavigate: (to: string) => void;
}) {
  const step = NEXT[current];
  const target = areaPath(projectId, step.area);

  return (
    <p className="view__actions" data-testid={`workflow-next-${current}`}>
      <span data-testid="workflow-next-copy">
        {step.copy}{' '}
      </span>
      <button
        type="button"
        className="action"
        onClick={() => onNavigate(target)}
        data-testid="workflow-next-link"
      >
        {step.label}
      </button>
    </p>
  );
}

const NEXT: Record<
  Exclude<ProjectAreaId, 'automations'>,
  { readonly area: ProjectAreaId; readonly label: string; readonly copy: string }
> = {
  overview: {
    area: 'evidence',
    label: 'Continue in Evidence',
    copy: 'A confirmed milestone becomes history. Evidence is what a draft is written from — collect it next.',
  },
  evidence: {
    area: 'evolution',
    label: 'Continue in Evolution',
    copy: 'Screenshots are candidates until curation selects them into evidence. Photograph the pages next.',
  },
  evolution: {
    area: 'content',
    label: 'Continue in Content',
    copy: 'Selected pictures depict the story. Write the evidence-grounded draft next.',
  },
  content: {
    area: 'publish',
    label: 'Continue in Publish',
    copy: 'An approved story is ready to leave the product. Publish through a named target or export the site next.',
  },
  publish: {
    area: 'overview',
    label: 'Back to Overview',
    copy: 'Published work stays readable as history. See it in the timeline next.',
  },
};
