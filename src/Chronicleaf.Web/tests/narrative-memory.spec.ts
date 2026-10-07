import { expect, test, type Page } from '@playwright/test';

/**
 * NARRATIVE-MEMORY-001 and NARRATIVE-MEMORY-002 in a real browser, against the real
 * published application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded data: this installation starts empty, every project
 * and every narrative entry below is created by the same clicks an owner would make, and every
 * claim is read back from the page. The empty state is asserted before anything is recorded,
 * precisely because a project with no narrative history must not display history it does not
 * have - and a generated summary is asserted as a different fact from the owner's words, because
 * a reader must never mistake one for the other.
 *
 * The refresh runs in the delivered queue on the published server, not in the browser: the
 * suite clicks the panel's refresh control and then reads the operation status the panel itself
 * reports, exactly as an owner would, and the server agrees through its own read route.
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

test('a project with no narrative history shows honest empty states', async ({ page }) => {
  const projectId = await createProject(page, 'Ledger', 'Double-entry accounting.');

  await expect(page.getByTestId('narrative-memory')).toBeVisible();
  await expect(page.getByTestId('narrative-entries')).toContainText(
    'No narrative history has been recorded for this project.',
  );
  await expect(page.getByTestId('narrative-summary')).toContainText(
    'No summary has been generated for this project.',
  );

  // The server says the same thing the panel does.
  const narrative = await readNarrativeFromThePage(page, projectId);
  expect(narrative.status).toBe(200);
  expect(narrative.body.entries).toEqual([]);
  expect(narrative.body.summary).toBeNull();
});

test('a recorded entry is attributed history and a refresh concludes a summary', async ({
  page,
}) => {
  const projectId = await createProject(page, 'Ledger', 'Double-entry accounting.');

  await page.getByTestId('narrative-kind').selectOption('origin');
  await page
    .getByTestId('narrative-text')
    .fill('Ledger began as a single-file SQLite prototype on a laptop.');
  await page.getByTestId('narrative-visibility').selectOption('exportable');
  await page.getByTestId('narrative-save').click();

  const entry = page.getByTestId('narrative-entry-text');
  await expect(entry).toHaveCount(1);
  await expect(entry).toHaveText('Ledger began as a single-file SQLite prototype on a laptop.');
  await expect(page.getByTestId('narrative-entry-kind')).toHaveText('origin');

  // A refresh is a job, not an answer: the panel follows it until the queue settles it.
  await page.getByTestId('narrative-refresh').click();
  await expect(page.getByTestId('narrative-refresh-status')).toHaveText('succeeded', {
    timeout: 90_000,
  });

  await expect(page.getByTestId('narrative-summary-revision')).toHaveText('Revision 1');
  await expect(page.getByTestId('narrative-summary-stale')).toHaveCount(0);

  // The stored summary carries the owner's words as attributed history rather than as a new
  // project: the entry text and the unknown-feedback label are both in the body it serves.
  const narrative = await readNarrativeFromThePage(page, projectId);
  expect(narrative.status).toBe(200);
  expect(narrative.body.entries).toHaveLength(1);
  expect(narrative.body.summary).not.toBeNull();
  expect(narrative.body.summary.revision).toBe(1);
  expect(narrative.body.summary.stale).toBe(false);
  expect(narrative.body.summary.bodyJson).toContain('SQLite prototype');
  expect(narrative.body.summary.bodyJson).toMatch(/unknown/i);
});

test('an edit marks the summary stale until the next refresh', async ({ page }) => {
  const projectId = await createProject(page, 'Ledger', 'Double-entry accounting.');

  await page.getByTestId('narrative-kind').selectOption('decision');
  await page.getByTestId('narrative-text').fill('Ledger routes through the first router.');
  await page.getByTestId('narrative-visibility').selectOption('exportable');
  await page.getByTestId('narrative-save').click();
  await expect(page.getByTestId('narrative-entry-text')).toHaveCount(1);

  await page.getByTestId('narrative-refresh').click();
  await expect(page.getByTestId('narrative-refresh-status')).toHaveText('succeeded', {
    timeout: 90_000,
  });
  await expect(page.getByTestId('narrative-summary-revision')).toHaveText('Revision 1');

  // The edit names the revision it read: the panel sends it, the server advances it, and the
  // sources move under the stored body - so the prior answer reads stale rather than current.
  await page.getByTestId('narrative-edit-start').click();
  await page
    .getByTestId('narrative-edit-text')
    .fill('Ledger routes through the second router.');
  await expect(page.getByTestId('narrative-edit-save')).toContainText('Save revision 1');
  await page.getByTestId('narrative-edit-save').click();
  await expect(page.getByTestId('narrative-entry-text')).toHaveText(
    'Ledger routes through the second router.',
  );
  await expect(page.getByTestId('narrative-summary-stale')).toBeVisible();

  const stale = await readNarrativeFromThePage(page, projectId);
  expect(stale.body.summary.revision).toBe(1);
  expect(stale.body.summary.stale).toBe(true);
  expect(stale.body.summary.bodyJson).toContain('first router');

  await page.getByTestId('narrative-refresh').click();
  await expect(page.getByTestId('narrative-refresh-status')).toHaveText('succeeded', {
    timeout: 90_000,
  });
  await expect(page.getByTestId('narrative-summary-revision')).toHaveText('Revision 2');
  await expect(page.getByTestId('narrative-summary-stale')).toHaveCount(0);

  const renewed = await readNarrativeFromThePage(page, projectId);
  expect(renewed.body.summary.revision).toBe(2);
  expect(renewed.body.summary.stale).toBe(false);
  expect(renewed.body.summary.bodyJson).toContain('second router');
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

  // PROJECT-AREA-IA-001 migration: the narrative panel moved to the Content
  // area; the same selectors are asserted there with the same expectations.
  await page.getByTestId('project-area-content').click();
  await expect(page.getByTestId('narrative-memory')).toBeVisible();

  return projectIdFrom(page.url());
}

function projectIdFrom(url: string): string {
  return new URL(url).pathname.split('/').filter((segment) => segment.length > 0)[1] ?? '';
}

async function readNarrativeFromThePage(page: Page, projectId: string) {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/projects/${id}/narrative`, {
      credentials: 'same-origin',
    });

    return { status: response.status, body: await response.json() };
  }, projectId);
}
