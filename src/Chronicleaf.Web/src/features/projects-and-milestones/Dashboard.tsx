import type { Project, ProjectPage } from './types';

/**
 * What the owner sees first, stated from what is actually stored.
 *
 * An installation with no projects gets an empty state that says exactly that and offers the one
 * action that changes it. There is no example project, no sample milestone and no screenshot of a
 * history that does not exist: a first-run installation that showed a demo would be lying about what
 * the owner has done.
 */
export function Dashboard({
  projects,
  loading,
  onOpen,
  onCreate,
  onList,
}: {
  readonly projects: ProjectPage | null;
  readonly loading: boolean;
  readonly onOpen: (projectId: string) => void;
  readonly onCreate: () => void;
  readonly onList: () => void;
}) {
  const items = projects?.items ?? [];

  return (
    <section className="view" data-testid="dashboard">
      <h2 className="view__heading">Dashboard</h2>

      {loading ? (
        <p className="view__state" data-testid="dashboard-loading">
          Loading your projects…
        </p>
      ) : items.length === 0 ? (
        <div className="empty" data-testid="dashboard-empty">
          <p className="empty__title">No projects yet.</p>
          <p className="empty__body">
            Chronicleaf keeps the milestones you record here. Nothing has been recorded for this
            installation, so there is no history to show yet. Create a project to start one, or
            record a milestone against it whenever you have shipped something.
          </p>
          <button type="button" className="action action--primary" onClick={onCreate} data-testid="dashboard-create-project">
            Create your first project
          </button>
        </div>
      ) : (
        <>
          <p className="view__state" data-testid="dashboard-summary">
            {summaryFor(items)}
          </p>
          <ul className="cards" data-testid="dashboard-projects">
            {items.map((project) => (
              <li key={project.id}>
                <button type="button" className="card" onClick={() => onOpen(project.id)} data-testid="dashboard-project">
                  <span className="card__name">{project.name}</span>
                  <span className="card__body">{project.description || 'No description yet.'}</span>
                  {project.archivedAt ? <span className="badge badge--muted">Archived</span> : null}
                </button>
              </li>
            ))}
          </ul>
          <p className="view__actions">
            <button type="button" className="action" onClick={onList} data-testid="dashboard-all-projects">
              All projects
            </button>
            <button type="button" className="action" onClick={onCreate} data-testid="dashboard-new-project">
              New project
            </button>
          </p>
        </>
      )}
    </section>
  );
}

function summaryFor(items: readonly Project[]): string {
  const live = items.filter((project) => project.archivedAt === null);
  const archived = items.length - live.length;

  return [
    `${live.length} live ${live.length === 1 ? 'project' : 'projects'}`,
    `${archived} archived`,
  ].join(' · ');
}
