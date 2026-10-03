import type {
  ApiFailure,
  VoiceCatalogue,
  VoiceCheck,
  VoiceDraft,
  VoiceDraftPage,
  VoiceOverrides,
  VoiceProfile,
} from './types';

/**
 * The only client of the voice routes in this repository.
 *
 * It follows the same rules as the delivered credential and evidence clients: requests are
 * `same-origin`, so the browser attaches the session cookie and this code never sees it, and every
 * mutation carries a fresh antiforgery value that is used once and dropped.
 *
 * What is specific here is the revision discipline. Every write is conditional on the revision the
 * read published, and this module never invents one: a caller that has not read the resource has no
 * revision and must not save. That is why `readVoiceProfile` returns the revision it saw rather than
 * a placeholder, and why the save functions take it as an argument instead of reading it from a
 * module-level cache that could be stale by the time the write lands.
 */
export class VoiceApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'VoiceApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** True when the profile moved under the owner and the current one was preserved. */
  get isRevisionConflict(): boolean {
    return this.status === 409;
  }

  /** True when the request did not present the revision it needed. */
  get isMissingRevision(): boolean {
    return this.status === 428;
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}

/** The fields this client validates locally, so an obvious mistake never costs a round trip. */
export class VoiceInputError extends Error {
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(field: string, message: string) {
    super(message);
    this.name = 'VoiceInputError';
    this.fieldErrors = { [field]: [message] };
  }
}

/** Reads the preset catalogue, the vocabularies and the bounds an editor needs. */
export async function readCatalogue(signal?: AbortSignal): Promise<VoiceCatalogue> {
  return send<VoiceCatalogue>('/api/voice-presets', { method: 'GET', signal }, false);
}

/**
 * Reads a project's stored voice, and the revision its next write must present.
 *
 * The revision is the one the read published. A project that has never had a voice reports 0, which
 * is the token a first save presents - it is a real answer from the server rather than a value this
 * client guessed, which is what stops a first save from being refused for presenting a placeholder.
 */
export async function readVoiceProfile(
  projectId: string,
  signal?: AbortSignal,
): Promise<{ readonly profile: VoiceProfile; readonly revision: number }> {
  const answer = await sendWithEntityTag<VoiceProfile>(
    `/api/projects/${encodeURIComponent(projectId)}/voice-profile`,
    { method: 'GET', signal },
    false,
  );

  return { profile: answer.body, revision: answer.entityTagRevision ?? answer.body.revision };
}

/** Stores a project's voice, replacing the version at `revision`. */
export async function saveVoiceProfile(
  projectId: string,
  presetId: string,
  overrides: VoiceOverrides,
  revision: number,
  signal?: AbortSignal,
): Promise<{ readonly profile: VoiceProfile; readonly revision: number }> {
  const answer = await sendWithEntityTag<VoiceProfile>(
    `/api/projects/${encodeURIComponent(projectId)}/voice-profile`,
    {
      method: 'PUT',
      revision,
      body: { presetId, overrides },
      signal,
    },
    true,
  );

  return { profile: answer.body, revision: answer.entityTagRevision ?? answer.body.revision };
}

/**
 * Judges text against the project's current voice, and stores nothing.
 *
 * This is the stateless check a compose surface runs before anything is saved, which is why it needs
 * no revision: there is no record it could be stale against.
 */
export async function checkVoice(
  projectId: string,
  title: string,
  body: string,
  signal?: AbortSignal,
): Promise<VoiceCheck> {
  return send<VoiceCheck>(
    `/api/projects/${encodeURIComponent(projectId)}/voice-check`,
    { method: 'POST', body: { title, body }, signal },
    true,
  );
}

