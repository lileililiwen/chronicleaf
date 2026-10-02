import { useOwnerSession } from './features/community-access/useOwnerSession';
import type { OwnerSessionState } from './features/community-access/useOwnerSession';
import { SessionGate } from './features/community-access/Settings/SessionGate';
import { ProjectArea } from './features/projects-and-milestones/ProjectArea';

/** Visible identity of the shell. Asserted by the browser fixtures in e2e/shell.spec.ts. */
export const BRAND = 'Chronicleaf';

/** The product tagline, identical to the one in the repository README. */
export const TAGLINE = 'Turn project milestones into publishable stories.';

/**
 * The shell's own notice that the rest of the product is not here yet. The text is kept exactly as
 * the runtime shell fixed it, because the browser fixture asserts it: what it now stands for is the
 * story half of the product - evidence, drafts, export and publishing - which later changes add. The
 * project half it originally referred to is built in this change, and the panel below says so.
 */
export const SETUP_PENDING = 'Project setup coming in a later change.';

export function App() {
  // The session is read once, here. One reader means one antiforgery value is ever in flight, so a
  // value this page issues for a mutation cannot be rotated away by another component asking for
  // its own at the same moment.
  const session: OwnerSessionState = useOwnerSession();

  return (
    <main className="shell" data-testid="app-shell">
      <section className="shell__panel">
        <h1 className="shell__brand" data-testid="brand">
          {BRAND}
        </h1>
        <p className="shell__tagline" data-testid="tagline">
          {TAGLINE}
        </p>

        {/*
          COMMUNITY-ACCESS-001: the local owner signs in here, and only here. The session cookie is
          the browser's own, so this component is a form and a state, never a credential store.
        */}
        <SessionGate session={session} />

        {/*
          PROJECTS-AND-MILESTONES-001/002. The product is behind the session gate on purpose: these
          routes are private, so a browser without a session sees the sign-in form and nothing else.
        */}
        {session.status === 'signed-in' ? (
          <div className="product" data-testid="product-area">
            <ProjectArea />
          </div>
        ) : null}

        <section className="later" data-testid="later-packages">
          <h2 className="later__heading">Not in this change</h2>
          <ul className="later__list">
            <li>Evidence bundles, capture, media and background jobs.</li>
            <li>Turning a project&apos;s milestones into a drafted or exported story.</li>
          </ul>
          <p className="shell__state" data-testid="setup-pending">
            {SETUP_PENDING}
          </p>
        </section>
      </section>
    </main>
  );
}
