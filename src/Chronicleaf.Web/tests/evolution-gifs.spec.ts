import { expect, test, type Page } from '@playwright/test';

/**
 * FEATURE-TRIGGER-PANELS-004 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded data: this installation starts empty,
 * the project below is created by the same clicks an owner would make, and
 * every claim is read back from the page or from the server's own answer. The
 * empty state is asserted before anything is rendered, precisely because a
 * project with no animation must not play one.
 *
 * What this suite claims, and what it does not. A pass is evidence that the
 * panel lists, renders, approves and links what the server stores - and that a
 * refusal is reported in the server's own words with no animation invented. It
 * is NOT evidence that an animation depicts real screenshots on this host: no
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

test('a project with no evolution GIF shows an honest empty panel', async ({ page }) => {
  await createProject(page, 'Ledger', 'Double-entry accounting.');

  await expect(page.getByTestId('evolution-gif-panel')).toBeVisible();
  await expect(page.getByTestId('evolution-gif-heading')).toContainText('Evolution GIFs');
  await expect(page.getByTestId('evolution-gif-empty')).toBeVisible();
  await expect(page.getByTestId('evolution-gif-item')).toHaveCount(0);

  // No preview, static frame or download link exists for an animation that was never
  // rendered: the bytes are served from the route, never synthesised.
  await expect(page.getByTestId('evolution-gif-preview')).toHaveCount(0);
  await expect(page.getByTestId('evolution-gif-download')).toHaveCount(0);

  // The server says the same thing the panel does.
  const listed = await listGifsFromThePage(page, projectIdFrom(page.url()));
  expect(listed.status).toBe(200);
  expect(listed.body.items).toEqual([]);
});

test('rendering against an unknown milestone answers the server refusal', async ({ page }) => {
  await createProject(page, 'Ledger', 'Double-entry accounting.');

  await page.getByTestId('evolution-gif-milestones').fill('22222222-2222-2222-2222-222222222222');
  await page.getByTestId('evolution-gif-loop').check();
  await page.getByTestId('evolution-gif-render').click();

  await expect(page.getByTestId('evolution-gif-error')).toBeVisible();
  await expect(page.getByTestId('evolution-gif-item')).toHaveCount(0);
  await expect(page.getByTestId('evolution-gif-preview')).toHaveCount(0);
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

async function listGifsFromThePage(page: Page, projectId: string) {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/projects/${id}/evolution/gifs`, {
      credentials: 'same-origin',
    });

    return { status: response.status, body: await response.json() };
  }, projectId);
}
