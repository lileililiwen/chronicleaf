import { Dashboard } from './Dashboard';
import { MilestoneEditor } from './MilestoneEditor';
import { ProjectDetail } from './ProjectDetail';
import { ProjectEditor } from './ProjectEditor';
import { getMilestone, getProject, listProjects } from './api';
import { useMilestone, useProject, useProjects } from './useRecords';
import { segmentsOf, useRoute } from './useRoute';

/**
 * The product area, rendered only for a signed-in owner.
 *
 * The session gate decides whether any of this exists: a browser with no session never gets past it,
 * because every route below is private and would answer 401 anyway. What this adds is the small map
 * from a path to a view.
 *
 * Paths:
 * - `/` the dashboard
 * - `/projects` every project
 * - `/projects/new` a new project
 * - `/projects/{id}` one project and its milestones
 * - `/projects/{id}/edit` editing that project
 * - `/projects/{id}/milestones/new` recording a milestone
 * - `/projects/{id}/milestones/{milestoneId}` editing a milestone
 */
export function ProjectArea() {
  const { path, navigate } = useRoute();
  const segments = segmentsOf(path);

  if (segments.length === 0) {
    return <DashboardRoute onNavigate={navigate} />;
  }

  if (segments[0] === 'projects') {
    if (segments.length === 1) {
      return <ProjectsRoute onNavigate={navigate} />;
    }

    if (segments[1] === 'new' && segments.length === 2) {
      return (
        <ProjectEditor
          project={null}
          onSaved={(projectId) => navigate(`/projects/${projectId}`)}
          onCancel={() => navigate('/projects')}
        />
      );
    }

    const projectId = segments[1];

    if (segments.length === 2) {
      return (
        <ProjectDetail
          projectId={projectId}
          onEditProject={(id) => navigate(`/projects/${id}/edit`)}
          onNewMilestone={(id) => navigate(`/projects/${id}/milestones/new`)}
          onEditMilestone={(id, milestoneId) => navigate(`/projects/${id}/milestones/${milestoneId}`)}
          onBack={() => navigate('/projects')}
        />
      );
    }

    if (segments[2] === 'edit' && segments.length === 3) {
      return <ProjectEditRoute projectId={projectId} onNavigate={navigate} />;
    }

    if (segments[2] === 'milestones' && segments[3] === 'new' && segments.length === 4) {
      return (
        <MilestoneEditor
          projectId={projectId}
          milestone={null}
          onSaved={() => navigate(`/projects/${projectId}`)}
          onCancel={() => navigate(`/projects/${projectId}`)}
        />
      );
    }

    if (segments[2] === 'milestones' && segments.length === 4) {
      return (
        <MilestoneEditRoute
          projectId={projectId}
          milestoneId={segments[3]}
          onNavigate={navigate}
        />
      );
    }
  }

  return (
    <section className="view" data-testid="route-not-found">
      <h2 className="view__heading">Nothing here</h2>
      <p className="view__state">
        This address is not part of Chronicleaf. Nothing was changed.
      </p>
      <button type="button" className="action" onClick={() => navigate('/')} data-testid="route-home">
        Go to the dashboard
      </button>
    </section>
  );
}

function DashboardRoute({ onNavigate }: { readonly onNavigate: (to: string) => void }) {
  const projects = useProjects(listProjects);

  return (
    <Dashboard
      projects={projects.data}
      loading={projects.loading}
      onOpen={(projectId) => onNavigate(`/projects/${projectId}`)}
      onCreate={() => onNavigate('/projects/new')}
      onList={() => onNavigate('/projects')}
    />
  );
}

function ProjectsRoute({ onNavigate }: { readonly onNavigate: (to: string) => void }) {
  const projects = useProjects(listProjects);
  const items = projects.data?.items ?? [];

  return (
    <section className="view" data-testid="projects">
      <p className="view__crumbs">
        <button type="button" className="link" onClick={() => onNavigate('/')} data-testid="projects-back">
          ← Dashboard
        </button>
      </p>
      <h2 className="view__heading">Projects</h2>

      {projects.failure ? (
        <p className="notice notice--error" role="alert" data-testid="projects-error">
          {projects.failure.message}
        </p>
      ) : null}

      {projects.loading ? (
        <p className="view__state" data-testid="projects-loading">
          Loading projects…
        </p>
      ) : items.length === 0 ? (
        <div className="empty" data-testid="projects-empty">
          <p className="empty__title">No projects yet.</p>
          <button
            type="button"
            className="action action--primary"
            onClick={() => onNavigate('/projects/new')}
            data-testid="projects-create"
          >
            Create your first project
          </button>
        </div>
      ) : (
        <ul className="cards" data-testid="projects-list">
          {items.map((project) => (
            <li key={project.id}>
              <button
                type="button"
                className="card"
                onClick={() => onNavigate(`/projects/${project.id}`)}
                data-testid="project-card"
              >
                <span className="card__name">{project.name}</span>
                <span className="card__body">{project.description || 'No description yet.'}</span>
                {project.archivedAt ? <span className="badge badge--muted">Archived</span> : null}
              </button>
            </li>
          ))}
        </ul>
      )}

      {items.length > 0 ? (
        <p className="view__actions">
          <button
            type="button"
            className="action"
            onClick={() => onNavigate('/projects/new')}
            data-testid="projects-new"
          >
            New project
          </button>
        </p>
      ) : null}
    </section>
  );
}

function ProjectEditRoute({
  projectId,
  onNavigate,
}: {
  readonly projectId: string;
  readonly onNavigate: (to: string) => void;
}) {
  const project = useProject(getProject, projectId);

  if (project.failure?.status === 404) {
    return (
      <section className="view" data-testid="project-missing">
        <h2 className="view__heading">Project not found</h2>
        <button type="button" className="action" onClick={() => onNavigate('/projects')} data-testid="project-missing-back">
          Back to projects
        </button>
      </section>
    );
  }

  if (project.loading || project.data === null) {
    return (
      <p className="view__state" data-testid="project-loading">
        Loading this project…
      </p>
    );
  }

  return (
    <ProjectEditor
      project={{
        id: project.data.id,
        revision: project.data.revision,
        name: project.data.name,
        description: project.data.description,
        projectUrl: project.data.projectUrl,
      }}
      onSaved={(savedId) => onNavigate(`/projects/${savedId}`)}
      onCancel={() => onNavigate(`/projects/${project.data?.id ?? projectId}`)}
    />
  );
}

function MilestoneEditRoute({
  projectId,
  milestoneId,
  onNavigate,
}: {
  readonly projectId: string;
  readonly milestoneId: string;
  readonly onNavigate: (to: string) => void;
}) {
  const milestone = useMilestone(getMilestone, milestoneId);

  if (milestone.failure?.status === 404) {
    return (
      <section className="view" data-testid="milestone-missing">
        <h2 className="view__heading">Milestone not found</h2>
        <button
          type="button"
          className="action"
          onClick={() => onNavigate(`/projects/${projectId}`)}
          data-testid="milestone-missing-back"
        >
          Back to the project
        </button>
      </section>
    );
  }

  if (milestone.loading || milestone.data === null) {
    return (
      <p className="view__state" data-testid="milestone-loading">
        Loading this milestone…
      </p>
    );
  }

  return (
    <MilestoneEditor
      projectId={projectId}
      milestone={milestone.data}
      onSaved={() => onNavigate(`/projects/${projectId}`)}
      onCancel={() => onNavigate(`/projects/${projectId}`)}
    />
  );
}
