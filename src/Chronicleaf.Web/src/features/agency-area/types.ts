/** Wire shapes of the delivered agency client and weekly-report routes. */

export interface ApiFailure {
  readonly code: string;
  readonly message: string;
  readonly traceId: string;
  readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
}

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class AgencyApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'AgencyApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }
}

/** One agency client, as the delivered client routes answer it. */
export interface AgencyClient {
  readonly id: string;
  readonly name: string;
  readonly notes: string;
  readonly revision: number;
  readonly contextRevision: number;
  readonly archivedAt: string | null;
}

export interface AgencyClientPage {
  readonly items: readonly AgencyClient[];
  readonly nextCursor: string | null;
}

/** One project grouped under a client, as the member list answers it. */
export interface AgencyClientProject {
  readonly id: string;
  readonly name: string;
  readonly revision: number;
  readonly archivedAt: string | null;
}

export interface AgencyClientProjectPage {
  readonly items: readonly AgencyClientProject[];
  readonly nextCursor: string | null;
}

/** One owned project with the client grouping the project read carries. */
export interface AgencyProject {
  readonly id: string;
  readonly name: string;
  readonly revision: number;
  readonly archivedAt: string | null;
  readonly clientId: string | null;
}

export interface AgencyProjectPage {
  readonly items: readonly AgencyProject[];
  readonly nextCursor: string | null;
}

/** One project entry of a report context: the revision the report pins. */
export interface AgencyReportEntry {
  readonly projectId: string;
  readonly projectName: string;
  readonly projectRevision: number;
}

/** The report context of one client at one membership generation. */
export interface AgencyReportContext {
  readonly clientId: string;
  readonly clientRevision: number;
  readonly contextRevision: number;
  readonly projects: readonly AgencyReportEntry[];
}

/** One weekly report row, as the delivered routes answer it. */
export interface WeeklyReport {
  readonly id: string;
  readonly clientId: string;
  readonly weekStart: string;
  readonly timeZone: string;
  readonly windowStartUtc: string;
  readonly windowEndUtc: string;
  readonly revision: number;
  readonly projectIds: readonly string[];
  readonly attributionHash: string;
  readonly sourceRefs: string;
  readonly bodyMarkdown: string;
  readonly bodyHash: string;
  readonly approved: boolean;
}

/** One recorded approval, as the approve route answers it. */
export interface WeeklyReportApproval {
  readonly reportId: string;
  readonly revision: number;
  readonly bodyHash: string;
  readonly approvedAt: string;
}
