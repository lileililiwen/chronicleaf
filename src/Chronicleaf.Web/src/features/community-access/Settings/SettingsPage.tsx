import type { OwnerSessionState } from '../useOwnerSession';
import { ABSOLUTE_LIFETIME_DAYS, IDLE_TIMEOUT_HOURS } from '../types';

/**
 * PRODUCT-SHELL-UX-001. The settings page: session internals plus the
 * credentials link, and nothing else.
 *
 * Owner-decided (2026-10-07): session internals + credentials link ONLY.
 * Provider catalogue and voice preset summaries stay where they are.
 *
 * This is the one place that renders the actor identity, the idle timeout
 * and the lifetime. The actor renders as a label — "this installation's
 * owner" — never as a raw UUID dump, so a capture of this page carries no
 * session value. The stable-UUID property itself is proven at the API
 * boundary (`GET /api/session` answers the owner's own id), not by reading
 * characters out of this page.
 */
export function SettingsPage({
  session: state,
  onNavigate,
}: {
  readonly session: OwnerSessionState;
  readonly onNavigate: (to: string) => void;
}) {
  const { session, signOut } = state;

  if (session === null) {
    return (
      <section className="view" data-testid="settings">
        <h1 className="view__heading" data-testid="settings-heading">
          Settings
        </h1>
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="settings-loading">
          Reading this installation&apos;s session…
        </p>
      </section>
    );
  }

  return (
    <section className="view" data-testid="settings">
      <h1 className="view__heading" data-testid="settings-heading">
        Settings
      </h1>
      <p className="view__hint" data-testid="settings-lede">
        This installation&apos;s session and where its credentials live.
      </p>

      <h2 className="view__section" data-testid="settings-session-heading">
        Owner session
      </h2>
      <dl className="session__facts" data-testid="settings-session-facts">
        <dt>Signed in as</dt>
        <dd data-testid="settings-actor">This installation&apos;s owner</dd>
        <dt>Idle timeout</dt>
        <dd data-testid="settings-idle">{IDLE_TIMEOUT_HOURS} hours</dd>
        <dt>Session lifetime</dt>
        <dd data-testid="settings-lifetime">{ABSOLUTE_LIFETIME_DAYS} days</dd>
      </dl>
      <p className="view__actions">
        <button
          type="button"
          className="action"
          data-testid="settings-sign-out"
          onClick={() => {
            void signOut();
          }}
        >
          Sign out
        </button>
      </p>

      <h2 className="view__section" data-testid="settings-credentials-heading">
        Credentials
      </h2>
      <p className="view__state" data-testid="settings-credentials-body">
        Provider tokens live sealed in this installation&apos;s credential vault. Chronicleaf can
        show that a token is configured and when it last changed; it cannot show the value.
      </p>
      <p className="view__actions">
        <button
          type="button"
          className="action"
          onClick={() => onNavigate('/settings/credentials')}
          data-testid="settings-credentials-link"
        >
          Provider credentials
        </button>
      </p>
    </section>
  );
}
