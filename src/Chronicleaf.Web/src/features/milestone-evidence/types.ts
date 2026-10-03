/**
 * The wire shapes of the evidence routes, as the API publishes them.
 *
 * These are the only evidence facts this client believes in: every field here comes from a response
 * body, and nothing is filled in on the client side. There is no sample bundle, no placeholder item
 * and no availability this UI would show that the server did not send - which is what lets an empty
 * milestone say it is empty and an unavailable source look unavailable.
 */

/** The three honest answers about how completely a source was collected. */
export const EVIDENCE_AVAILABILITIES = ['complete', 'partial', 'unavailable'] as const;

export type EvidenceAvailability = (typeof EVIDENCE_AVAILABILITIES)[number];

/** The closed vocabulary of evidence kinds, including the two the owner may write by hand. */
export const EVIDENCE_KINDS = [
  'project-metadata',
  'commit',
  'diff',
  'release',
  'issue',
  'pr',
  'screenshot',
  'manual',
  'limitations',
  'next-steps',
] as const;

export type EvidenceKind = (typeof EVIDENCE_KINDS)[number];

/** One collected fact, exactly as the server recorded it. */
export interface EvidenceItem {
  readonly id: string;
  readonly kind: string;
  readonly sourceRef: string;
  readonly sourceRevision: string;
  readonly capturedAt: string;
  readonly payloadSha256: string;
  readonly text: string;
  readonly mediaId: string | null;
  readonly redactions: readonly string[];
  readonly availability: EvidenceAvailability;
  readonly supersedesId: string | null;
}

/** One immutable snapshot of a milestone's evidence. */
export interface EvidenceBundle {
  readonly id: string;
  readonly milestoneId: string;
  readonly revision: number;
  readonly createdAt: string;
  readonly items: readonly EvidenceItem[];
}

/**
 * A page of bundles, plus the revision a mutation has to present.
 *
 * `revision` is 0 for a milestone with no evidence at all. The client passes it straight back, so an
 * empty milestone can still be appended to without the UI inventing a revision of its own.
 */
export interface EvidencePage {
  readonly milestoneId: string;
  readonly revision: number;
  readonly items: readonly EvidenceBundle[];
  readonly nextCursor: string | null;
}

/** What the owner may write by hand. There is no kind, availability or source field to choose. */
export interface ManualEvidenceDraft {
  readonly notes: string;
  readonly knownLimitations: string[];
  readonly nextSteps: string[];
}

/** The delivered error envelope, as far as this client needs it. */
export interface ApiFailure {
  readonly code: string;
  readonly message: string;
  readonly traceId: string;
  readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
}

/**
 * A label for an availability, phrased as what a reader has to do about it.
 *
 * The words matter more than the decoration: an unavailable source is a gap someone has to go and
 * look at, and a partial one is a gap somebody already named. Rendering both as a neutral badge would
 * quietly turn a known hole into a fact the story simply does not mention.
 */
export function availabilityLabel(availability: EvidenceAvailability): string {
  switch (availability) {
    case 'complete':
      return 'Collected';
    case 'partial':
      return 'Partial';
    case 'unavailable':
      return 'Unavailable';
    default:
      return availability;
  }
}

/** A human label for an evidence kind, so the UI says words rather than slugs. */
export function evidenceKindLabel(kind: string): string {
  return kind
    .split('-')
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(' ');
}
