/**
 * The wire shapes of the static-blog export routes, read from the server's own contracts
 * rather than guessed.
 *
 * <b>What this module may hold.</b> An export job's status word, its redacted error code and
 * its completed counts - a job the owner asked for, read back to look at. What it never holds
 * is a credential, a provider response, or post text: the archive's bytes travel only through
 * the download answer, straight to a file.
 */

/** One accepted export: the job that will build it, as the request route answers it. */
export interface StaticBlogExportAccepted {
  readonly operationId: string;
  /** False when an identical request had already created this job. */
  readonly created: boolean;
}

/** One export job, as the status route answers it. */
export interface StaticBlogExportStatus {
  readonly operationId: string;
  /** `queued`, `running`, `succeeded`, `failed`, `cancelled` or `superseded`. */
  readonly status: string;
  /** The redacted job code, when the job ended without an archive. */
  readonly errorCode: string | null;
  /** The completed manifest digest, when the job succeeded. */
  readonly contentSha256: string | null;
  /** How many posts the archive depicts, when the job succeeded. */
  readonly postCount: number | null;
  /** How many distinct pictures the archive packs, when the job succeeded. */
  readonly imageCount: number | null;
}

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class StaticBlogApiError extends Error {
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
    this.name = 'StaticBlogApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** No live approval authorizes the selection. The remedy is an approval, not a retry. */
  get isApprovalRequired(): boolean {
    return this.code === 'approval_required';
  }

  /** The approval no longer describes the stored revision, evidence or pictures. */
  get isApprovalStale(): boolean {
    return this.code === 'approval_stale';
  }

  /** An approved picture's bytes are gone. Re-select or re-capture it and approve again. */
  get isMediaUnavailable(): boolean {
    return this.code === 'media_unavailable';
  }

  /** A binding moved while the job was queued: the archive was withheld, not rebuilt. */
  get isSuperseded(): boolean {
    return this.code === 'static_blog_superseded';
  }
}
