import { expect, test, type Page } from '@playwright/test';

/**
 * WORKFLOW-WAYFINDING-001..006 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API: this installation starts with one project, one
 * confirmed milestone, one manual evidence bundle, one directly-seeded GitHub
 * connection and two directly-seeded GitHub candidates — the seed writes
 * through the product's own routes wherever a browser could (project,
 * milestone, evidence) and inserts only the rows no browser can produce
 * (a connection row needs no transport to exist; a candidate row is produced
 * by a signed delivery a page cannot send). Suite 12 covers the real
 * transport connect; what this suite covers is the panels and the routes.
 *
 * What this suite claims, and what it does not. A pass is evidence that
 * every next-step and cross-link lands on its real area route; that a
 * candidate row records a candidate-status milestone through the delivered
 * import route (and replays without duplicating); that the schedule form
 * saves and is refused in the server's own words; that one capture
 * submission carries up to three pages the server judges (on this host the
 * job then fails `capture_sandbox_unavailable`, which the panel must name);
 * that Team/Tenancy stay reachable through the dashboard crumbs and
 * unlisted on Settings, per the recorded owner decision; and that nothing
 * fires before its explicit control. It is NOT evidence that a page was
 * captured, that GitHub was reached, or that anything was sent anywhere.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** The milestone the seed recorded, by the title it created it with. */
const SEEDED_MILESTONE = 'Wayfinding milestone';

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

test('next-step footers walk the loop and row links cross between Evidence and Evolution', async ({
  page,
}) => {
  await signIn(page);
  const projectId = await openProjectHolding(page, SEEDED_MILESTONE);
  const fired = armPostCounter(page);

  // Overview → Evidence, in workflow order, on a real route.
  await page.getByTestId('workflow-next-overview').getByTestId('workflow-next-link').click();
  await expect(page.getByTestId('project-area-evidence')).toBeVisible();

  // An Evidence row reaches the Evolution area where its pages are photographed.
  await row(page, SEEDED_MILESTONE).getByTestId('workflow-evidence-to-evolution').click();
  await expect(page.getByTestId('project-area-evolution')).toBeVisible();

  // Evolution footer → Content; back to Evidence, then Evolution, asserting each
  // landing — a navigation the suite does not witness is a navigation it cannot reason about.
  await page.getByTestId('workflow-next-evolution').getByTestId('workflow-next-link').click();
  await expect(page.getByTestId('project-area-content')).toBeVisible();
  await page.getByTestId('project-area-evidence').click();
  await expect(page.getByTestId('project-area-evidence')).toBeVisible();
  await expect(row(page, SEEDED_MILESTONE)).toBeVisible();
  await page.getByTestId('project-area-evolution').click();
  await expect(page.getByTestId('project-area-evolution')).toBeVisible();
  await expect(row(page, SEEDED_MILESTONE)).toBeVisible();
  await row(page, SEEDED_MILESTONE).getByTestId('workflow-evolution-to-evidence').click();
  await expect(page.getByTestId('project-area-evidence')).toBeVisible();

  // Evidence footer → Evolution; Content footer → Publish; Publish footer → Overview.
  await page.getByTestId('workflow-next-evidence').getByTestId('workflow-next-link').click();
  await expect(page.getByTestId('project-area-evolution')).toBeVisible();
  await page.getByTestId('project-area-content').click();
  await page.getByTestId('workflow-next-content').getByTestId('workflow-next-link').click();
  await expect(page.getByTestId('project-area-publish')).toBeVisible();
  await page.getByTestId('workflow-next-publish').getByTestId('workflow-next-link').click();
  await expect(page.getByTestId('project-area-overview')).toBeVisible();

  // Navigation fires no request: the loop is links, not mutations.
  expect(fired.posts).toEqual([]);
  expect(projectId).toMatch(/^[0-9a-f-]{36}$/);
});

