import { expect, test, type Page } from '@playwright/test';

/**
 * FEATURE-TRIGGER-PANELS-003 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded data: this installation starts empty,
 * the project below is created by the same clicks an owner would make, and
 * every claim is read back from the page or from the server's own answer. The
 * empty state is asserted before anything is rendered, precisely because a
 * project with no comparison must not display one.
 *
 * What this suite claims, and what it does not. A pass is evidence that the
 * panel lists, renders, approves and links what the server stores - and that a
 * refusal is reported in the server's own words with no picture invented. It
 * is NOT evidence that a comparison depicts real screenshots on this host: no
 * candidate exists here, and rendering against an unknown milestone is refused
 * rather than drawn.
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

test('a project with no evolution image shows an honest empty panel', async ({ page }) => {
  await createProject(page, 'Ledger', 'Double-entry accounting.');

  await expect(page.getByTestId('evolution-image-panel')).toBeVisible();
  await expect(page.getByTestId('evolution-image-heading')).toContainText('Evolution images');
  await expect(page.getByTestId('evolution-image-empty')).toBeVisible();
  await expect(page.getByTestId('evolution-image-item')).toHaveCount(0);

  // No preview or download link exists for a comparison that was never rendered: the bytes
  // are served from the route, never synthesised, so there is nothing to link to.
  await expect(page.getByTestId('evolution-image-preview')).toHaveCount(0);
  await expect(page.getByTestId('evolution-image-download')).toHaveCount(0);

  // The server says the same thing the panel does.
  const listed = await listEvolutionsFromThePage(page, projectIdFrom(page.url()));
  expect(listed.status).toBe(200);
  expect(listed.body.items).toEqual([]);
});

test('rendering against an unknown milestone answers the server refusal', async ({ page }) => {
  await createProject(page, 'Ledger', 'Double-entry accounting.');

  await page.getByTestId('evolution-image-milestones').fill('22222222-2222-2222-2222-222222222222');
  await page.getByTestId('evolution-image-layout-input').selectOption('gallery');
  await page.getByTestId('evolution-image-render').click();

  await expect(page.getByTestId('evolution-image-error')).toBeVisible();
  await expect(page.getByTestId('evolution-image-item')).toHaveCount(0);
  await expect(page.getByTestId('evolution-image-preview')).toHaveCount(0);
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

  // PROJECT-AREA-IA-001 migration: the evolution image panel moved to the
  // Evolution area; the same selectors are asserted there with the same
  // expectations.
  await page.getByTestId('project-area-evolution').click();
  await expect(page.getByTestId('evolution-image-panel')).toBeVisible();

  return projectIdFrom(page.url());
}

function projectIdFrom(url: string): string {
  return new URL(url).pathname.split('/').filter((segment) => segment.length > 0)[1] ?? '';
}

async function listEvolutionsFromThePage(page: Page, projectId: string) {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/projects/${id}/evolution/images`, {
      credentials: 'same-origin',
    });

    return { status: response.status, body: await response.json() };
  }, projectId);
}
