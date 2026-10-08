import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer, type ViteDevServer } from 'vite';
import { formatRevisionConflict } from '../src/features/ui-feedback/revisionConflict';

/**
 * WEB-FEED-001..005. Form feedback, proven against the real components:
 * this suite starts the Vite dev server and route-mocks every `/api/*`
 * call, so React renders for real with no server and no database.
 *
 * Run standalone after `npm ci` (no build needed):
 *   npx playwright test tests/form-feedback.spec.ts
 */

const WEB_DIR = join(dirname(fileURLToPath(import.meta.url)), '..');
const SRC_DIR = join(WEB_DIR, 'src');

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
  readonly headers?: Record<string, string>;
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
      headers: hit.headers,
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

const DRAFT_A1 = {
  assetId: 'a1',
  type: 'draft',
  status: 'draft',
  currentRevision: 3,
  updatedAt: '2026-10-01T00:00:00.000Z',
  title: 'Story',
  summary: null,
  bodyMarkdown: 'Hello world',
  bodyHash: 'abc',
  evidenceBundleId: null,
  evidenceRevision: 2,
  voiceRevision: null,
  voiceFingerprint: null,
  providerId: null,
  model: null,
  promptVersion: null,
  schemaVersion: null,
  createdAt: '2026-10-01T00:00:00.000Z',
  claims: [],
  groundingFindings: [],
  voiceFindings: [],
};

test('WEB-FEED-001: project save shows a role=status toast the dismiss button clears', async ({
  page,
}) => {
  const saved = { ...PROJECT_P1, id: 'p9', name: 'Harborlight', revision: 1 };
  await mockApi(page, [
    ...sessionMocks(),
    { method: 'POST', path: '/api/projects', json: saved },
    { method: 'GET', path: '/api/projects/p9', json: saved },
    { method: 'GET', path: '/api/projects', json: { items: [saved], nextCursor: null } },
  ]);
  await page.goto(`${origin}/projects/new`);

  const region = page.getByTestId('toast-region');
  await expect(region).toHaveAttribute('role', 'status');
  await expect(region).toHaveAttribute('aria-live', 'polite');
  await expect(page.getByTestId('toast')).toHaveCount(0);

  await expect(page.getByTestId('project-name-count')).toHaveText('0 / 120');
  await page.getByTestId('project-name-input').fill('Harborlight');
  await expect(page.getByTestId('project-name-count')).toHaveText('11 / 120');
  await page.getByTestId('project-description-input').fill('Guides ships home.');
  await expect(page.getByTestId('project-description-count')).toHaveText('18 / 2000');

  await page.getByTestId('project-save').click();
  const toast = page.getByTestId('toast');
  await expect(toast).toContainText('Project saved.');
  await expect(region).toContainText('Project saved.');

  await page.getByTestId('toast-dismiss').click();
  await expect(toast).toHaveCount(0);
  // The region itself never unmounts: an empty live region takes no space, so
  // attached (not visible) is the honest assertion while no toast is current.
  await expect(region).toBeAttached();
});

test('WEB-FEED-001: milestone save toast survives the post-save navigation', async ({ page }) => {
  const created = { ...MILESTONE_M1, id: 'm2', title: 'Launch day', revision: 1 };
  await mockApi(page, [
    ...sessionMocks(),
    { method: 'GET', path: '/api/projects/p1', json: PROJECT_P1 },
    { method: 'POST', path: '/api/projects/p1/milestones', json: created },
    { method: 'GET', path: '/api/projects/p1/milestones', json: { items: [created], nextCursor: null } },
  ]);
  await page.goto(`${origin}/projects/p1/milestones/new`);

  await page.getByTestId('milestone-title-input').fill('Launch day');
  await expect(page.getByTestId('milestone-title-count')).toHaveText('10 / 160');
  await page.getByTestId('milestone-notes-input').fill('ab');
  await expect(page.getByTestId('milestone-notes-count')).toHaveText('2 / 20000');

  await page.getByTestId('milestone-save').click();
  await expect(page).toHaveURL(/\/projects\/p1\/?$/);
  await expect(page.getByTestId('toast')).toContainText('Milestone saved.');
});

