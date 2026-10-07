import { Dashboard } from './Dashboard';
import { MilestoneEditor } from './MilestoneEditor';
import { ProjectDetail } from './ProjectDetail';
import { ProjectEditor } from './ProjectEditor';
import { OperationsArea } from '../background-jobs/OperationsArea';
import { CredentialsArea } from '../credential-vault/CredentialsArea';
import { TeamPanel } from '../team-panels/TeamPanel';
import { TenancyPanel } from '../tenancy-views/TenancyPanel';
import { TimelinePage } from '../milestone-timeline/TimelinePage';
import { SettingsPage } from '../community-access/Settings/SettingsPage';
import { getMilestone, getProject, listProjects } from './api';
import { useMilestone, useProject, useProjects } from './useRecords';
import { segmentsOf } from './useRoute';
import type { Route } from './useRoute';
import type { OwnerSessionState } from '../community-access/useOwnerSession';

/**
 * The product area, rendered only for a signed-in owner.
 *
 * The session gate decides whether any of this exists: a browser with no session never gets past it,
 * because every route below is private and would answer 401 anyway. What this adds is the small map
 * from a path to a view.
 *
 * This is the only place that map lives. The route reader is owned by
 * the shell (App.tsx) and handed in, so the nav and the view always agree
 * about which path the owner is on. A second reader here would hold its own
 * copy of the current path, and a navigation from the nav would leave this
 * map reading a route the owner is no longer on - which is the kind of
 * disagreement that shows up as a page that navigated and did not.
 *
 * Paths:
 * - `/` the dashboard
 * - `/projects` every project
 * - `/projects/new` a new project
 * - `/projects/{id}` one project and its milestones
 * - `/projects/{id}/edit` editing that project
 * - `/projects/{id}/milestones/new` recording a milestone
 * - `/projects/{id}/milestones/{milestoneId}` editing a milestone
 * - `/timeline` the project timeline picker (PRODUCT-SHELL-UX-001)
 * - `/operations` the background jobs panel (BACKGROUND-JOBS-001/002)
 * - `/operations/{id}` one background job
 * - `/settings` session internals plus the credentials link (PRODUCT-SHELL-UX-001)
 * - `/settings/credentials` the credential settings surface (CREDENTIAL-VAULT-001/002)
 * - `/settings/team` the team invitation and member triggers (FEATURE-TRIGGER-PANELS-001)
 * - `/settings/tenancy` the hosted tenancy triggers (FEATURE-TRIGGER-PANELS-002)
 */
export function ProjectArea({ route, session }: { readonly route: Route; readonly session: OwnerSessionState }) {
  const { path, navigate } = route;
  const segments = segmentsOf(path);

  if (segments.length === 0) {
    return <DashboardRoute onNavigate={navigate} />;
  }

  if (segments[0] === 'timeline' && segments.length === 1) {
    return <TimelinePage onNavigate={navigate} />;
  }

  if (segments[0] === 'operations') {
    return <OperationsArea path={segments.join('/')} onNavigate={navigate} />;
  }

  if (segments[0] === 'settings') {
    return <SettingsArea path={segments.join('/')} onNavigate={navigate} session={session} />;
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
      <h1 className="view__heading">Nothing here</h1>
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
    <>
      <p className="view__crumbs" data-testid="settings-links">
        <button type="button" className="link" onClick={() => onNavigate('/settings/team')} data-testid="settings-team-link">
          Team
        </button>{' '}
        <button type="button" className="link" onClick={() => onNavigate('/settings/tenancy')} data-testid="settings-tenancy-link">
          Tenancy
        </button>
      </p>
      <Dashboard
        projects={projects.data}
        loading={projects.loading}
        onOpen={(projectId) => onNavigate(`/projects/${projectId}`)}
        onCreate={() => onNavigate('/projects/new')}
        onList={() => onNavigate('/projects')}
        onCredentials={() => onNavigate('/settings/credentials')}
      />
    </>
  );
}

/**
 * The settings area: session internals, credentials, team invitations and hosted tenancy.
 *
 * One map for the four settings surfaces, beside the existing route map. Each surface
 * keeps its own panel and its own routes; what this adds is the choice between them.
 * `/settings` itself is the session page (PRODUCT-SHELL-UX-001): session
 * internals plus the credentials link only — team and tenancy keep their own
 * routes and are not listed there, per owner decision.
 */
function SettingsArea({
  path,
  onNavigate,
  session,
}: {
  readonly path: string;
  readonly onNavigate: (to: string) => void;
  readonly session: OwnerSessionState;
}) {
  const segments = path.split('/').filter((segment) => segment.length > 0);

  if (segments.length === 1) {
    return <SettingsPage session={session} onNavigate={onNavigate} />;
  }

  if (segments[1] === 'team' && segments.length === 2) {
    return (
      <>
        <p className="view__crumbs">
          <button type="button" className="link" onClick={() => onNavigate('/')} data-testid="settings-back">
            ← Dashboard
          </button>
        </p>
        <TeamPanel />
      </>
    );
  }

  if (segments[1] === 'tenancy' && segments.length === 2) {
    return (
      <>
        <p className="view__crumbs">
          <button type="button" className="link" onClick={() => onNavigate('/')} data-testid="settings-back">
            ← Dashboard
          </button>
        </p>
        <TenancyPanel />
      </>
    );
  }

  return <CredentialsArea path={path} onNavigate={onNavigate} />;
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
      <h1 className="view__heading">Projects</h1>

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
        <h1 className="view__heading">Project not found</h1>
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
        <h1 className="view__heading">Milestone not found</h1>
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
