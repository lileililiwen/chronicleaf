import { useEffect, useRef, useState } from 'react';

import type { OwnerSessionState } from '../useOwnerSession';
import { ABSOLUTE_LIFETIME_DAYS, IDLE_TIMEOUT_HOURS } from '../types';

/**
 * The local owner's sign-in and sign-out, and nothing else.
 *
 * This is a single-user Community installation, so there is no signup, no invitation, no second
 * identity and no credential store: the owner types the setup token the operator generated, the
 * browser keeps the session cookie, and signing out revokes the session on the server. The token
 * input is the browser's own password field, so a password manager may offer it to this one
 * local origin and to no other.
 *
 * The whole form works from the keyboard: the labelled field is reachable with Tab, Enter submits
 * it, and the state that follows is announced in a live region.
 *
 * The session state is passed in rather than read here, so the whole page reads it exactly once.
 * Two components each asking the server for their own session would each be handed their own
 * antiforgery value, and the second would invalidate the first - so one reader, one value.
 */
export function SessionGate({ session: state }: { readonly session: OwnerSessionState }) {
  const { status, session, error, signIn, signOut } = state;
  const [accessToken, setAccessToken] = useState('');
  const [busy, setBusy] = useState(false);
  const tokenInput = useRef<HTMLInputElement | null>(null);

  // Focus moves to the field whenever a login is needed, so the keyboard flow never needs a mouse.
  useEffect(() => {
    if (status === 'signed-out') {
      tokenInput.current?.focus();
    }
  }, [status]);

  const submit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    if (busy || accessToken.length === 0) {
      return;
    }

    setBusy(true);
    const accepted = await signIn(accessToken);

    if (accepted) {
      // The token is not kept after it has been exchanged for a session.
      setAccessToken('');
    }

    setBusy(false);
  };

  if (status === 'loading') {
    return (
      <section className="session" aria-busy="true" data-testid="session-gate">
        <p className="session__state" data-testid="session-loading">
          Checking this installation&apos;s session…
        </p>
      </section>
    );
  }

  if (status === 'signed-in' && session) {
    return (
      <section className="session" data-testid="session-gate">
        <h2 className="session__heading" data-testid="session-heading">
          Owner session
        </h2>
        <dl className="session__facts">
          <dt>Actor</dt>
          <dd data-testid="session-actor">{session.actorId}</dd>
          <dt>Idle timeout</dt>
          <dd>{IDLE_TIMEOUT_HOURS} hours</dd>
          <dt>Session lifetime</dt>
          <dd>{ABSOLUTE_LIFETIME_DAYS} days</dd>
        </dl>
        <button
          type="button"
          className="session__action"
          data-testid="session-sign-out"
          onClick={() => {
            void signOut();
          }}
        >
          Sign out
        </button>
        <p className="session__error" role="alert" data-testid="session-error">
          {error ?? ''}
        </p>
      </section>
    );
  }

  return (
    <section className="session" data-testid="session-gate">
      <h2 className="session__heading" data-testid="session-heading">
        Owner sign-in
      </h2>
      <p className="session__hint">
        This installation has one owner. Enter the setup token the operator generated; it is exchanged
        once for a session cookie and is not stored by this page.
      </p>
      <form className="session__form" onSubmit={submit} data-testid="session-form">
        <label className="session__label" htmlFor="chronicleaf-access-token">
          Access token
        </label>
        <input
          className="session__input"
          id="chronicleaf-access-token"
          ref={tokenInput}
          type="password"
          name="accessToken"
          autoComplete="current-password"
          value={accessToken}
          disabled={busy}
          onChange={(event) => setAccessToken(event.target.value)}
          data-testid="session-token-input"
        />
        <button
          type="submit"
          className="session__action"
          disabled={busy || accessToken.length === 0}
          data-testid="session-submit"
        >
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
      <p className="session__error" role="alert" data-testid="session-error">
        {error ?? ''}
      </p>
    </section>
  );
}
