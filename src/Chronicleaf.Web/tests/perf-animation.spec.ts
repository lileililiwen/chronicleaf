import { expect, test, type Page, type Route } from '@playwright/test';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer, type ViteDevServer } from 'vite';

/**
 * WEB-PERF-001..004. Performance and animation, proven against the real
 * components: this suite starts the Vite dev server and route-mocks every
 * `/api/*` call, so React renders for real with no server and no database.
 * Transitions are proven through computed styles (the DOM cannot show a
 * 120ms interval any other way) and the reduced-motion guard through the
 * built CSS (the rule only exists to survive the build).
 *
 * Run standalone after `npm ci` AND `vite build` (the WEB-PERF-004 oracle
 * reads `dist/assets/*.css`):
 *   npx playwright test tests/perf-animation.spec.ts
 */

const WEB_DIR = join(dirname(fileURLToPath(import.meta.url)), '..');
const SRC_DIR = join(WEB_DIR, 'src');
const STYLES = readFileSync(join(SRC_DIR, 'styles.css'), 'utf8');
const TOKENS = readFileSync(join(SRC_DIR, 'tokens.css'), 'utf8');

let server: ViteDevServer;
let origin: string;

test.beforeAll(async () => {
  server = await createServer({
    root: WEB_DIR,
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
  /** When set, the response is held until the gate opens — the loading state stays observable. */
  readonly gate?: Promise<void>;
}

const ENVELOPE = { code: 'internal_error', message: 'Not mocked in this test.', traceId: 'test' };

/** Route-mocks `/api/*` with an exact pathname+method dispatch table. */
async function mockApi(page: Page, entries: readonly MockEntry[]): Promise<void> {
  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    const hit = entries.find((entry) => entry.method === request.method() && entry.path === pathname);

    if (hit === undefined) {
      await route.fulfill({ status: 500, contentType: 'application/json', json: ENVELOPE });
      return;
    }

    if (hit.gate !== undefined) {
      await hit.gate;
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

function sessionMocks(): MockEntry[] {
  return [
    { method: 'GET', path: '/api/session', json: SESSION },
    { method: 'GET', path: '/api/session/antiforgery', json: ANTIFORGERY },
  ];
}

const PROJECT_P1 = {
  id: 'p1',
  name: 'Harbor',
  description: 'Guides ships home.',
  projectUrl: null,
  repoConnectionId: null,
  revision: 3,
  archivedAt: null,
};

const MILESTONE_M1 = {
  id: 'm1',
  projectId: 'p1',
  title: 'Launch',
  version: null,
  occurredAt: '2026-09-01T10:00:00.000Z',
  type: 'feature',
  source: 'manual',
  sourceKey: null,
  status: 'confirmed',
  revision: 2,
  notes: '',
  knownLimitations: [],
  nextSteps: [],
};

const CANDIDATE_C1 = {
  id: 'c1',
  mediaId: 'med1',
  projectId: 'p1',
  sourceUrl: 'https://example.com/pricing',
  finalUrl: 'https://example.com/pricing',
  viewport: 'desktop',
  width: 1440,
  height: 900,
  capturedAt: '2026-10-01T00:00:00.000Z',
  status: 'candidate',
  caption: '',
  isPrimary: false,
  displayOrder: 0,
  revision: 1,
};

function builtCssFiles(): string[] {
  const dir = join(WEB_DIR, 'dist', 'assets');
  if (!existsSync(dir)) return [];
  return readdirSync(dir)
    .filter((f) => f.endsWith('.css'))
    .map((f) => readFileSync(join(dir, f), 'utf8'));
}

test('WEB-PERF-001: candidate images carry payload dimensions plus lazy decoding', async ({ page }) => {
  await mockApi(page, [
    ...sessionMocks(),
    { method: 'GET', path: '/api/projects/p1', json: PROJECT_P1 },
    { method: 'GET', path: '/api/projects/p1/milestones', json: { items: [MILESTONE_M1], nextCursor: null } },
    {
      method: 'GET',
      path: '/api/milestones/m1/evidence',
      json: { milestoneId: 'm1', revision: 2, items: [], nextCursor: null },
    },
    {
      method: 'GET',
      path: '/api/milestones/m1/screenshots',
      json: { items: [CANDIDATE_C1], mediaBaseUrl: '/api/media' },
    },
  ]);
  await page.goto(`${origin}/projects/p1/evolution`);

  const image = page.getByTestId('capture-candidate-image');
  await expect(image).toBeAttached();
  // The box precedes the bytes: dimensions equal the payload's own numbers.
  await expect(image).toHaveAttribute('width', '1440');
  await expect(image).toHaveAttribute('height', '900');
  await expect(image).toHaveAttribute('loading', 'lazy');
  await expect(image).toHaveAttribute('decoding', 'async');
});

test('WEB-PERF-002: the 120ms shorthand is declared on all five control selectors', async () => {
  const shorthand = 'transition: color 120ms, background-color 120ms, border-color 120ms;';
  const hits = STYLES.split(shorthand).length - 1;
  expect(hits, `expected the shared transition on 5 selectors, found ${hits}`).toBe(5);

  for (const selector of ['.action', '.nav__item', '.filter', '.badge', 'button.link']) {
    const block = new RegExp(`${selector.replace('.', '\\.')}\\s*\\{[\\s\\S]*?\\}`, 'g');
    const matches = STYLES.match(block) ?? [];
    expect(
      matches.some((m) => m.includes('transition: color 120ms')),
      `${selector} carries the shared transition`,
    ).toBe(true);
  }
});

test('WEB-PERF-002: computed transition-duration is 0.12s on sampled controls', async ({ page }) => {
  await page.setContent(`<html><head><style>${TOKENS}\n${STYLES}</style></head><body>
<button class="action" id="action">a</button>
<button class="nav__item" id="navitem">n</button>
<button class="filter" id="filter">f</button>
</body></html>`);
  for (const id of ['action', 'navitem', 'filter']) {
    const duration = await page.locator(`#${id}`).evaluate(
      (element) => getComputedStyle(element).getPropertyValue('transition-duration'),
    );
    // The engine normalizes the authored 120ms to seconds and repeats it
    // per transitioned property; the 120ms literal itself is proven in the
    // source oracle above.
    const parts = duration.split(',').map((part) => part.trim());
    expect(parts.length, `#${id} transitions three properties`).toBe(3);
    for (const part of parts) expect(part, `#${id} transition-duration`).toBe('0.12s');
  }
});

test('WEB-PERF-003: list containers report busy while loading and idle once settled', async ({
  page,
}) => {
  let openMilestones!: () => void;
  const milestonesGate = new Promise<void>((resolve) => {
    openMilestones = () => resolve();
  });
  await mockApi(page, [
    ...sessionMocks(),
    { method: 'GET', path: '/api/projects/p1', json: PROJECT_P1 },
    {
      method: 'GET',
      path: '/api/projects/p1/milestones',
      json: { items: [MILESTONE_M1], nextCursor: null },
      gate: milestonesGate,
    },
  ]);
  await page.goto(`${origin}/projects/p1`);

  // The milestone list owns a section now (it rendered a fragment before),
  // and that section carries the container side of the loading discipline.
  const panel = page.getByTestId('milestone-list-panel');
  await expect(panel).toHaveAttribute('aria-busy', 'true');
  await expect(page.getByTestId('milestones-loading')).toHaveAttribute('aria-busy', 'true');

  openMilestones();
  await expect(page.getByTestId('milestone-list')).toBeVisible();
  await expect(panel).toHaveAttribute('aria-busy', 'false');
});

test('WEB-PERF-003: operations and clients containers report busy while loading', async ({ page }) => {
  let openOperations!: () => void;
  const operationsGate = new Promise<void>((resolve) => {
    openOperations = () => resolve();
  });
  let openClients!: () => void;
  const clientsGate = new Promise<void>((resolve) => {
    openClients = () => resolve();
  });
  await mockApi(page, [
    ...sessionMocks(),
    { method: 'GET', path: '/api/operations', json: { items: [], nextCursor: null }, gate: operationsGate },
    { method: 'GET', path: '/api/clients', json: { items: [], nextCursor: null }, gate: clientsGate },
  ]);

  await page.goto(`${origin}/operations`);
  const operations = page.getByTestId('operations-panel');
  await expect(operations).toHaveAttribute('aria-busy', 'true');
  openOperations();
  await expect(operations).toHaveAttribute('aria-busy', 'false');

  await page.goto(`${origin}/clients`);
  const clients = page.getByTestId('clients');
  await expect(clients).toHaveAttribute('aria-busy', 'true');
  openClients();
  await expect(clients).toHaveAttribute('aria-busy', 'false');
});

test('WEB-PERF-004: the reduced-motion guard survives into the built CSS', async () => {
  // Source side: the guard sits at the top of styles.css with the
  // package-naming comment.
  expect(STYLES).toContain('web-perf-and-animation');
  expect(STYLES).toContain('@media (prefers-reduced-motion: reduce)');
  expect(STYLES).toContain('transition-duration: 0.01ms !important;');

  // Built side: the rule the browser actually receives. LightningCSS
  // minifies `0.01ms` to `.01ms`, so the oracle matches either spelling.
  const built = builtCssFiles();
  expect(built.length, 'run `vite build` before this suite: no dist/assets/*.css found').toBeGreaterThan(0);
  const joined = built.join('\n');
  expect(joined).toContain('prefers-reduced-motion');
  expect(joined).toMatch(/0?\.01ms/);
});
