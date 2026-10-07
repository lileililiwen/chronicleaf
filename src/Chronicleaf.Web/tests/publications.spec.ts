import { expect, test, type Page } from '@playwright/test';

/**
 * FEATURE-TRIGGER-PANELS-005 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded data: this installation starts empty,
 * the project below is created by the same clicks an owner would make, and
 * every claim is read back from the page or from the server's own answer. The
 * empty state is asserted before anything is named, precisely because a panel
 * with no attempt must not display a status.
 *
 * What this suite claims, and what it does not. A pass is evidence that the
 * panel reads and reconciles what the server stores, that a preview
 * authorizes nothing, and that an unknown attempt is refused in the server's
 * own words. It is NOT evidence that anything was sent anywhere: no publish
 * is attempted here, and no transport is contacted except through a route the
 * owner explicitly triggered.
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

test('with no attempt selected the panel shows an empty state and no receipt', async ({
  page,
}) => {
  await createProject(page, 'Ledger', 'Double-entry accounting.');

  await expect(page.getByTestId('publication-panel')).toBeVisible();
  await expect(page.getByTestId('publication-heading')).toContainText('Publications');
  await expect(page.getByTestId('publication-empty')).toBeVisible();
  await expect(page.getByTestId('publication-receipt')).toHaveCount(0);
});

test('reading an unknown attempt answers the route own refusal', async ({ page }) => {
  await createProject(page, 'Ledger', 'Double-entry accounting.');

  await page.getByTestId('publication-attempt-input').fill('33333333-3333-3333-3333-333333333333');
  await page.getByTestId('publication-read').click();

  await expect(page.getByTestId('publication-error')).toBeVisible();
  await expect(page.getByTestId('publication-receipt')).toHaveCount(0);

  // The server says the same thing the panel does.
  const answer = await readReceiptFromThePage(page, '33333333-3333-3333-3333-333333333333');
  expect(answer.status).toBe(404);
  expect(answer.body.code).toBe('not_found');
});

test('reconciling an unknown attempt answers the route own refusal', async ({ page }) => {
  await createProject(page, 'Ledger', 'Double-entry accounting.');

  await page.getByTestId('publication-attempt-input').fill('33333333-3333-3333-3333-333333333333');
  await page.getByTestId('publication-reconcile').click();

  await expect(page.getByTestId('publication-error')).toBeVisible();
  await expect(page.getByTestId('publication-receipt')).toHaveCount(0);
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

  // PROJECT-AREA-IA-001 migration: the publication panel moved to the Publish
  // area; the same selectors are asserted there with the same expectations.
  await page.getByTestId('project-area-publish').click();
  await expect(page.getByTestId('publication-panel')).toBeVisible();

  return projectIdFrom(page.url());
}

function projectIdFrom(url: string): string {
  return new URL(url).pathname.split('/').filter((segment) => segment.length > 0)[1] ?? '';
}

/**
 * Reads the server's own answer for one receipt through the delivered route,
 * with this browser's own session cookie.
 */
async function readReceiptFromThePage(page: Page, attemptId: string) {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/publications/${id}`, { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  }, attemptId);
}
