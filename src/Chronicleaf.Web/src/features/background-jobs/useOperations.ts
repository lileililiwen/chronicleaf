import { useCallback, useEffect, useRef, useState } from 'react';

import { OperationApiError } from './api';
import type { Operation, OperationPage, OperationStatus } from './types';

/**
 * Loading state for the owner's own background operations.
 *
 * The shape is the delivered milestone reader's, for the same reason: a view has to be honest about
 * the three things that can be true - the answer is still coming, the server sent something, or the
 * server refused. There is no sample row and no optimistic status here, so an operation that has not
 * been reported yet is not drawn as `queued`, and an operation that is still running is not drawn as
 * finished because the panel would rather it looked done.
 */

/** How long to wait before re-reading a list that still holds something that can change. */
export const ACTIVE_POLL_INTERVAL_MS = 2000;

export interface Loaded<T> {
  readonly data: T | null;
  readonly loading: boolean;
  readonly failure: OperationApiError | null;
  readonly reload: () => void;
}

/** Whether a list still holds something that can change on its own. */
function hasActive(items: readonly Operation[] | undefined): boolean {
  return (items ?? []).some(
    (operation) => operation.status === 'queued' || operation.status === 'running',
  );
}

/**
 * Runs one read whenever its inputs change, and refuses to write state after the view has gone or the
 * inputs have moved on - so a slow answer for an operation the owner has left cannot overwrite the one
 * they are looking at now.
 *
 * When <paramref name="pollWhile"/> says the last answer can still change, the read is repeated after
 * a short wait. A list whose operations are all finished is left alone: polling forever would be
 * traffic with no possible change behind it, and a panel that keeps asking is a panel that cannot be
 * trusted to be quiet.
 */
function useLoaded<T>(
  fetchWith: (signal: AbortSignal) => Promise<T>,
  inputs: readonly unknown[],
  pollWhile: (data: T | null) => boolean = () => false,
): Loaded<T> {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<OperationApiError | null>(null);
  const [nonce, setNonce] = useState(0);
  const latest = useRef<T | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const read = () => {
      setLoading(true);

      fetchWith(controller.signal)
        .then((answer) => {
          if (!active) {
            return;
          }

          latest.current = answer;
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
          if (!active) {
            return;
          }

          setLoading(false);

          if (pollWhile(latest.current)) {
            timer = setTimeout(read, ACTIVE_POLL_INTERVAL_MS);
          }
        });
    };

    read();

    return () => {
      active = false;
      controller.abort();
      if (timer !== undefined) {
        clearTimeout(timer);
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...inputs, nonce]);

  return { data, loading, failure, reload: useCallback(() => setNonce((value) => value + 1), []) };
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof Error && thrown.name === 'AbortError';
}

function asFailure(thrown: unknown): OperationApiError {
  if (thrown instanceof OperationApiError) {
    return thrown;
  }

  return new OperationApiError(
    {
      code: 'internal_error',
      message:
        thrown instanceof Error && thrown.message.length > 0
          ? thrown.message
          : 'The request could not be completed.',
      traceId: '',
    },
    0,
  );
}

/** The scope's operations, re-read while any of them can still change. */
export function useOperations(
  list: (signal: AbortSignal) => Promise<OperationPage>,
): Loaded<OperationPage> {
  return useLoaded(list, [], (page) => hasActive(page?.items));
}

/** One operation, re-read while it can still change. */
export function useOperation(
  get: (operationId: string, signal: AbortSignal) => Promise<Operation>,
  operationId: string,
): Loaded<Operation> {
  return useLoaded(
    (signal) => get(operationId, signal),
    [operationId],
    (operation) => operation?.cancellable === true,
  );
}

/** Narrows a list to one status, for the panel's filter. */
export function filterByStatus(
  items: readonly Operation[],
  status: OperationStatus | 'all',
): readonly Operation[] {
  return status === 'all' ? items : items.filter((operation) => operation.status === status);
}
