import { PROJECT_AREAS, areaPath, type ProjectAreaId } from './areas';

/**
 * The project sub-navigation (PROJECT-AREA-IA-001/003).
 *
 * It renders on every project area, so every area is one step from every
 * other area once the owner is on a project — no back-link, no return to
 * the dashboard needed. The active area carries `aria-current="page"`,
 * following the top nav's own convention.
 */
export function ProjectSubNav({
  projectId,
  current,
  onNavigate,
}: {
  readonly projectId: string;
  readonly current: ProjectAreaId | null;
  readonly onNavigate: (to: string) => void;
}) {
  return (
    <nav className="nav nav--sub" aria-label="Project areas" data-testid="project-subnav">
      {PROJECT_AREAS.map((area) => (
        <button
          key={area.id}
          type="button"
          className={area.id === current ? 'nav__item nav__item--active' : 'nav__item'}
          aria-current={area.id === current ? 'page' : undefined}
          onClick={() => onNavigate(areaPath(projectId, area.id))}
          data-testid={area.testid}
        >
          {area.label}
        </button>
      ))}
    </nav>
  );
}
