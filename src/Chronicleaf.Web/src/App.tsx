import { useOwnerSession } from './features/community-access/useOwnerSession';
import type { OwnerSessionState } from './features/community-access/useOwnerSession';
import { SessionGate } from './features/community-access/Settings/SessionGate';
import { ProjectArea } from './features/projects-and-milestones/ProjectArea';
import { useRoute } from './features/projects-and-milestones/useRoute';
import type { Route } from './features/projects-and-milestones/useRoute';
import { PublicJourneyView } from './features/journey-publish/PublicJourneyView';

/** Visible identity of the shell. The brand mark links home; it is a nav control, not a heading. */
export const BRAND = 'Chronicleaf';

/** The product tagline, identical to the one in the repository README. */
export const TAGLINE = 'Turn project milestones into publishable stories.';

/**
 * The top-level nav, in core-journey order (requirement §9: connect repo →
 * milestone → evidence → screenshot → draft → review → export reads through
 * Projects → Timeline → Operations → Credentials → Settings).
 */
export const NAV_ENTRIES = [
  { label: 'Projects', path: '/', testid: 'nav-projects' },
  { label: 'Timeline', path: '/timeline', testid: 'nav-timeline' },
  { label: 'Operations', path: '/operations', testid: 'nav-operations' },
  { label: 'Credentials', path: '/settings/credentials', testid: 'nav-credentials' },
  { label: 'Settings', path: '/settings', testid: 'nav-settings' },
] as const;

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

  if (session.status === 'loading') {
    return (
      <main className="shell" data-testid="app-shell">
        <section className="shell__panel">
          <h1 className="shell__brand" data-testid="brand">
            {BRAND}
          </h1>
          <p className="shell__tagline" data-testid="tagline">
            {TAGLINE}
          </p>
          <SessionGate session={session} />
        </section>
      </main>
    );
  }

  if (session.status === 'signed-out') {
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
        </section>
      </main>
    );
  }

  return <ProductShell session={session} />;
}

/**
 * PRODUCT-SHELL-UX-001. One shell, one nav, one header per page.
 *
 * The brand mark links to the dashboard; every product area is one nav step
 * away from every other, so no page is reachable only through a back-link.
 * Each view renders its own h1 — the shell never repeats a global heading.
 */
function ProductShell({ session }: { readonly session: OwnerSessionState }) {
  // The one route reader for the signed-in product. It is owned here and
  // handed to ProjectArea, so the nav and the view can never disagree about
  // which path the owner is on.
  const route: Route = useRoute();
  const { path, navigate } = route;

  return (
    <div className="shell shell--app" data-testid="app-shell">
      <div className="app">
        <header className="app__header">
          <button type="button" className="app__brand" onClick={() => navigate('/')} data-testid="brand-home">
            {BRAND}
          </button>
          <p className="app__tagline" data-testid="tagline">
            {TAGLINE}
          </p>
          <nav className="nav" aria-label="Product" data-testid="app-nav">
            {NAV_ENTRIES.map((entry) => (
              <button
                key={entry.path}
                type="button"
                className={navClass(path, entry.path)}
                aria-current={isActive(path, entry.path) ? 'page' : undefined}
                onClick={() => navigate(entry.path)}
                data-testid={entry.testid}
              >
                {entry.label}
              </button>
            ))}
          </nav>
        </header>

        {/*
          PROJECTS-AND-MILESTONES-001/002. The product is behind the session gate on purpose: these
          routes are private, so a browser without a session sees the sign-in form and nothing else.
        */}
        <div className="product" data-testid="product-area">
          <ProjectArea route={route} session={session} />
        </div>

        {/*
          The signed-in session bar: sign-out lives here, on every product page,
          while session internals (actor, idle timeout, lifetime) render only
          under /settings. See SessionGate. It is hidden on /settings itself,
          where the settings page owns the session display and the bar would
          duplicate its heading and sign-out.
        */}
        {path === '/settings' ? null : (
          <SessionGate session={session} onSettings={() => navigate('/settings')} />
        )}
      </div>
    </div>
  );
}

function isActive(path: string, entry: string): boolean {
  if (entry === '/') {
    return path === '/' || path.startsWith('/projects');
  }

  if (entry === '/settings') {
    return path === '/settings' || path.startsWith('/settings/team') || path.startsWith('/settings/tenancy');
  }

  return path === entry || path.startsWith(`${entry}/`);
}

function navClass(path: string, entry: string): string {
  return isActive(path, entry) ? 'nav__item nav__item--active' : 'nav__item';
}