test('a candidate row records a candidate-status milestone and replays without duplicating', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_MILESTONE);
  await page.getByTestId('project-area-evidence').click();

  const panel = page.getByTestId('github-candidate-panel');
  await expect(panel.getByTestId('github-candidate-row').first()).toBeVisible();

  const fired = armImportCounter(page);
  expect(fired.imports).toBe(0);

  const firstRow = panel.getByTestId('github-candidate-row').first();
  const title = (await firstRow.getByTestId('github-candidate-title').textContent()) ?? '';

  const [importResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/projects\/[^/]+\/milestones\/import$/.test(new URL(response.url()).pathname),
    ),
    firstRow.getByTestId('github-candidate-import').click(),
  ]);

  expect(importResponse.status()).toBe(201);
  expect(fired.imports).toBe(1);

  // The proposal became a record that still needs a human decision — and the notice says
  // where that decision is made.
  const outcome = firstRow.getByTestId('github-candidate-imported');
  await expect(outcome).toBeVisible();
  await expect(outcome).toContainText('as a candidate milestone');
  await expect(outcome).toContainText(title.trim());

  // The new row reads in the milestone list as a candidate: importing proposed, it did not confirm.
  await outcome.getByTestId('github-candidate-import-overview').click();
  await expect(page.getByTestId('project-area-overview')).toBeVisible();
  const imported = row(page, title.trim());
  await expect(imported).toBeVisible();
  await expect(imported.getByTestId('milestone-status')).toHaveText('candidate');

  // A second press replays the stored row instead of duplicating it.
  await page.getByTestId('project-area-evidence').click();
  await expect(panel.getByTestId('github-candidate-row').first()).toBeVisible();
  const [replayResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/projects\/[^/]+\/milestones\/import$/.test(new URL(response.url()).pathname),
    ),
    panel.getByTestId('github-candidate-row').first().getByTestId('github-candidate-import').click(),
  ]);

  expect(replayResponse.status()).toBe(200);
  expect(fired.imports).toBe(2);
  await expect(
    panel.getByTestId('github-candidate-row').first().getByTestId('github-candidate-imported'),
  ).toContainText('Already recorded');
});

test('the schedule form saves the cadence and is refused in the server words', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_MILESTONE);
  await page.getByTestId('project-area-automations').click();

  const section = page.getByTestId('automations-schedule');
  await expect(section.getByTestId('automations-schedule-empty')).toBeVisible();

  const fired = armScheduleCounter(page);
  expect(fired.saves).toBe(0);

  // Create at If-Match zero through the delivered PUT: the stored cadence reads back.
  await section.getByTestId('automations-schedule-interval-input').fill('60');
  await section.getByTestId('automations-schedule-save').click();
  await expect(section.getByTestId('automations-schedule-notice')).toContainText(
    'every 60 minutes',
  );
  await expect(section.getByTestId('automations-schedule-interval')).toContainText(
    'Every 60 minutes',
  );
  expect(fired.saves).toBe(1);

  // Below the minimum the server refuses, in its own words — and nothing is stored.
  await section.getByTestId('automations-schedule-interval-input').fill('14');
  await section.getByTestId('automations-schedule-save').click();
  const refusal = section.getByTestId('automations-schedule-error');
  await expect(refusal).toBeVisible();
  await expect(refusal).toContainText('validation_failed');
  expect(fired.saves).toBe(2);
});

test('one capture submission carries two pages the server judges as one job', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_MILESTONE);
  await page.getByTestId('project-area-evolution').click();

  const panel = row(page, SEEDED_MILESTONE).getByTestId('capture-panel');
  await expect(panel.getByTestId('capture-url-2')).toBeVisible();
  await expect(panel.getByTestId('capture-url-3')).toBeVisible();

  // The form promises up to three pages now — and means it.
  await expect(panel.getByTestId('capture-form-hint')).toContainText('Up to 3 pages');

  const bodies: unknown[] = [];
  page.on('request', (request) => {
    const path = new URL(request.url()).pathname;

    if (request.method() === 'POST' && /\/api\/milestones\/[^/]+\/captures$/.test(path)) {
      bodies.push(request.postDataJSON());
    }
  });

  await panel.getByTestId('capture-url').fill('https://example.com/pricing');
  await panel.getByTestId('capture-url-2').fill('https://example.com/tour');

  const [accepted] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/milestones\/[^/]+\/captures$/.test(new URL(response.url()).pathname),
    ),
    panel.getByTestId('capture-submit').click(),
  ]);

  // One submission, one request, both pages in it.
  expect(accepted.status()).toBe(202);
  expect(bodies).toHaveLength(1);
  expect((bodies[0] as { urls: unknown }).urls).toEqual([
    'https://example.com/pricing',
    'https://example.com/tour',
  ]);

  // The 202 is work accepted, no picture — and on this host the job then fails with the
  // sandbox refusal the panel must name, proving the two-page request reached the server.
  await expect(panel.getByTestId('capture-job-running')).toBeVisible();
  const refusal = panel.getByTestId('capture-sandbox-unavailable');
  await expect(refusal).toBeVisible({ timeout: 30_000 });
  await expect(refusal).toContainText('capture_sandbox_unavailable');
});

