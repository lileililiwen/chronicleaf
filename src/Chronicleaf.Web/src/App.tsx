import { SessionGate } from './features/community-access/Settings/SessionGate';

/** Visible identity of the shell. Asserted by the browser fixtures in e2e/shell.spec.ts. */
export const BRAND = 'Chronicleaf';

/** The product tagline, identical to the one in the repository README. */
export const TAGLINE = 'Turn project milestones into publishable stories.';

/**
 * The single honest state of this shell: no milestone, credential, editor, export or
 * provider surface exists yet. Later OpenSpec changes replace this state.
 */
export const SETUP_PENDING = 'Project setup coming in a later change.';

export function App() {
  return (
    <main className="shell" data-testid="app-shell">
      <section className="shell__panel">
        <h1 className="shell__brand" data-testid="brand">
          {BRAND}
        </h1>
        <p className="shell__tagline" data-testid="tagline">
          {TAGLINE}
        </p>
        <p className="shell__state" data-testid="setup-pending">
          {SETUP_PENDING}
        </p>

        {/*
          COMMUNITY-ACCESS-001: the local owner signs in here, and only here. The session cookie is
          the browser's own, so this component is a form and a state, never a credential store.
        */}
        <SessionGate />
      </section>
    </main>
  );
}
