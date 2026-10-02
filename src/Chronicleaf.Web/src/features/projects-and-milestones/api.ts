import type {
  ApiFailure,
  ImportedCandidate,
  Milestone,
  MilestoneDraft,
  MilestonePage,
  MilestoneStatus,
  Project,
  ProjectDraft,
  ProjectPage,
} from './types';

/**
 * The only client of the Project and Milestone routes in this repository.
 *
 * Every request is `same-origin`, so the session cookie is attached by the browser and never by this
 * code, and every mutation carries the antiforgery value the access package issues for this session.
 * The value is requested per mutation, used once and dropped, exactly as the delivered sign-out does
 * it, so a value from one action can never be replayed by the next.
 *
 * Every failure arrives as the delivered envelope and is raised as a {@link MilestoneApiError} that
 * keeps the code, the status and the field errors. That is deliberate: a 409 has to be distinguishable
 * from a 422, because the first means somebody else's edit won and the second means this edit was
 * never acceptable. A client that flattened both into "something went wrong" would let the UI hide
 * the one case the owner needs to know about.
 */

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class MilestoneApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'MilestoneApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** The stored revision the server named, when the refusal was a stale-revision conflict. */
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

/** One page of projects, following an opaque cursor when there is more. */
export async function listProjects(signal?: AbortSignal): Promise<ProjectPage> {
  return readJson<ProjectPage>('/api/projects', { method: 'GET', signal });
}

export async function createProject(draft: ProjectDraft, signal?: AbortSignal): Promise<Project> {
  return writeJson<Project>('/api/projects', {
    method: 'POST',
    body: {
      name: draft.name,
      description: draft.description,
      projectUrl: draft.projectUrl.length === 0 ? null : draft.projectUrl,
    },
    signal,
  });
}

export async function getProject(projectId: string, signal?: AbortSignal): Promise<Project> {
  return readJson<Project>(`/api/projects/${encodeURIComponent(projectId)}`, { method: 'GET', signal });
}

/**
 * Applies a partial project edit. The fields the editor owns are always sent, and `projectUrl` is
 * sent as an explicit `null` when it is empty, so clearing a field is a decision the server records
 * rather than an omission it guesses at.
 */
export async function updateProject(
  projectId: string,
  revision: number,
  draft: ProjectDraft,
  signal?: AbortSignal,
): Promise<Project> {
  return writeJson<Project>(`/api/projects/${encodeURIComponent(projectId)}`, {
    method: 'PATCH',
    revision,
    body: {
      name: draft.name,
      description: draft.description,
      projectUrl: draft.projectUrl.length === 0 ? null : draft.projectUrl,
    },
    signal,
  });
}

/** Archives a project. Nothing under it is deleted; the stamp makes it read-only. */
export async function archiveProject(projectId: string, revision: number, signal?: AbortSignal): Promise<Project> {
  return writeJson<Project>(`/api/projects/${encodeURIComponent(projectId)}`, {
    method: 'DELETE',
    revision,
    signal,
  });
}

export async function listMilestones(
  projectId: string,
  status: MilestoneStatus | 'all',
  signal?: AbortSignal,
): Promise<MilestonePage> {
  const query = new URLSearchParams({ limit: '100' });

  if (status !== 'all') {
    query.set('status', status);
  }

  return readJson<MilestonePage>(`/api/projects/${encodeURIComponent(projectId)}/milestones?${query}`, {
    method: 'GET',
    signal,
  });
}

export async function getMilestone(milestoneId: string, signal?: AbortSignal): Promise<Milestone> {
  return readJson<Milestone>(`/api/milestones/${encodeURIComponent(milestoneId)}`, { method: 'GET', signal });
}

/** Creates a milestone the owner typed. The server stores it confirmed: the save was the decision. */
export async function createMilestone(
  projectId: string,
  draft: MilestoneDraft,
  signal?: AbortSignal,
): Promise<Milestone> {
  return writeJson<Milestone>(`/api/projects/${encodeURIComponent(projectId)}/milestones`, {
    method: 'POST',
    body: toBody(draft),
    signal,
  });
}

/** Applies a partial milestone edit at the revision the editor loaded. */
export async function updateMilestone(
  milestoneId: string,
  revision: number,
  draft: MilestoneDraft,
  signal?: AbortSignal,
): Promise<Milestone> {
  return writeJson<Milestone>(`/api/milestones/${encodeURIComponent(milestoneId)}`, {
    method: 'PATCH',
    revision,
    body: toBody(draft),
    signal,
  });
}

