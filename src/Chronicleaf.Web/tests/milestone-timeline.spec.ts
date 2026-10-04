import { expect, test, type Page } from '@playwright/test';

/**
 * MILESTONE-TIMELINE-001 and MILESTONE-TIMELINE-002 in a real browser, against the real
 * published application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded data: this installation starts empty, every project and
 * milestone below is created by the same clicks an owner would make, and every claim is read back
 * from the page. The empty state is asserted before anything is recorded, precisely because a
 * project with no confirmed history must not display a history it does not have.
 *
 * One step uses a real API call from inside the page rather than a click: importing a candidate.
 * No product surface collects records from a source yet - the intake route is what a later
 * collector will call - so a candidate is created through that route with this browser's own
 * session cookie, and the showing and labeling of it is done through the UI.
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

test('a project with no confirmed history shows an honest empty timeline', async ({ page }) => {
  const projectId = await createProject(page, 'Ledger', 'Double-entry accounting.');

  await expect(page.getByTestId('project-timeline')).toBeVisible();
  await expect(page.getByTestId('timeline-empty')).toBeVisible();
  await expect(page.getByTestId('timeline-empty')).toContainText('No confirmed milestones yet.');
  await expect(page.getByTestId('timeline-entry')).toHaveCount(0);

  // The candidate toggle on an empty history is still an empty history.
  await page.getByTestId('timeline-candidates-toggle').check();
  await expect(page.getByTestId('timeline-empty')).toBeVisible();
  await expect(page.getByTestId('timeline-empty')).toContainText(
    'No confirmed milestones or candidates yet.',
  );

  // The server says the same thing the panel does.
  const timeline = await readTimelineFromThePage(page, projectId, false);
  expect(timeline.status).toBe(200);
  expect(timeline.body.items).toEqual([]);
  expect(timeline.body.nextCursor).toBeNull();
});

test('confirmed milestones read oldest first under their own dates', async ({ page }) => {
  const projectId = await createProject(page, 'Ledger', 'Double-entry accounting.');
  await recordMilestone(page, 'Later work', '2026-09-28T09:30');
  await recordMilestone(page, 'Earlier work', '2026-09-20T09:30');

  const entries = page.getByTestId('timeline-entry');
  await expect(entries).toHaveCount(2);

  // Chronology, not recency: the September 20th record reads first even though it was saved last.
  await expect(entries.nth(0).getByTestId('timeline-title')).toHaveText('Earlier work');
  await expect(entries.nth(1).getByTestId('timeline-title')).toHaveText('Later work');
  await expect(entries.nth(0).getByTestId('timeline-status')).toHaveText('confirmed');

  // Two dates, because the records happened on different days.
  await expect(page.getByTestId('timeline-day')).toHaveCount(2);

  // The instants are kept on the elements as UTC ISO-8601, in ascending order: the API
  // speaks UTC and the dateTime values are what it sent, whatever the browser's zone is.
  const firstTime = entries.nth(0).getByTestId('timeline-time');
  const secondTime = entries.nth(1).getByTestId('timeline-time');
  const firstDateTime = await firstTime.getAttribute('dateTime');
  const secondDateTime = await secondTime.getAttribute('dateTime');
  expect(firstDateTime).toMatch(/2026-09-20T\d{2}:30:00/);
  expect(secondDateTime).toMatch(/2026-09-28T\d{2}:30:00/);
  expect(firstDateTime! < secondDateTime!).toBe(true);

  const timeline = await readTimelineFromThePage(page, projectId, false);
  expect(timeline.status).toBe(200);
  expect(timeline.body.items.map((item: { title: string }) => item.title)).toEqual([
    'Earlier work',
    'Later work',
  ]);

  // A backdated edit moves its row on the next read: the September 28th record becomes September 10th.
  await entries.nth(1).getByTestId('timeline-open').click();
  await expect(page.getByTestId('milestone-edit')).toBeVisible();
  await page.getByTestId('milestone-occurred-at-input').fill('2026-09-10T09:30');
  await page.getByTestId('milestone-save').click();
  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}$/);

  const moved = page.getByTestId('timeline-entry');
  await expect(moved).toHaveCount(2);
  await expect(moved.nth(0).getByTestId('timeline-title')).toHaveText('Later work');
  await expect(moved.nth(1).getByTestId('timeline-title')).toHaveText('Earlier work');
});

test('candidates stay hidden until asked for, and a row opens its milestone editor', async ({
  page,
}) => {
  const projectId = await createProject(page, 'Ledger', 'Double-entry accounting.');
  await recordMilestone(page, 'Shipped', '2026-09-28T09:30');

  const imported = await importCandidateFromThePage(page, projectId, 'github', 'release/v2.0.0', 'Release candidate');
  expect(imported.status).toBe(201);

  // The default view is the confirmed history: the candidate is not in it.
  await expect(page.getByTestId('timeline-entry')).toHaveCount(1);
  await expect(page.getByTestId('timeline-entry').getByTestId('timeline-title')).toHaveText('Shipped');

  await page.getByTestId('timeline-candidates-toggle').check();

  const entries = page.getByTestId('timeline-entry');
  await expect(entries).toHaveCount(2);
  await expect(entries.nth(0).getByTestId('timeline-title')).toHaveText('Shipped');
  await expect(entries.nth(1).getByTestId('timeline-title')).toHaveText('Release candidate');
  await expect(entries.nth(1).getByTestId('timeline-status')).toHaveText('candidate');

  // Selecting the candidate opens the milestone editor for that record, not a new one.
  await entries.nth(1).getByTestId('timeline-open').click();
  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}\/milestones\/[0-9a-f-]{36}$/);
  await expect(page.getByTestId('milestone-edit')).toBeVisible();
  await expect(page.getByTestId('milestone-title-input')).toHaveValue('Release candidate');
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

/** Records one confirmed milestone by clicking, and leaves the page on the project. */
async function recordMilestone(page: Page, title: string, occurredAt: string) {
  await page.getByTestId('milestone-new').click();
  await page.getByTestId('milestone-title-input').fill(title);
  await page.getByTestId('milestone-type-input').selectOption('feature');
  await page.getByTestId('milestone-occurred-at-input').fill(occurredAt);
  await page.getByTestId('milestone-save').click();

  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}$/);
}