/** Writes a new draft, which copies the project's effective voice into the draft's own snapshot. */
export async function createVoiceDraft(
  projectId: string,
  title: string,
  body: string,
  signal?: AbortSignal,
): Promise<VoiceDraft> {
  return send<VoiceDraft>(
    `/api/projects/${encodeURIComponent(projectId)}/voice-drafts`,
    { method: 'POST', body: { title, body }, signal },
    true,
  );
}

/** Reads one draft with the voice it was created under and the findings for its stored text. */
export async function readVoiceDraft(draftId: string, signal?: AbortSignal): Promise<VoiceDraft> {
  return send<VoiceDraft>(`/api/voice-drafts/${encodeURIComponent(draftId)}`, { method: 'GET', signal }, false);
}

/** Replaces a draft's own text. The stored text is sent and returned byte for byte. */
export async function editVoiceDraft(
  draftId: string,
  title: string,
  body: string,
  revision: number,
  signal?: AbortSignal,
): Promise<VoiceDraft> {
  return send<VoiceDraft>(
    `/api/voice-drafts/${encodeURIComponent(draftId)}`,
    { method: 'PUT', revision, body: { title, body }, signal },
    true,
  );
}

/**
 * Approves a draft. The server refuses with 422 while any finding remains and writes nothing in
 * either branch, so this call cannot change the owner's wording.
 */
export async function approveVoiceDraft(
  draftId: string,
  revision: number,
  signal?: AbortSignal,
): Promise<VoiceDraft> {
  return send<VoiceDraft>(
    `/api/voice-drafts/${encodeURIComponent(draftId)}/approve`,
    { method: 'POST', revision, body: {}, signal },
    true,
  );
}

/** Lists a project's drafts, a bounded page at a time. */
export async function listVoiceDrafts(
  projectId: string,
  limit?: number,
  cursor?: string,
  signal?: AbortSignal,
): Promise<VoiceDraftPage> {
  const parameters = new URLSearchParams();

  if (limit !== undefined) {
    parameters.set('limit', String(limit));
  }

  if (cursor !== undefined && cursor.length > 0) {
    parameters.set('cursor', cursor);
  }

  const query = parameters.toString();

  return send<VoiceDraftPage>(
    `/api/projects/${encodeURIComponent(projectId)}/voice-drafts${query.length > 0 ? `?${query}` : ''}`,
    { method: 'GET', signal },
    false,
  );
}

/** The revision an entity-tag carries, or null when the server published none. */
export function parseEntityTag(entityTag: string | null): number | null {
  if (entityTag === null) {
    return null;
  }

  const parsed = Number(entityTag.trim().replace(/^W\//, '').replace(/"/g, ''));

  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
}

interface Request {
  readonly method: string;
  readonly body?: unknown;
  readonly revision?: number;
  readonly signal?: AbortSignal;
}

interface Answer<T> {
  readonly body: T;
  readonly entityTag: string | null;
  /** The revision the server published as its entity-tag, when it published one. */
  readonly entityTagRevision: number | null;
}

async function send<T>(path: string, request: Request, mutating: boolean): Promise<T> {
  const answer = await sendWithEntityTag<T>(path, request, mutating);

  return answer.body;
}

async function sendWithEntityTag<T>(
  path: string,
  request: Request,
  mutating: boolean,
): Promise<Answer<T>> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (request.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  if (mutating) {
    const metadata = await readAntiforgeryValue();
    headers[metadata.headerName] = metadata.token;
  }

  if (request.revision !== undefined) {
    // The revision this write replaces. Without it the server refuses rather than guessing which
    // version the owner was looking at.
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
    throw new VoiceApiError(await toFailure(response), response.status);
  }

  const entityTag = response.headers.get('ETag');

  return {
    body: (await response.json()) as T,
    entityTag,
    entityTagRevision: parseEntityTag(entityTag),
  };
}

async function readAntiforgeryValue(): Promise<{ headerName: string; token: string }> {
  const response = await fetch('/api/session/antiforgery', {
    method: 'GET',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new VoiceApiError(await toFailure(response), response.status);
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