test('team and tenancy stay on the dashboard crumbs and off Settings', async ({ page }) => {
  await signIn(page);

  // The owner decision, pinned: Settings lists session internals plus the credentials
  // link only — no team surface, no tenancy surface.
  await page.goto('/settings');
  await expect(page.getByTestId('settings-heading')).toBeVisible();
  await expect(page.getByTestId('settings-team-link')).toHaveCount(0);
  await expect(page.getByTestId('settings-tenancy-link')).toHaveCount(0);

  // The discoverability path that decision keeps: the dashboard crumbs.
  await page.goto('/');
  await expect(page.getByTestId('settings-team-link')).toBeVisible();
  await page.getByTestId('settings-team-link').click();
  await expect(page.getByTestId('team-panel')).toBeVisible();
  await expect(page.getByTestId('team-heading')).toContainText('Team invitations and members');

  await page.goto('/');
  await expect(page.getByTestId('settings-tenancy-link')).toBeVisible();
  await page.getByTestId('settings-tenancy-link').click();
  await expect(page.getByTestId('tenancy-panel')).toBeVisible();
  await expect(page.getByTestId('tenancy-heading')).toContainText('Hosted tenancy');

  // The candidate panel names Automations too — and means the real route.
  await openProjectHolding(page, SEEDED_MILESTONE);
  await page.getByTestId('project-area-evidence').click();
  const candidatePanel = page.getByTestId('github-candidate-panel');
  await expect(candidatePanel.getByTestId('github-candidate-automations-link')).toBeVisible();
  await candidatePanel.getByTestId('github-candidate-automations-link').click();
  await expect(page.getByTestId('automations-view')).toBeVisible();
});

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/**
 * Opens the project the milestone with this title lives in and returns its id.
 * A fixture that opened the wrong project would assert about a record it never looked at.
 */
async function openProjectHolding(page: Page, title: string): Promise<string> {
  const found = await locateMilestone(page, title);

  if (found === null) {
    throw new Error(`no milestone titled "${title}" exists in this installation`);
  }

  await page.goto(`/projects/${found.projectId}`);
  await expect(page.getByTestId('project-name')).toBeVisible();
  await expect(row(page, title)).toBeVisible();

  return found.projectId;
}

/** The milestone row with this exact title, so two milestones cannot be confused. */
function row(page: Page, title: string) {
  return page
    .getByTestId('milestone')
    .filter({ has: page.getByTestId('milestone-title').filter({ hasText: title }) })
    .first();
}

async function locateMilestone(
  page: Page,
  title: string,
): Promise<{ projectId: string; milestoneId: string } | null> {
  return page.evaluate(async (wanted) => {
    const projects = await fetch('/api/projects', { credentials: 'same-origin' });

    if (!projects.ok) {
      return null;
    }

    const catalogue = (await projects.json()) as { readonly items: readonly { readonly id: string }[] };

    for (const project of catalogue.items) {
      const response = await fetch(`/api/projects/${project.id}/milestones`, {
        credentials: 'same-origin',
      });

      if (!response.ok) {
        continue;
      }

      const listing = (await response.json()) as {
        readonly items: readonly { readonly id: string; readonly title: string }[];
      };

      for (const milestone of listing.items) {
        if (milestone.title === wanted) {
          return { projectId: project.id, milestoneId: milestone.id };
        }
      }
    }

    return null;
  }, title);
}

/** Every POST the page issues from here on. Navigation must fire none. */
function armPostCounter(page: Page): { posts: string[] } {
  const posts: string[] = [];

  page.on('request', (request) => {
    if (request.method() === 'POST') {
      posts.push(new URL(request.url()).pathname);
    }
  });

  return { posts };
}

/** Counts the import-bound POSTs the page issues from here on. */
function armImportCounter(page: Page): { imports: number } {
  const fired = { imports: 0 };

  page.on('request', (request) => {
    if (
      request.method() === 'POST' &&
      /\/api\/projects\/[^/]+\/milestones\/import$/.test(new URL(request.url()).pathname)
    ) {
      fired.imports += 1;
    }
  });

  return fired;
}

/** Counts the schedule-bound PUTs the page issues from here on. */
function armScheduleCounter(page: Page): { saves: number } {
  const fired = { saves: 0 };

  page.on('request', (request) => {
    if (
      request.method() === 'PUT' &&
      /\/api\/projects\/[^/]+\/sync-schedule$/.test(new URL(request.url()).pathname)
    ) {
      fired.saves += 1;
    }
  });

  return fired;
}