function projectIdFrom(url: string): string {
  return new URL(url).pathname.split('/').filter((segment) => segment.length > 0)[1] ?? '';
}

async function readTimelineFromThePage(page: Page, projectId: string, includeCandidates: boolean) {
  return page.evaluate(async ([id, candidates]) => {
    const query = candidates ? '?includeCandidates=true' : '';

    const response = await fetch(`/api/projects/${id}/timeline${query}`, {
      credentials: 'same-origin',
    });

    return { status: response.status, body: await response.json() };
  }, [projectId, includeCandidates] as const);
}

/**
 * Hands one upstream record to the intake route, with this browser's own session cookie and a fresh
 * antiforgery value - the same call a collector in a later package will make.
 */
async function importCandidateFromThePage(
  page: Page,
  projectId: string,
  source: string,
  sourceKey: string,
  title: string,
) {
  return page.evaluate(
    async ([id, src, key, recordTitle]) => {
      const metadata = await (
        await fetch('/api/session/antiforgery', { credentials: 'same-origin' })
      ).json();

      const response = await fetch(`/api/projects/${id}/milestones/import`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', [metadata.headerName]: metadata.token },
        body: JSON.stringify({
          source: src,
          candidates: [
            {
              sourceKey: key,
              title: recordTitle,
              occurredAt: new Date().toISOString(),
              type: 'release',
            },
          ],
        }),
      });

      return { status: response.status, body: await response.json() };
    },
    [projectId, source, sourceKey, title],
  );
}
