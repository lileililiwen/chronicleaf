/** The export formats this feature downloads, one per delivered backend. */
export type PackFormat = 'linkedin' | 'medium' | 'xiaohongshu';

/** The asset types the milestone asset list carries for derived packs. */
export const LINKEDIN_PACK_TYPE = 'linkedin-copy';
export const XIAOHONGSHU_PACK_TYPE = 'xiaohongshu-pack';

/** One adapted LinkedIn copy, as the adapt route answers it. */
export interface AdaptedLinkedInCopy {
  readonly assetId: string;
  readonly revision: number;
  readonly bodyHash: string;
  readonly charCount: number;
  readonly lengthWarning: boolean;
  readonly sourceAssetId: string;
  readonly sourceRevision: number;
  readonly evidenceRevision: number;
  readonly voiceRevision: number;
  readonly selectedMediaIds: readonly string[];
  readonly hasCallToAction: boolean;
}

/** One adapted Xiaohongshu pack, as the adapt route answers it. */
export interface AdaptedXiaohongshuPack {
  readonly assetId: string;
  readonly revision: number;
  readonly bodyHash: string;
  readonly titleCandidates: readonly string[];
  readonly selectedTitle: string;
  readonly charCount: number;
  readonly lengthWarning: boolean;
  readonly sourceAssetId: string;
  readonly sourceRevision: number;
  readonly evidenceRevision: number;
  readonly voiceRevision: number;
  readonly imageOrder: readonly string[];
  readonly hasCallToAction: boolean;
}

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class PackApiError extends Error {
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
    this.name = 'PackApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** Native publication is not in contract. The refusal is the answer, not a send. */
  get isPublisherUnsupported(): boolean {
    return this.code === 'publisher_unsupported';
  }

  /** The source story was never approved. The remedy is an approval, not a retry. */
  get isApprovalRequired(): boolean {
    return this.code === 'approval_required';
  }

  /** The approval no longer describes the stored state. Re-read and approve again. */
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
