import { useCallback, useEffect, useState } from 'react';

import { EvidenceApiError } from './api';
import type { EvidencePage } from './types';

/**
 * Loading state for one milestone's evidence.
 *
 * The state is exactly what a view needs to be honest about: whether the answer is still on its way,
 * what the server actually returned, and what it refused. There is no placeholder bundle and no
 * optimistic item - a milestone with no evidence reports revision 0 and an empty list, and this hook
 * passes that through as it is rather than substituting something to draw.
 */
export interface EvidenceLoaded {
  readonly data: EvidencePage | null;
  readonly loading: boolean;
  readonly failure: EvidenceApiError | null;
  /** Fetches again, leaving the current data visible while it does. */
  readonly reload: () => void;
}

/**
 * Reads the evidence whenever the milestone changes, and refuses to write state after the view has
 * gone or the milestone has moved on - so a slow answer for a milestone the owner has left cannot
 * overwrite the one they are looking at now.
 */
export function useEvidence(
  list: (milestoneId: string, limit: number | undefined, signal: AbortSignal) => Promise<EvidencePage>,
  milestoneId: string,
): EvidenceLoaded {
  const [data, setData] = useState<EvidencePage | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<EvidenceApiError | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);

    list(milestoneId, undefined, controller.signal)
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
  }, [milestoneId, nonce]);

  return { data, loading, failure, reload: useCallback(() => setNonce((value) => value + 1), []) };
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof Error && thrown.name === 'AbortError';
}

function asFailure(thrown: unknown): EvidenceApiError {
  if (thrown instanceof EvidenceApiError) {
    return thrown;
  }

  return new EvidenceApiError(
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
