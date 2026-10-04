import { useState } from 'react';

import {
  MilestoneApiError,
  archiveProject,
  confirmMilestone,
  dismissMilestone,
  getProject,
  listMilestones,
} from './api';
import { GitHubConnectionPanel } from '../github-evidence/GitHubConnectionPanel';
import { GitHubSyncPanel } from '../github-evidence/GitHubSyncPanel';
import { GroundedMarkdownDraftPanel } from '../grounded-markdown-drafts/GroundedMarkdownDraftPanel';
import { MilestoneEvidencePanel } from '../milestone-evidence/MilestoneEvidencePanel';
import { LlmProviderArea } from '../llm-provider-contract/LlmProviderArea';
import { ScreenshotCapturePanel } from '../screenshot-capture/ScreenshotCapturePanel';
import { ScreenshotPicker } from '../screenshot-curation/ScreenshotPicker';
import { VoiceArea } from '../voice-profiles/VoiceArea';
import { milestoneTypeLabel, type Milestone, type MilestoneStatus, type Project } from './types';
import { useMilestones, useProject } from './useRecords';

/**
 * One project and the milestones actually stored under it.
 *
 * Three states are visible here and each is shown as what it is. A project with no milestones says
 * so. A record that arrived from a source is shown as a candidate, with the owner's own confirm and
 * dismiss actions beside it and nothing else able to move it. An archived project shows its history
 * unchanged and offers no way to change anything, because the server would refuse every one of them.
 *
 * The confirm and dismiss buttons are the only controls in this client that change a status, and
 * they are the owner's: there is no bulk action, no "accept all" and no automation.
 */