/**
 * The owner's own confirmation. There is no other route that can set this status, and this UI offers
 * it only for a record the owner can act on.
 */
export async function confirmMilestone(
  milestoneId: string,
  revision: number,
  signal?: AbortSignal,
): Promise<Milestone> {
  return writeJson<Milestone>(`/api/milestones/${encodeURIComponent(milestoneId)}/confirm`, {
    method: 'POST',
    revision,
    signal,
  });
}

/** The owner's own dismissal. A dismissed record is kept, and only the owner can bring it back. */
export async function dismissMilestone(
  milestoneId: string,
  revision: number,
  signal?: AbortSignal,
): Promise<Milestone> {
  return writeJson<Milestone>(`/api/milestones/${encodeURIComponent(milestoneId)}/dismiss`, {
    method: 'POST',
    revision,
    signal,
  });
}

/**
 * Hands an upstream record to the intake route, which stores it as a candidate. This exists so a
 * later collection package has one place to deliver records, and so a candidate can be created and
 * confirmed in this build without any UI pretending to be a collector.
 */
export async function importCandidates(
  projectId: string,
  source: string,
  candidates: readonly { sourceKey: string; draft: MilestoneDraft }[],
  signal?: AbortSignal,
): Promise<readonly ImportedCandidate[]> {
  const body = await writeJson<{ items: readonly ImportedCandidate[] }>(
    `/api/projects/${encodeURIComponent(projectId)}/milestones/import`,
    {
      method: 'POST',
      body: {
        source,
        candidates: candidates.map((candidate) => ({ sourceKey: candidate.sourceKey, ...toBody(candidate.draft) })),
      },
      signal,
    },
  );

  return body.items;
}

function toBody(draft: MilestoneDraft) {
  return {
    title: draft.title,
    version: draft.version.length === 0 ? null : draft.version,
    occurredAt: new Date(draft.occurredAt).toISOString(),
    type: draft.type,
    notes: draft.notes,
    knownLimitations: draft.knownLimitations,
    nextSteps: draft.nextSteps,
  };
}

interface Request {
  readonly method: string;
  readonly body?: unknown;
  readonly revision?: number;
  readonly signal?: AbortSignal;
}

async function readJson<T>(path: string, request: Request): Promise<T> {
  return send<T>(path, request, false);
}

async function writeJson<T>(path: string, request: Request): Promise<T> {
  return send<T>(path, request, true);
}

async function send<T>(path: string, request: Request, mutating: boolean): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  if (mutating) {
    // A fresh antiforgery value per mutation. The access package rotates the stored value on every
    // issue, so a value from an earlier action is not reused here.
    const metadata = await readAntiforgeryValue();
    headers[metadata.headerName] = metadata.token;
  }

  if (request.revision !== undefined) {
    // The revision this edit is based on. Without it the server would not know which version the
    // owner was looking at, and refusing to guess is the whole point.
    headers['If-Match'] = String(request.revision);
  }

  const response = await fetch(path, {
    method: request.method,
    credentials: 'same-origin',
    headers,
    body: request.body === undefined ? undefined : JSON.stringify(request.body),
    signal: request.signal,
  });

  if (!response.ok) {
    throw new MilestoneApiError(await toFailure(response), response.status);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

async function readAntiforgeryValue(): Promise<{ headerName: string; token: string }> {
  const response = await fetch('/api/session/antiforgery', {
    method: 'GET',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new MilestoneApiError(await toFailure(response), response.status);
  }

  return (await response.json()) as { headerName: string; token: string };
}

async function toFailure(response: Response): Promise<ApiFailure> {
  try {
    const body = (await response.json()) as Partial<ApiFailure>;

    return {
      code: typeof body.code === 'string' ? body.code : 'internal_error',
      message:
        typeof body.message === 'string' ? body.message : 'The request could not be completed.',
      traceId: typeof body.traceId === 'string' ? body.traceId : '',
      fieldErrors: body.fieldErrors,
    };
  } catch {
    // A body that is not the envelope still leaves a refusal to report; the status is the answer.
    return {
      code: 'internal_error',
      message: 'The request could not be completed.',
      traceId: '',
    };
  }
}
