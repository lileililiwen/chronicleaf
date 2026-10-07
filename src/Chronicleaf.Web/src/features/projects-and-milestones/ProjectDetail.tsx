import { useState } from 'react';

import { MilestoneApiError, archiveProject, getProject } from './api';
import type { Project } from './types';
import { useProject } from './useRecords';
import { ProjectSubNav } from '../project-areas/ProjectSubNav';
import { ProjectAutomations } from '../project-areas/ProjectAutomations';
import { ProjectContent } from '../project-areas/ProjectContent';
import { ProjectEvidence } from '../project-areas/ProjectEvidence';
import { ProjectEvolution } from '../project-areas/ProjectEvolution';
import { ProjectOverview } from '../project-areas/ProjectOverview';
import { ProjectPublish } from '../project-areas/ProjectPublish';
import type { ProjectAreaId } from '../project-areas/areas';

/**
 * One project, six addresses (PROJECT-AREA-IA-001).
 *
 * The page used to be one endless scroll of 20 panels. It is now a
 * header — the project's own facts and the owner's own actions — plus
 * one sub-navigation and one area. The header renders identically on
 * every area, so header-level suites keep passing wherever they
 * navigate; each area renders exactly the panels the move map assigns
 * it, and the Automations area renders only new components over
 * delivered routes.
 */
export function ProjectDetail({
  projectId,
  area,
  onEditProject,
  onNewMilestone,
  onEditMilestone,
  onBack,
  onNavigate,
}: {
  readonly projectId: string;
  readonly area: ProjectAreaId;
  readonly onEditProject: (projectId: string) => void;
  readonly onNewMilestone: (projectId: string) => void;
  readonly onEditMilestone: (projectId: string, milestoneId: string) => void;
  readonly onBack: () => void;
  readonly onNavigate: (to: string) => void;
}) {
  const project = useProject(getProject, projectId);
  const [archiveFailure, setArchiveFailure] = useState<MilestoneApiError | null>(null);
  const [archiveNotice, setArchiveNotice] = useState<string | null>(null);
  const [archiving, setArchiving] = useState(false);

  if (project.failure?.status === 404) {
    return (
      <section className="view" data-testid="project-missing">
        <h1 className="view__heading">Project not found</h1>
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

  const archive = async () => {
    if (record === null) {
      return;
    }

    setArchiving(true);
    setArchiveFailure(null);
    setArchiveNotice(null);

    try {
      await archiveProject(record.id, record.revision);
      project.reload();
      setArchiveNotice('The project is archived. Its milestones are kept and stay readable.');
    } catch (thrown) {
      setArchiveFailure(thrown instanceof MilestoneApiError ? thrown : null);
    } finally {
      setArchiving(false);
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
          <h1 className="view__heading" data-testid="project-name">
            {record.name}
          </h1>
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
                disabled={archiving}
                data-testid="project-archive"
              >
                {archiving ? 'Archiving…' : 'Archive project'}
              </button>
            </div>
          ) : null}

          {archiveNotice ? (
            <p className="notice notice--ok" role="status" data-testid="project-notice">
              {archiveNotice}
            </p>
          ) : null}

          {archiveFailure ? (
            <p className="notice notice--error" role="alert" data-testid="project-action-error">
              {archiveFailure.message}
            </p>
          ) : null}

          <ProjectSubNav projectId={record.id} current={area} onNavigate={onNavigate} />
        </header>
      )}

      {record === null ? null : area === 'overview' ? (
        <ProjectOverview
          projectId={record.id}
          archived={archived}
          onEditMilestone={onEditMilestone}
          onNavigate={onNavigate}
        />
      ) : area === 'evidence' ? (
        <ProjectEvidence
          projectId={record.id}
          archived={archived}
          onEditMilestone={onEditMilestone}
          onNavigate={onNavigate}
        />
      ) : area === 'content' ? (
        <ProjectContent
          projectId={record.id}
          archived={archived}
          onEditMilestone={onEditMilestone}
          onNavigate={onNavigate}
        />
      ) : area === 'publish' ? (
        <ProjectPublish projectId={record.id} onNavigate={onNavigate} />
      ) : area === 'evolution' ? (
        <ProjectEvolution
          projectId={record.id}
          archived={archived}
          onEditMilestone={onEditMilestone}
          onNavigate={onNavigate}
        />
      ) : (
        <ProjectAutomations projectId={record.id} archived={archived} onNavigate={onNavigate} />
      )}
    </section>
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
