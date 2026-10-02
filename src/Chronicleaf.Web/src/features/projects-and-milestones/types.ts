/**
 * The wire shapes of the Project and Milestone routes, as the API publishes them.
 *
 * These are the only project facts this client believes in: every field here comes from a response
 * body, and nothing is filled in on the client side. There is no local copy of a project, no sample
 * history and no value the UI would show that the server did not send.
 */

export const MILESTONE_TYPES = [
  'feature',
  'release',
  'deployment',
  'ui',
  'bugfix',
  'performance',
  'payment',
  'feedback',
  'stars',
  'migration',
  'open-source',
  'first-customer',
  'custom',
] as const;

export type MilestoneType = (typeof MILESTONE_TYPES)[number];

export const MILESTONE_STATUSES = ['candidate', 'confirmed', 'dismissed'] as const;

export type MilestoneStatus = (typeof MILESTONE_STATUSES)[number];

/** A project as the server stores it. `archivedAt` is set once, and never removed. */
export interface Project {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly projectUrl: string | null;
  readonly repoConnectionId: string | null;
  readonly revision: number;
  readonly archivedAt: string | null;
}

/** A milestone as the server stores it, including the status only the owner can change. */
export interface Milestone {
  readonly id: string;
  readonly projectId: string;
  readonly title: string;
  readonly version: string | null;
  readonly occurredAt: string;
  readonly type: string;
  readonly source: string;
  readonly sourceKey: string | null;
  readonly status: MilestoneStatus;
  readonly revision: number;
  readonly notes: string;
  readonly knownLimitations: readonly string[];
  readonly nextSteps: readonly string[];
}

export interface ProjectPage {
  readonly items: readonly Project[];
  readonly nextCursor: string | null;
}

export interface MilestonePage {
  readonly items: readonly Milestone[];
  readonly nextCursor: string | null;
}

export interface ImportedCandidate {
  readonly sourceKey: string;
  readonly milestoneId: string;
  readonly created: boolean;
}

/** The fields the milestone editor owns. There is no status: it is not an editable field. */
export interface MilestoneDraft {
  readonly title: string;
  readonly version: string;
  readonly occurredAt: string;
  readonly type: MilestoneType;
  readonly notes: string;
  readonly knownLimitations: string[];
  readonly nextSteps: string[];
}

export interface ProjectDraft {
  readonly name: string;
  readonly description: string;
  readonly projectUrl: string;
}

/** The delivered error envelope, as far as this client needs it. */
export interface ApiFailure {
  readonly code: string;
  readonly message: string;
  readonly traceId: string;
  readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
}

/** A human label for a type, so the UI says words rather than slugs. */
export function milestoneTypeLabel(type: string): string {
  return type
    .split('-')
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(' ');
}
