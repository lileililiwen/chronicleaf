import { useCallback, useEffect, useState } from 'react';

import { MilestoneApiError } from './api';
import type { Milestone, MilestonePage, MilestoneStatus, Project, ProjectPage } from './types';

/**
 * Loading state for the owner's own records.
 *
 * The state is exactly the three things a view needs to be honest about: whether the answer is still
 * on its way, what the server actually returned, and what it refused. There is no placeholder list,
 * no optimistic insert and no "would be" row - `data` holds what the server sent, and a view with no
 * data yet says so instead of drawing something.
 */

export interface Loaded<T> {
  readonly data: T | null;
  readonly loading: boolean;
  readonly failure: MilestoneApiError | null;
  /** Fetches again, leaving the current data visible while it does. */
  readonly reload: () => void;
}

/**
 * Runs one read whenever its inputs change, and refuses to write state after the view has gone or the
 * inputs have moved on - so a slow answer for a project the owner has left cannot overwrite the one
 * they are looking at now.
 */
function useLoaded<T>(fetchWith: (signal: AbortSignal) => Promise<T>, inputs: readonly unknown[]): Loaded<T> {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<MilestoneApiError | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);

    fetchWith(controller.signal)
      .then((answer) => {
        if (!active) {
          return;
        }

        setData(answer);
        setFailure(null);
      })
      .catch((thrown: unknown) => {
        if (!active || isAbort(thrown)) {
          return;
        }

        setFailure(asFailure(thrown));
      })
      .finally(() => {
        if (active) {
          setLoading(false);
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...inputs, nonce]);

  return { data, loading, failure, reload: useCallback(() => setNonce((value) => value + 1), []) };
}

export function useProjects(list: (signal: AbortSignal) => Promise<ProjectPage>): Loaded<ProjectPage> {
  return useLoaded(list, []);
}

export function useProject(
  get: (projectId: string, signal: AbortSignal) => Promise<Project>,
  projectId: string,
): Loaded<Project> {
  return useLoaded((signal) => get(projectId, signal), [projectId]);
}

export function useMilestones(
  list: (projectId: string, status: MilestoneStatus | 'all', signal: AbortSignal) => Promise<MilestonePage>,
  projectId: string,
  status: MilestoneStatus | 'all',
): Loaded<MilestonePage> {
  return useLoaded((signal) => list(projectId, status, signal), [projectId, status]);
}

export function useMilestone(
  get: (milestoneId: string, signal: AbortSignal) => Promise<Milestone>,
  milestoneId: string,
): Loaded<Milestone> {
  return useLoaded((signal) => get(milestoneId, signal), [milestoneId]);
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof Error && thrown.name === 'AbortError';
}

function asFailure(thrown: unknown): MilestoneApiError {
  if (thrown instanceof MilestoneApiError) {
    return thrown;
  }

  return new MilestoneApiError(
    {
      code: 'internal_error',
      message: thrown instanceof Error && thrown.message.length > 0
        ? thrown.message
        : 'The request could not be completed.',
      traceId: '',
    },
    0,
  );
}
