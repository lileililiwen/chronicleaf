import { useOwnerSession } from './features/community-access/useOwnerSession';
import type { OwnerSessionState } from './features/community-access/useOwnerSession';
import { SessionGate } from './features/community-access/Settings/SessionGate';
import { ProjectArea } from './features/projects-and-milestones/ProjectArea';
import { PublicJourneyView } from './features/journey-publish/PublicJourneyView';

/** Visible identity of the shell. Asserted by the browser fixtures in e2e/shell.spec.ts. */
export const BRAND = 'Chronicleaf';

/** The product tagline, identical to the one in the repository README. */
export const TAGLINE = 'Turn project milestones into publishable stories.';

/**
 * The shell's own notice that the rest of the product is not here yet. The text is kept exactly as
 * the runtime shell fixed it, because the browser fixture asserts it: what it now stands for is the
 * story half of the product - drafts are written and reviewable, but export and publishing are not.
 */
export const SETUP_PENDING = 'Project setup coming in a later change.';

/**
 * What is still not here, in the shell's own list.
 *
 * Kept honest rather than aspirational. Grounded drafting now exists, so "AI drafting" is no longer on
 * this list; what remains after it is the human review and approval step and the export and publication
 * half, and those are separate packages with their own approval rules. A list that kept naming a
 * delivered capability would be a list nobody reads.
 */
const LATER_ITEMS = [
  'Human review and approval of a draft.',
  'Markdown export and publishing to external services.',
];

export function App() {
  // The public journey view is anonymous and sessionless: it reads only the public
  // projection route, so it renders before - and without - the session gate. The path is
  // read statically because every arrival here is a full document load: the server answers
  // every non-API path with this same document, and no in-app navigation pushes a
  // /journey/* path.
  const journeySlug = journeySlugOf(window.location.pathname);

  if (journeySlug !== null) {
    return (
      <main className="shell" data-testid="app-shell">
        <section className="shell__panel">
          <h1 className="shell__brand" data-testid="brand">
            {BRAND}
          </h1>
          <p className="shell__tagline" data-testid="tagline">
            {TAGLINE}
          </p>
          <PublicJourneyView slug={journeySlug} />
        </section>
      </main>
    );
  }

  return <SignedInApp />;
}

/** The slug of a public journey path, or null when this is not one. */
function journeySlugOf(path: string): string | null {
  const segments = path.split('/').filter((segment) => segment.length > 0);

  if (segments.length === 2 && segments[0] === 'journey' && segments[1].length > 0) {
    return segments[1];
  }

  return null;
}

function SignedInApp() {
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
            {LATER_ITEMS.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
          <p className="shell__state" data-testid="setup-pending">
            {SETUP_PENDING}
          </p>
        </section>
      </section>
    </main>
  );
}