export function ProjectDetail({
  projectId,
  onEditProject,
  onNewMilestone,
  onEditMilestone,
  onBack,
}: {
  readonly projectId: string;
  readonly onEditProject: (projectId: string) => void;
  readonly onNewMilestone: (projectId: string) => void;
  readonly onEditMilestone: (projectId: string, milestoneId: string) => void;
  readonly onBack: () => void;
}) {
  const project = useProject(getProject, projectId);
  const [status, setStatus] = useState<MilestoneStatus | 'all'>('all');
  const milestones = useMilestones(listMilestones, projectId, status);
  const [actionFailure, setActionFailure] = useState<MilestoneApiError | null>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  if (project.failure?.status === 404) {
    return (
      <section className="view" data-testid="project-missing">
        <h2 className="view__heading">Project not found</h2>
        <p className="view__state">
          This project is not part of this installation, so there is nothing to show.
        </p>
        <button type="button" className="action" onClick={onBack} data-testid="project-missing-back">
          Back to projects
        </button>
      </section>
    );
  }

  const record: Project | null = project.data;
  const archived = record?.archivedAt !== null && record?.archivedAt !== undefined;

  const act = async (milestone: Milestone, action: 'confirm' | 'dismiss') => {
    setBusyId(milestone.id);
    setActionFailure(null);
    setActionNotice(null);

    try {
      const saved =
        action === 'confirm'
          ? await confirmMilestone(milestone.id, milestone.revision)
          : await dismissMilestone(milestone.id, milestone.revision);

      // The list is re-read rather than patched locally: the record that is now stored is the one
      // the owner sees, including any revision the action advanced.
      milestones.reload();
      setActionNotice(
        action === 'confirm'
          ? `“${saved.title}” is now confirmed.`
          : `“${saved.title}” is dismissed and kept. Confirm it to bring it back.`,
      );
    } catch (thrown) {
      setActionFailure(thrown instanceof MilestoneApiError ? thrown : null);
      milestones.reload();
    } finally {
      setBusyId(null);
    }
  };

  const archive = async () => {
    if (record === null) {
      return;
    }

    setBusyId('project');
    setActionFailure(null);
    setActionNotice(null);

    try {
      await archiveProject(record.id, record.revision);
      project.reload();
      setActionNotice('The project is archived. Its milestones are kept and stay readable.');
    } catch (thrown) {
      setActionFailure(thrown instanceof MilestoneApiError ? thrown : null);
    } finally {
      setBusyId(null);
    }
  };

  return (
    <section className="view" data-testid="project-detail">
      <p className="view__crumbs">
        <button type="button" className="link" onClick={onBack} data-testid="project-back">
          ← Projects
        </button>
      </p>

      {project.loading || record === null ? (
        <p className="view__state" data-testid="project-loading">
          Loading this project…
        </p>
      ) : (
        <header className="project__header">
          <h2 className="view__heading" data-testid="project-name">
            {record.name}
          </h2>
          {archived ? (
            <p className="notice notice--muted" data-testid="project-archived">
              Archived on {formatInstant(record.archivedAt ?? '')}. This project is read-only and its
              milestones are kept exactly as they were.
            </p>
          ) : null}
          {record.description ? (
            <p className="project__description" data-testid="project-description">
              {record.description}
            </p>
          ) : null}
          {record.projectUrl ? (
            <p className="project__url" data-testid="project-url">
              <span className="project__url-label">Project URL</span>{' '}
              <a href={record.projectUrl} rel="noreferrer noopener">
                {record.projectUrl}
              </a>
            </p>
          ) : null}

          {!archived ? (
            <div className="view__actions">
              <button type="button" className="action" onClick={() => onEditProject(record.id)} data-testid="project-edit">
                Edit project
              </button>
              <button
                type="button"
                className="action"
                onClick={() => onNewMilestone(record.id)}
                data-testid="milestone-new"
              >
                Record a milestone
              </button>
              <button
                type="button"
                className="action action--danger"
                onClick={() => void archive()}
                disabled={busyId === 'project'}
                data-testid="project-archive"
              >
                {busyId === 'project' ? 'Archiving…' : 'Archive project'}
              </button>
            </div>
          ) : null}

          {/*
            VOICE-PROFILES-001/002. The project's writing voice, a check of text against it and the
            drafts already written under it. It sits behind its own toggle because it is three
            surfaces and an owner opening a project to record a milestone does not need all of them
            at once. An archived project still shows it read-only: a voice is a fact about how this
            project was written, and the drafts it produced are kept.
          */}
          <VoiceArea projectId={record.id} />

          {/*
            GITHUB-EVIDENCE-001. Which repository this project collects from, and the one form that
            changes it. It is here rather than under a milestone because a connection belongs to the
            project: every milestone below asks the same repository, and a per-milestone connection
            would be a second answer to a question the project has already been asked. An archived
            project still shows what it collected, read-only.
          */}
          <GitHubConnectionPanel projectId={record.id} archived={archived} />
        </header>
      )}

      {actionNotice ? (
        <p className="notice notice--ok" role="status" data-testid="project-notice">
          {actionNotice}
        </p>
      ) : null}

      {actionFailure ? (
        <p className="notice notice--error" role="alert" data-testid="project-action-error">
          {actionFailure.message}
        </p>
      ) : null}

      <div className="filters" role="group" aria-label="Filter milestones by status">
        {(['all', 'confirmed', 'candidate', 'dismissed'] as const).map((candidate) => (
          <button
            key={candidate}
            type="button"
            className={candidate === status ? 'filter filter--active' : 'filter'}
            aria-pressed={candidate === status}
            onClick={() => setStatus(candidate)}
            data-testid={`milestone-filter-${candidate}`}
          >
            {candidate === 'all' ? 'All' : candidate}
          </button>
        ))}
      </div>

      {milestones.loading ? (
        <p className="view__state" data-testid="milestones-loading">
          Loading milestones…
        </p>
      ) : milestones.failure ? (
        <p className="notice notice--error" role="alert" data-testid="milestones-error">
          {milestones.failure.message}
        </p>
      ) : (milestones.data?.items.length ?? 0) === 0 ? (
        <div className="empty" data-testid="milestones-empty">
          <p className="empty__title">
            {status === 'all' ? 'No milestones recorded yet.' : `No ${status} milestones.`}
          </p>
          <p className="empty__body">
            {status === 'all'
              ? 'Nothing has been recorded for this project. What you record here becomes the history a story is written from.'
              : 'Change the filter to see the other milestones.'}
          </p>
        </div>
      ) : (
        <ul className="milestones" data-testid="milestone-list">
          {milestones.data?.items.map((milestone) => (
            <MilestoneRow
              key={milestone.id}
              milestone={milestone}
              archived={archived}
              busy={busyId === milestone.id}
              onEdit={() => onEditMilestone(projectId, milestone.id)}
              onConfirm={() => void act(milestone, 'confirm')}
              onDismiss={() => void act(milestone, 'dismiss')}
            />
          ))}
        </ul>
      )}
    </section>
  );
}