test('WEB-FEED-001: vault save toast auto-dismisses after 4s', async ({ page }) => {
  const unconfigured = (provider: string) => ({
    id: null,
    provider,
    configured: false,
    updatedAt: null,
  });
  await mockApi(page, [
    ...sessionMocks(),
    { method: 'GET', path: '/api/settings/credentials/openai', json: unconfigured('openai') },
    {
      method: 'PUT',
      path: '/api/settings/credentials/openai',
      json: { id: 'c1', provider: 'openai', configured: true, updatedAt: '2026-10-08T00:00:00.000Z' },
    },
  ]);
  await page.goto(`${origin}/settings/credentials`);

  const row = page.getByTestId('credential-openai');
  await expect(row.getByTestId('credential-openai-unconfigured')).toBeVisible();
  await row.getByTestId('credential-openai-input').fill('test-token-value');
  await row.getByTestId('credential-openai-save').click();

  await expect(page.getByTestId('toast')).toContainText('Credential stored.');
  await expect(page.getByTestId('toast')).toHaveCount(0, { timeout: 10_000 });
});

test('WEB-FEED-002: dirty tag and gold border show on typing and clear on save', async ({
  page,
}) => {
  await mockApi(page, [
    ...sessionMocks(),
    { method: 'GET', path: '/api/projects/p1', json: PROJECT_P1 },
    { method: 'GET', path: '/api/projects/p1/milestones', json: { items: [MILESTONE_M1], nextCursor: null } },
    {
      method: 'GET',
      path: '/api/milestones/m1/evidence',
      json: { milestoneId: 'm1', revision: 2, items: [], nextCursor: null },
    },
    { method: 'GET', path: '/api/milestones/m1/drafts', json: DRAFT_A1 },
    { method: 'GET', path: '/api/assets/a1/review', json: { reviewStatus: 'draft', approval: null } },
    { method: 'PATCH', path: '/api/assets/a1', json: { assetId: 'a1', revision: 4, bodyHash: 'def' } },
  ]);
  await page.goto(`${origin}/projects/p1/content`);

  const editor = page.getByTestId('review-editor');
  await expect(editor).toBeVisible();
  await expect(page.getByTestId('review-dirty-tag')).toHaveCount(0);

  await page.getByTestId('review-body').fill('Hello world, edited.');
  const tag = page.getByTestId('review-dirty-tag');
  await expect(tag).toBeVisible();
  await expect(tag).toHaveText('Unsaved changes');
  const borderColor = await editor.evaluate(
    (element) => getComputedStyle(element).getPropertyValue('border-left-color'),
  );
  expect(borderColor).toBe('rgb(217, 164, 65)');

  await page.getByTestId('review-save').click();
  await expect(tag).toHaveCount(0);
});

test('WEB-FEED-003: one conflict sentence shared by all three call sites', async () => {
  expect(formatRevisionConflict(null)).toBe(
    'This record changed since you opened it, so your edit was not saved.',
  );
  expect(formatRevisionConflict(7)).toBe(
    'This record changed since you opened it (the stored revision is 7). Your edit was not saved.',
  );

  const milestone = readFileSync(
    join(SRC_DIR, 'features/projects-and-milestones/MilestoneEditor.tsx'),
    'utf8',
  );
  const project = readFileSync(
    join(SRC_DIR, 'features/projects-and-milestones/ProjectEditor.tsx'),
    'utf8',
  );
  const review = readFileSync(
    join(SRC_DIR, 'features/markdown-review-export/MarkdownReviewPanel.tsx'),
    'utf8',
  );
  for (const [name, source] of [
    ['MilestoneEditor', milestone],
    ['ProjectEditor', project],
    ['MarkdownReviewPanel', review],
  ] as const) {
    expect(source, `${name} must use the shared helper`).toContain('formatRevisionConflict');
  }
  expect(milestone).not.toContain('changed since you opened it, so your edit');
  expect(project).not.toContain('changed since you opened it, so your edit');
  expect(review).not.toContain('The revision moved while it was being read');
});

