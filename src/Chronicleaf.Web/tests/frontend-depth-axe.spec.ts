import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { createServer, type ViteDevServer } from 'vite';

/**
 * GATE-ADOPT-003. The deep frontend check's axe half: a full WCAG pass over
 * every top-level nav entry, proven against the real components with no
 * server and no database.
 *
 * This suite starts the Vite dev server and route-mocks every `/api/*` call
 * (the form-feedback pattern), so React renders for real: the session reads
 * authenticated, every collection reads empty, and every credential provider
 * reads unconfigured. Axe then scans the settled empty-state render of each
 * of the six `NAV_ENTRIES` pages with the same `wcag2a/wcag2aa/wcag21a/
 * wcag21aa` tags the a11y-core smoke uses, and any critical or serious
 * violation fails the gate loudly, naming the page.
 *
 * The three app-backed UI suites (a11y-core, a11y-roles, responsive-shell)
 * need the published application with a real PostgreSQL behind it, so they
 * execute inside scripts/e2e.sh — not here. scripts/gate/frontend-depth.sh
 * proves they are wired there; this file is the standalone half.
 *
 * Run standalone after `npm ci` (no build needed):
 *   npx playwright test tests/frontend-depth-axe.spec.ts
 */

let server: ViteDevServer;
let origin: string;

test.beforeAll(async () => {
  server = await createServer({
    root: new URL('..', import.meta.url).pathname,
    logLevel: 'silent',
    server: { port: 0, host: '127.0.0.1' },
  });
  await server.listen();
  const url = server.resolvedUrls?.local?.[0];
  expect(url, 'vite dev server published no local url').toBeTruthy();
  origin = url as string;
});

test.afterAll(async () => {
  await server?.close();
});

interface MockEntry {
  readonly method: string;
  readonly path: string;
  readonly status?: number;
  readonly json?: unknown;
}

const ENVELOPE = { code: 'internal_error', message: 'Not mocked in this test.', traceId: 'test' };

/** Route-mocks `/api/*` with an exact pathname+method dispatch table. */
async function mockApi(page: Page, entries: readonly MockEntry[]): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    const hit = entries.find((entry) => entry.method === request.method() && entry.path === pathname);

    if (hit === undefined) {
      await route.fulfill({ status: 500, contentType: 'application/json', json: ENVELOPE });
      return;
    }

    await route.fulfill({
      status: hit.status ?? 200,
      contentType: 'application/json',
      json: hit.json ?? {},
    });
  });
}

const SESSION = { authenticated: true, actorId: '0192b5c0-7f1e-7a2b-9c3d-4e5f60718293' };
const ANTIFORGERY = { headerName: 'X-Chronicleaf-Test', token: 'test-token' };

const PROVIDERS = [
  'github',
  'github-webhook',
  'openai',
  'gemini',
  'anthropic',
  'openrouter',
  'ollama',
  'publisher',
] as const;

/** Every collection reads empty; every provider reads unconfigured. No secret anywhere. */
function depthMocks(): MockEntry[] {
  return [
    { method: 'GET', path: '/api/session', json: SESSION },
    { method: 'GET', path: '/api/session/antiforgery', json: ANTIFORGERY },
    { method: 'GET', path: '/api/projects', json: { items: [], nextCursor: null } },
    { method: 'GET', path: '/api/clients', json: { items: [], nextCursor: null } },
    { method: 'GET', path: '/api/operations', json: { items: [] } },
    ...PROVIDERS.map(
      (provider): MockEntry => ({
        method: 'GET',
        path: `/api/settings/credentials/${provider}`,
        json: { id: null, provider, configured: false, updatedAt: null },
      }),
    ),
  ];
}

/** One row per NAV_ENTRIES page: the path plus the settled-render oracle. */
const NAV_PAGES = [
  { path: '/', name: 'Projects' },
  { path: '/clients', name: 'Clients' },
  { path: '/timeline', name: 'Timeline' },
  { path: '/operations', name: 'Operations' },
  { path: '/settings/credentials', name: 'Credentials' },
  { path: '/settings', name: 'Settings' },
] as const;

async function expectSettled(page: Page, path: string): Promise<void> {
  switch (path) {
    case '/':
      await expect(
        page.getByTestId('dashboard-empty').or(page.getByTestId('dashboard-projects')),
      ).toBeVisible();
      break;
    case '/clients':
      await expect(
        page.getByTestId('clients-empty').or(page.getByTestId('clients-list')),
      ).toBeVisible();
      break;
    case '/timeline':
      await expect(
        page.getByTestId('timeline-empty').or(page.getByTestId('timeline-picker')),
      ).toBeVisible();
      break;
    case '/operations':
      await expect(
        page.getByTestId('operations-empty').or(page.getByTestId('operations-list')),
      ).toBeVisible();
      break;
    case '/settings/credentials':
      await expect(page.getByTestId('credential-vault-providers')).toBeVisible();
      break;
    case '/settings':
      await expect(page.getByTestId('settings-session-facts')).toBeVisible();
      break;
    default:
      break;
  }
}

for (const entry of NAV_PAGES) {
  test(`GATE-DEPTH-AXE: ${entry.name} (${entry.path}) reports zero critical or serious WCAG violations`, async ({
    page,
  }) => {
    await mockApi(page, depthMocks());
    await page.goto(`${origin}${entry.path}`);
    await expectSettled(page, entry.path);

    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();

    const criticalOrSerious = results.violations.filter(
      (violation) => violation.impact === 'critical' || violation.impact === 'serious',
    );
    if (criticalOrSerious.length > 0) {
      const summary = criticalOrSerious
        .map((v) => `${v.id} (${v.impact}): ${v.help} [${v.nodes.length} node(s)]`)
        .join('\n  ');
      throw new Error(
        `axe-core reported ${criticalOrSerious.length} critical/serious violation(s) on ${entry.name}:\n  ${summary}`,
      );
    }
  });
}
