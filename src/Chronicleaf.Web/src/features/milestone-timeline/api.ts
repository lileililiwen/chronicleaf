import { TimelineApiError, type ApiFailure, type TimelinePage } from './types';

/**
 * The only client of the timeline route in this repository.
 *
 * Every request is `same-origin`, so the session cookie is attached by the browser and never by
 * this code. The timeline is a read, so no antiforgery value is carried: the access package checks
 * the session and the trusted origin before this route runs either way. Every failure arrives as
 * the delivered envelope and is raised as a {@link TimelineApiError} that keeps the code and the
 * status, so a 404 for a project outside this installation stays distinguishable from a 422 for a
 * cursor that belongs to another project.
 */

/** One page of a project's timeline, oldest first. */
export async function listTimeline(
  projectId: string,
  options: { readonly cursor?: string; readonly includeCandidates?: boolean },
  signal?: AbortSignal,
): Promise<TimelinePage> {
  const query = new URLSearchParams({ limit: '25' });

  if (options.cursor !== undefined) {
    query.set('cursor', options.cursor);
  }

  if (options.includeCandidates === true) {
    query.set('includeCandidates', 'true');
  }

  const response = await fetch(
    `/api/projects/${encodeURIComponent(projectId)}/timeline?${query}`,
    { method: 'GET', credentials: 'same-origin', headers: { Accept: 'application/json' }, signal },
  );

  if (!response.ok) {
    throw new TimelineApiError(await toFailure(response), response.status);
  }

  return (await response.json()) as TimelinePage;
}

async function toFailure(response: Response): Promise<ApiFailure> {
  try {
    const body = (await response.json()) as Partial<ApiFailure>;

    return {
      code: typeof body.code === 'string' ? body.code : 'internal_error',
      message: typeof body.message === 'string' ? body.message : 'The timeline could not be read.',
      traceId: typeof body.traceId === 'string' ? body.traceId : 'unknown',
      fieldErrors: body.fieldErrors,
    };
  } catch {
    return { code: 'internal_error', message: 'The timeline could not be read.', traceId: 'unknown' };
  }
}
