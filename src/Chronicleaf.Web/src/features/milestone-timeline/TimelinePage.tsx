import { useState } from 'react';

import { listProjects } from '../projects-and-milestones/api';
import { useProjects } from '../projects-and-milestones/useRecords';
import { ProjectTimeline } from './ProjectTimeline';

/**
 * PRODUCT-SHELL-UX-001. The top-level timeline view: a project picker over
 * the existing project list plus the existing `ProjectTimeline` for the
 * chosen project.
 *
 * Owner-decided (2026-10-07): reuse the `ProjectTimeline` component, per the
 * authored spec — not a fork. No new API is read here: the picker reads the
 * delivered project list and the timeline reads the delivered timeline
 * route, exactly as `ProjectDetail` already does.
 */
export function TimelinePage({ onNavigate }: { readonly onNavigate: (to: string) => void }) {
  const projects = useProjects(listProjects);
  const items = projects.data?.items ?? [];
  const [projectId, setProjectId] = useState<string | null>(null);
  const chosen = items.find((project) => project.id === projectId) ?? null;

  return (
    <section className="view" data-testid="timeline-page">
      <h1 className="view__heading" data-testid="timeline-page-heading">
        Timeline
      </h1>
      <p className="view__hint" data-testid="timeline-lede">
        Each project&apos;s confirmed history, oldest first. Choose a project to read its timeline.
      </p>

      {projects.failure ? (
        <p className="notice notice--error" role="alert" data-testid="timeline-error">
          {projects.failure.message}
        </p>
      ) : null}

      {projects.loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="timeline-loading">
          Loading projects…
        </p>
      ) : items.length === 0 ? (
        <div className="empty" data-testid="timeline-empty">
          <p className="empty__title">No projects yet.</p>
          <p className="empty__body">
            A timeline needs a project with confirmed milestones. Create a project first, then
            record a milestone against it whenever you have shipped something.
          </p>
          <button
            type="button"
            className="action action--primary"
            onClick={() => onNavigate('/projects/new')}
            data-testid="timeline-create-project"
          >
            Create your first project
          </button>
        </div>
      ) : (
        <>
          {/*
            WEB-A11Y-ROLES-002. The project choice is a set of pressed
            chips in a labelled group, the same vocabulary the
            milestone and operations filters use.
          */}
          <div className="view__actions" role="group" aria-label="Choose a project" data-testid="timeline-picker">
            {items.map((project) => (
              <button
                key={project.id}
                type="button"
                className={project.id === chosen?.id ? 'filter filter--active' : 'filter'}
                aria-pressed={project.id === chosen?.id}
                onClick={() => setProjectId(project.id)}
                data-testid="timeline-project"
              >
                {project.name}
              </button>
            ))}
          </div>

          {chosen === null ? (
            <p className="view__state" data-testid="timeline-unpicked">
              Choose a project above to read its timeline.
            </p>
          ) : (
            <ProjectTimeline
              projectId={chosen.id}
              onEditMilestone={(id, milestoneId) => onNavigate(`/projects/${id}/milestones/${milestoneId}`)}
            />
          )}
        </>
      )}
    </section>
  );
}
