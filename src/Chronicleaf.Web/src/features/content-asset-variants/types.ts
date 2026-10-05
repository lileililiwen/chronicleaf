/**
 * The wire shapes of the variant routes, read from the server's own contracts rather than guessed.
 *
 * <b>What this module may hold.</b> A variant's identity, its format, the revision it is at, and
 * the digests and revisions it is bound to. What it never holds is a credential, a provider
 * response, or a stored consent: deriving calls no provider, and nothing here could reach one.
 *
 * <b>Approval is not here.</b> Whether a variant may be exported is the review package's derived
 * state, read through its own client. This client derives and reads; the review client approves.
 */

/** Every derived format, in the order the picker lists them. */
export const VARIANT_TYPES = [
  'changelog',
  'technical-blog',
  'social-short',
  'github-release',
  'readme-update',
  'portfolio',
  'weekly-progress',
] as const;

/** One of the formats this product derives. */
export type VariantType = (typeof VARIANT_TYPES)[number];

/** One variant asset in a milestone's list, as the server answers it. */
export interface VariantSummary {
  readonly assetId: string;
  readonly type: string;
  readonly currentRevision: number;
  readonly bodyHash: string;
  readonly evidenceRevision: number;
  readonly voiceRevision: number;
  readonly updatedAt: string;
}

/** One derived revision, as the derive route answers it. */
export interface DerivedVariant {
  readonly assetId: string;
  readonly type: string;
  readonly revision: number;
  readonly bodyHash: string;
  readonly sourceAssetId: string;
  readonly sourceRevision: number;
  readonly evidenceRevision: number;
  readonly voiceRevision: number;
}

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class VariantApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(
    failure: {
      readonly code: string;
      readonly message: string;
      readonly traceId: string;
      readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
    },
    status: number,
  ) {
    super(failure.message);
    this.name = 'VariantApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** The source story was never approved. The remedy is an approval, not a retry. */
  get isApprovalRequired(): boolean {
    return this.code === 'approval_required';
  }

  /** The source approval no longer describes the stored state. Re-read and derive again. */
  get isApprovalStale(): boolean {
    return this.code === 'approval_stale';
  }

  /** A presented revision is not the stored head. Re-read and decide again. */
  get isRevisionConflict(): boolean {
    return this.code === 'revision_conflict';
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}