test('WEB-FEED-004: capture URL hint and empty-state anchor to the form', async ({ page }) => {
  await mockApi(page, [
    ...sessionMocks(),
    { method: 'GET', path: '/api/projects/p1', json: PROJECT_P1 },
    { method: 'GET', path: '/api/projects/p1/milestones', json: { items: [MILESTONE_M1], nextCursor: null } },
    {
      method: 'GET',
      path: '/api/milestones/m1/evidence',
      json: { milestoneId: 'm1', revision: 2, items: [], nextCursor: null },
    },
    { method: 'GET', path: '/api/milestones/m1/screenshots', json: { items: [], mediaBaseUrl: '/api/media' } },
  ]);
  await page.goto(`${origin}/projects/p1/evolution`);

  await expect(page.getByTestId('capture-empty')).toBeVisible();
  await expect(page.getByText('at least 1 required')).toBeVisible();
  const anchor = page.getByTestId('capture-empty-link');
  await expect(anchor).toHaveAttribute('href', '#capture-form');
  await anchor.click();
  await expect(page.getByTestId('capture-form')).toBeInViewport();

  const viewports = page.getByTestId('capture-viewports');
  await expect(viewports.locator('legend')).toHaveText('Viewports');
});

test('WEB-FEED-005: client counters update on typing and save toasts', async ({ page }) => {
  const created = { id: 'c1', name: 'Acme', notes: 'Retainer.', revision: 1, contextRevision: 1, archivedAt: null };
  await mockApi(page, [
    ...sessionMocks(),
    { method: 'GET', path: '/api/clients', json: { items: [], nextCursor: null } },
    { method: 'POST', path: '/api/clients', json: created },
    { method: 'GET', path: '/api/clients/c1', json: created },
  ]);
  await page.goto(`${origin}/clients`);

  await page.getByTestId('clients-create-first').click();
  await expect(page.getByTestId('client-form-name-count')).toHaveText('0 / 160');
  await page.getByTestId('client-form-name').fill('Acme');
  await expect(page.getByTestId('client-form-name-count')).toHaveText('4 / 160');
  await expect(page.getByTestId('client-form-notes-count')).toHaveText('0 / 20000');
  await page.getByTestId('client-form-notes').fill('Retainer.');
  await expect(page.getByTestId('client-form-notes-count')).toHaveText('9 / 20000');

  await page.getByTestId('client-form-save').click();
  await expect(page.getByTestId('toast')).toContainText('Client saved.');
});

test('WEB-FEED-005: provider rows group under the provider name', async ({ page }) => {
  const providers = [
    'github',
    'github-webhook',
    'openai',
    'gemini',
    'anthropic',
    'openrouter',
    'ollama',
    'publisher',
  ] as const;
  const labels: Record<(typeof providers)[number], string> = {
    github: 'GitHub',
    'github-webhook': 'GitHub webhook secret',
    openai: 'OpenAI',
    gemini: 'Google Gemini',
    anthropic: 'Anthropic',
    openrouter: 'OpenRouter',
    ollama: 'Ollama',
    publisher: 'Publisher',
  };
  await mockApi(page, [
    ...sessionMocks(),
    ...providers.map(
      (provider): MockEntry => ({
        method: 'GET',
        path: `/api/settings/credentials/${provider}`,
        json: { id: null, provider, configured: false, updatedAt: null },
      }),
    ),
  ]);
  await page.goto(`${origin}/settings/credentials`);

  for (const provider of providers) {
    const row = page.getByTestId(`credential-${provider}`);
    await expect(row).toBeVisible();
    const fieldset = row.locator('fieldset');
    await expect(fieldset).toHaveCount(1);
    await expect(fieldset.locator('legend').first()).toHaveText(labels[provider]);
  }
});
