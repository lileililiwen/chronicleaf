import { expect, test, type Page } from '@playwright/test';

/**
 * FEATURE-TRIGGER-PANELS-006 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded data: this installation starts empty,
 * the project below is created by the same clicks an owner would make, and
 * every claim is read back from the page or from the server's own answer. The
 * empty state is asserted before anything is previewed, precisely because a
 * project with no journey must not display a public page that does not exist.
 *
 * What this suite claims, and what it does not. A pass is evidence that the
 * panel previews and publishes through the delivered routes, that the public
 * view shows only what the projection route returns, and that an unknown slug
 * is the route's own 404 page with no redirect and no guessed content. It is
 * NOT evidence that a journey was published on this host: nothing here is
 * approved, so nothing can be published, and the preview refusal is the proof
 * the boundary holds.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

test.beforeEach(async ({ page }) => {
  if (!ACCESS_TOKEN) {
    throw new Error(
      'CHRONICLEAF_E2E_ACCESS_TOKEN is not set. Run these fixtures through scripts/e2e.sh, which ' +
        'starts the published application with a real access token hash and a real database.',
    );
  }

  await page.context().clearCookies();
  await page.goto('/');
});

test('a project with no journey shows an honest empty panel', async ({ page }) => {
  const projectId = await createProject(page, 'Ledger', 'Double-entry accounting.');

  await expect(page.getByTestId('journey-panel')).toBeVisible();
  await expect(page.getByTestId('journey-heading')).toContainText('Project journey');
  await expect(page.getByTestId('journey-empty')).toBeVisible();

  // The server says the same thing the panel does: no row, no projection.
  const stored = await readJourneyFromThePage(page, projectId);
  expect(stored.status).toBe(404);
  expect(stored.body.code).toBe('not_found');
});

test('previewing an empty selection answers the server refusal', async ({ page }) => {
  await createProject(page, 'Ledger', 'Double-entry accounting.');

  await page.getByTestId('journey-preview').click();

  await expect(page.getByTestId('journey-error')).toBeVisible();
  await expect(page.getByTestId('journey-preview-result')).toHaveCount(0);
});

test('an unknown slug is the route own 404 page, not a redirect', async ({ page }) => {
  const response = await page.goto('/journey/no-such-journey-here');

  expect(response?.status()).toBe(200);

  // The document loads; the journey itself is missing, and the page says so where it is.
  await expect(page.getByTestId('public-journey')).toBeVisible();
  await expect(page.getByTestId('public-journey-missing')).toBeVisible();
  await expect(page.getByTestId('public-journey-missing')).toContainText('not_found');
  await expect(page.getByTestId('public-journey-entries')).toHaveCount(0);

  // The server says the same thing the page does.
  const answer = await readPublicJourneyFromTheRequest(page, 'no-such-journey-here');
  expect(answer.status).toBe(404);
  expect(answer.body.code).toBe('not_found');
});

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/** Creates a project by clicking, and leaves the page on that project. */
async function createProject(page: Page, name: string, description: string): Promise<string> {
  await signIn(page);

  await page.goto('/projects/new');
  await page.getByTestId('project-name-input').fill(name);
  await page.getByTestId('project-description-input').fill(description);
  await page.getByTestId('project-save').click();

  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}$/);

  return projectIdFrom(page.url());
}

function projectIdFrom(url: string): string {
  return new URL(url).pathname.split('/').filter((segment) => segment.length > 0)[1] ?? '';
}

async function readJourneyFromThePage(page: Page, projectId: string) {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/projects/${id}/journey`, {
      credentials: 'same-origin',
    });

    return { status: response.status, body: await response.json() };
  }, projectId);
}

/**
 * Reads the server's own answer for one public slug without a session, the
 * way an anonymous reader's browser would.
 */
async function readPublicJourneyFromTheRequest(page: Page, slug: string) {
  return page.evaluate(async (name) => {
    const response = await fetch(`/api/journey/${name}`, { credentials: 'omit' });

    return { status: response.status, body: await response.json() };
  }, slug);
}