function MilestoneRow({
  milestone,
  archived,
  busy,
  onEdit,
  onConfirm,
  onDismiss,
}: {
  readonly milestone: Milestone;
  readonly archived: boolean;
  readonly busy: boolean;
  readonly onEdit: () => void;
  readonly onConfirm: () => void;
  readonly onDismiss: () => void;
}) {
  // How many collections this milestone has finished, and therefore how many times the evidence
  // panel above has been told to read the bundle again. The count lives here because this row is the
  // only place both panels are in the same scope: neither feature has to know the other exists, and
  // the row is a plain number passed to one and a plain function passed to the other.
  const [settledCollections, setSettledCollections] = useState(0);

  return (
    <li className="milestone" data-testid="milestone" data-status={milestone.status}>
      <div className="milestone__head">
        <h3 className="milestone__title" data-testid="milestone-title">
          {milestone.title}
        </h3>
        <span
          className={
            milestone.status === 'confirmed'
              ? 'badge badge--ok'
              : milestone.status === 'candidate'
                ? 'badge badge--pending'
                : 'badge badge--muted'
          }
          data-testid="milestone-status"
        >
          {milestone.status}
        </span>
      </div>

      <p className="milestone__meta">
        <span data-testid="milestone-type">{milestoneTypeLabel(milestone.type)}</span>
        {milestone.version ? <span data-testid="milestone-version"> · {milestone.version}</span> : null}
        <span data-testid="milestone-occurred-at"> · {formatInstant(milestone.occurredAt)}</span>
        <span data-testid="milestone-revision"> · revision {milestone.revision}</span>
        {milestone.sourceKey ? (
          <span data-testid="milestone-source">
            {' '}
            · from {milestone.source} ({milestone.sourceKey})
          </span>
        ) : null}
      </p>

      {milestone.notes ? <p className="milestone__notes" data-testid="milestone-notes">{milestone.notes}</p> : null}

      {milestone.knownLimitations.length > 0 ? (
        <div className="milestone__list" data-testid="milestone-limitations">
          <h4>Known limitations</h4>
          <ul>
            {milestone.knownLimitations.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        </div>
      ) : null}

      {milestone.nextSteps.length > 0 ? (
        <div className="milestone__list" data-testid="milestone-next-steps">
          <h4>Next steps</h4>
          <ul>
            {milestone.nextSteps.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        </div>
      ) : null}

      {/* What was actually collected for this milestone, read from the server on its own route. The
          reload token is the one thing passed in from below: a collection that finishes appends a
          revision this panel did not ask for, and a token that changed is how it hears about it. */}
      <MilestoneEvidencePanel
        milestoneId={milestone.id}
        milestoneTitle={milestone.title}
        archived={archived}
        reloadToken={settledCollections}
      />

      {/* GITHUB-EVIDENCE-001/002. Asking this milestone's repository for a ref, and following the job
          that does it. It goes directly under the evidence panel because the two are the same subject:
          what is collected is appended to the bundle above, and the revision this form sends is the
          revision that bundle is on. Above the draft below, because a draft is written from evidence
          and a collection is how more of it arrives.

          When the job reaches a status it will not leave, once, the evidence panel above is told to
          read the bundle again - so an owner watching a collection land sees what the server holds
          without reloading the page, and no panel here starts polling for a change this row is
          already told about. */}
      <GitHubSyncPanel
        projectId={milestone.projectId}
        milestoneId={milestone.id}
        milestoneTitle={milestone.title}
        archived={archived}
        onSettled={() => setSettledCollections((value) => value + 1)}
      />

      {/* A bounded draft from exactly that evidence, through a provider the owner named. The panel is
          absent for an archived milestone: a generation is a draft for a live record, and offering
          one for something that can no longer change would invite work nobody can finish. */}
      <LlmProviderArea
        milestoneId={milestone.id}
        milestoneTitle={milestone.title}
        archived={archived}
      />

      {/* GROUNDED-MARKDOWN-DRAFTS-001/002. A draft written from exactly the evidence above, through a
          provider the owner names, and shown with its citations and both lists of findings. It sits under
          the generation panel because it consumes that panel's contract - the consent handshake - and
          drives it itself so an owner sees one flow rather than being sent somewhere else to authorize
          one.

          The panel offers no approval and no edit. A draft is text a human has not reviewed, its body is
          rendered as source rather than as HTML, and a job that stops says the server's own refusal in
          words while re-reading the asset from the server rather than inferring the outcome from a
          status. Human editing and approval are the review package's, bound to a revision and a body
          hash. */}
      <GroundedMarkdownDraftPanel
        milestoneId={milestone.id}
        milestoneTitle={milestone.title}
        archived={archived}
        confirmed={milestone.status === 'confirmed'}
      />

      {/* Bounded screenshots of one public page for that milestone, which are candidates and nothing
          else. The panel asks for a job rather than producing a picture, and says so while it waits:
          a capture is browser work in the delivered queue, so the owner is told the work was accepted
          and nothing more. It offers no way to select a candidate or attach one to evidence, because
          screenshot-curation owns that and reads these rows below. On a host that cannot confine a
          browser the job answers capture_sandbox_unavailable before any browser starts, and the panel
          reports that refusal as an answer rather than as a spinner that stopped. */}
      <ScreenshotCapturePanel
        milestoneId={milestone.id}
        milestoneTitle={milestone.title}
        archived={archived}
      />

      {/* SCREENSHOT-CURATION-001/002. What the owner chose about those pictures: which are selected
          evidence, which are rejected, which one is primary, in what sequence, and with what words.
          Selecting publishes the picture into the evidence panel above in the same server
          transaction, so a change that advances the bundle tells that panel to read again on its own
          terms - the same token the sync panel already uses, because neither feature has to know the
          other exists. */}
      <ScreenshotPicker
        milestoneId={milestone.id}
        milestoneTitle={milestone.title}
        archived={archived}
        onEvidenceChanged={() => setSettledCollections((value) => value + 1)}
      />

      {archived ? null : (
        <div className="milestone__actions">
          <button type="button" className="action" onClick={onEdit} data-testid="milestone-edit">
            Edit
          </button>
          {milestone.status === 'confirmed' ? (
            <button
              type="button"
              className="action"
              onClick={onDismiss}
              disabled={busy}
              data-testid="milestone-dismiss"
            >
              Dismiss
            </button>
          ) : (
            <button
              type="button"
              className="action action--primary"
              onClick={onConfirm}
              disabled={busy}
              data-testid="milestone-confirm"
            >
              Confirm
            </button>
          )}
        </div>
      )}
    </li>
  );
}

/** An ISO instant shown in the reader's own zone, with the UTC value available in the title. */
function formatInstant(iso: string): string {
  const date = new Date(iso);

  if (Number.isNaN(date.getTime()) || iso.length === 0) {
    return iso;
  }

  return date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}
