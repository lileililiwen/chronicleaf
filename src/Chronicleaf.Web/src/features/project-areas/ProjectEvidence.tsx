import { GitHubCandidatePanel } from '../github-release-candidates/GitHubCandidatePanel';
import { GitHubConnectionPanel } from '../github-evidence/GitHubConnectionPanel';
import { LocalRepositoryPanel } from '../local-git-evidence/LocalRepositoryPanel';
import { MilestoneList } from './MilestoneRows';
import { WorkflowNext } from './WorkflowNext';

/**
 * The Evidence area (PROJECT-AREA-IA-001): manual evidence, syncs,
 * candidates, curation, and the connections they read from.
 *
 * Project-level panels first (connections, then what the connection
 * produced), then the milestone rows with exactly this area's detail
 * panels: the collected bundle, the two sync forms, and the picker
 * that publishes a choice into the bundle.
 */
export function ProjectEvidence({
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
        GITHUB-EVIDENCE-001. Which repository this project collects from, and the one form that
        changes it. It is here rather than under a milestone because a connection belongs to the
        project: every milestone below asks the same repository, and a per-milestone connection
        would be a second answer to a question the project has already been asked. An archived
        project still shows what it collected, read-only.
      */}
      <GitHubConnectionPanel projectId={projectId} archived={archived} />

      {/*
        LOCAL-GIT-EVIDENCE-001/002. Which local repository this project collects from, and the one form
        that changes it. It is beside the GitHub panel rather than under a milestone for the same reason
        that one is: a registration belongs to the project, and every milestone below asks the same
        repository. The two are separate because they are separate facts - a project may collect from a
        hosted repository, a local one, or both - and a panel that merged them would have to invent an
        answer to "which one?" that the server does not give.
      */}
      <LocalRepositoryPanel projectId={projectId} archived={archived} />

      {/*
        GITHUB-RELEASE-CANDIDATES-001/002. The tag and event derived Milestone candidates this
        project's repository has produced, and the one form that asks for another read. It sits under
        the connection panel because a candidate is something read from the connection above: with no
        repository connected this panel has nothing to read, and it says so rather than showing an
        empty list that would look like a quiet repository.

        It is here and not under a milestone because a candidate is a proposal about the project, not
        about any one milestone: nothing in it can be confirmed from here, so there is no milestone to
        scope it to. An archived project still shows what was proposed, read-only, because a
        candidate somebody did not act on is a fact about the repository and not about the project.
      */}
      <GitHubCandidatePanel projectId={projectId} archived={archived} onNavigate={onNavigate} />

      <MilestoneList
        projectId={projectId}
        archived={archived}
        detail="evidence"
        showFilter={false}
        onEditMilestone={onEditMilestone}
        onNavigate={onNavigate}
      />

      {/*
        WORKFLOW-WAYFINDING-001. The next step in the loop: screenshots are
        candidates until curation selects them into the evidence above.
        Navigation only — no request fires.
      */}
      <WorkflowNext projectId={projectId} current="evidence" onNavigate={onNavigate} />
    </>
  );
}
