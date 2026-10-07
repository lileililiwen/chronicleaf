/**
 * The six project areas (PROJECT-AREA-IA-001).
 *
 * One project, six addresses. The bare `/projects/{id}` URL renders
 * Overview, so every URL assertion written before the split still holds.
 * An unknown area segment is not an area: the route map renders the
 * delivered "Nothing here" view for those.
 */
export const PROJECT_AREAS = [
  { id: 'overview', label: 'Overview', suffix: '', testid: 'project-area-overview' },
  { id: 'evidence', label: 'Evidence', suffix: 'evidence', testid: 'project-area-evidence' },
  { id: 'content', label: 'Content', suffix: 'content', testid: 'project-area-content' },
  { id: 'publish', label: 'Publish', suffix: 'publish', testid: 'project-area-publish' },
  { id: 'evolution', label: 'Evolution', suffix: 'evolution', testid: 'project-area-evolution' },
  { id: 'automations', label: 'Automations', suffix: 'automations', testid: 'project-area-automations' },
] as const;

export type ProjectAreaId = (typeof PROJECT_AREAS)[number]['id'];

/** The area for a sub-path segment, or null when the segment names no area. */
export function areaOfSegment(segment: string | undefined): ProjectAreaId | null {
  if (segment === undefined) {
    return 'overview';
  }

  const found = PROJECT_AREAS.find((area) => area.suffix === segment);

  return found?.id ?? null;
}

/** The client-side path of an area for a project. */
export function areaPath(projectId: string, area: ProjectAreaId): string {
  const suffix = PROJECT_AREAS.find((entry) => entry.id === area)?.suffix ?? '';

  return suffix === '' ? `/projects/${projectId}` : `/projects/${projectId}/${suffix}`;
}
