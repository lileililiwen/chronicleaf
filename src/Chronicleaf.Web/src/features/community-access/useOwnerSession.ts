import { useCallback, useEffect, useState } from 'react';

import { readAntiforgery, readSession, signIn as requestSignIn, signOut as requestSignOut } from './api';
import type { AntiforgeryMetadata, OwnerSession } from './types';
import { SessionApiError } from './api';

type Status = 'loading' | 'signed-in' | 'signed-out';

/**
 * The owner's session, as this component knows it.
 *
 * The cookie is the authority: this hook only mirrors what the server already decided. It never
 * stores the access token, the cookie value or the antiforgery value anywhere durable, and it
 * never invents a session - `signed-out` is the honest starting state for a browser that has not
 * been given a token yet.
 */
export interface OwnerSessionState {
  readonly status: Status;
  readonly session: OwnerSession | null;
  readonly antiforgery: AntiforgeryMetadata | null;
  readonly error: string | null;
  readonly signIn: (accessToken: string) => Promise<boolean>;
  readonly signOut: () => Promise<void>;
}

export function useOwnerSession(): OwnerSessionState {
  const [status, setStatus] = useState<Status>('loading');
  const [session, setSession] = useState<OwnerSession | null>(null);
  const [antiforgery, setAntiforgery] = useState<AntiforgeryMetadata | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();

    readSession(controller.signal)
      .then((found) => {
        if (controller.signal.aborted) {
          return;
        }

        setSession(found);
        setStatus(found ? 'signed-in' : 'signed-out');
      })
      .catch((failure: unknown) => {
        if (controller.signal.aborted) {
          return;
        }

        // A browser that cannot even ask is not a signed-in browser, and the reason is shown
        // rather than swallowed.
        setStatus('signed-out');
        setError(failure instanceof Error ? failure.message : 'The session could not be read.');
      });

    return () => controller.abort();
  }, []);

  const signIn = useCallback(async (accessToken: string) => {
    setError(null);

    try {
      await requestSignIn(accessToken);
      const found = await readSession();
      const metadata = await readAntiforgery();

      setSession(found);
      setAntiforgery(metadata);
      setStatus('signed-in');

      return true;
    } catch (failure) {
      setError(
        failure instanceof SessionApiError
          ? failure.message
          : 'The sign-in could not be completed.',
      );

      return false;
    }
  }, []);

  const signOut = useCallback(async () => {
    setError(null);

    try {
      // The antiforgery value is requested for this session, used once, and dropped. It is never
      // kept anywhere a later request could reuse it.
      const metadata = await readAntiforgery();
      await requestSignOut(metadata);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'The sign-out could not be completed.');

      return;
    } finally {
      setAntiforgery(null);
    }

    setSession(null);
    setStatus('signed-out');
  }, []);

  return { status, session, antiforgery, error, signIn, signOut };
}
