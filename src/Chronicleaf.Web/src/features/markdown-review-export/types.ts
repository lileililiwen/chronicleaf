/**
 * The wire shapes of the review routes, read from the server's own contracts rather than guessed.
 *
 * <b>What this module may hold.</b> An approval's binding - who decided, what text, and which
 * evidence, voice and pictures it was bound to - and the review state derived from it. What it
 * never holds is a credential, a provider response, or a stored consent: a review stays available
 * when every provider is down, and nothing here could reach one.
 *
 * <b>The stored `status` column is not here.</b> It is the drafts package's and always reads
 * `draft`. The review state this client renders - `draft`, `approved` or `invalidated` - is
 * derived from the approvals table by the server, and this client renders exactly what it says.
 */

/** One recorded approval, as the server answers it. */
export interface ContentApproval {
  readonly approvalId: string;
  readonly assetId: string;
  readonly contentRevision: number;
  /** Always `text`: the only kind this package approves. */
  readonly payloadKind: string;
  /** Lowercase SHA-256 over the exact approved bytes. */
  readonly bodyHash: string;
  readonly evidenceRevision: number;
  readonly approvedAt: string;
}

/** The review state of one asset. */
export interface ContentReview {
  readonly assetId: string;
  readonly currentRevision: number;
  /** `draft` (never approved), `approved` (a live approval describes the head), or `invalidated`. */
  readonly reviewStatus: 'draft' | 'approved' | 'invalidated';
  readonly approval: ContentApproval | null;
}

/** One edited revision, as the edit route answers it. */
export interface EditedRevision {
  readonly assetId: string;
  readonly revision: number;
  readonly bodyHash: string;
}

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class ReviewApiError extends Error {
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
    this.name = 'ReviewApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** No live approval authorizes an export. The remedy is an approval, not a retry. */
  get isApprovalRequired(): boolean {
    return this.code === 'approval_required';
  }

  /** The approval no longer describes the stored revision, evidence or pictures. */
  get isApprovalStale(): boolean {
    return this.code === 'approval_stale';
  }

  /** Deterministic findings remain on the revision. Correct the text; nothing acknowledges this. */
  get isApprovalBlocked(): boolean {
    return this.code === 'approval_blocked';
  }

  /** The head moved since it was read. Re-read and decide again. */
  get isRevisionConflict(): boolean {
    return this.code === 'revision_conflict';
  }

  /** An approved picture's bytes are gone. Re-select or re-capture it and approve again. */
  get isMediaUnavailable(): boolean {
    return this.code === 'media_unavailable';
  }

  /**
   * The stored revision the server named, when the refusal was a stale-revision conflict.
   * WEB-FEED-003: mirrors `MilestoneApiError.storedRevision` so the review panel's
   * `describeFailure` can render the one shared conflict sentence.
   */
  get storedRevision(): number | null {
    const named = this.fieldErrors['If-Match']?.[0];
    const matched = named ? /(\d+)/.exec(named) : null;

    return matched ? Number.parseInt(matched[1], 10) : null;
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}
