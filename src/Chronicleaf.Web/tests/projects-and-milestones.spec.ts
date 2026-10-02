import { expect, test, type Page } from '@playwright/test';

/**
 * PROJECTS-AND-MILESTONES-001 and PROJECTS-AND-MILESTONES-002 in a real browser, against the real
 * published application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded data: this installation starts with an empty project list
 * because nothing has been recorded in it, every record below is created by the same clicks an owner
 * would make, and every claim is read back from the page or from the server's own answer. The empty
 * state is asserted before anything is created, precisely because an empty installation must not
 * display a history it does not have.
 *
 * One step uses a real API call from inside the page rather than a click: importing a candidate. No
 * product surface collects records from a source yet, and the design puts that in a later package -
 * so a candidate is created through the same intake route such a package would call, with this
 * browser's own session cookie, and the confirming and dismissing of it is done through the UI.
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

test('a new installation shows an honest empty dashboard and no invented history', async ({ page }) => {
  await signIn(page);

  const empty = page.getByTestId('dashboard-empty');
  await expect(empty).toBeVisible();
  await expect(empty).toContainText('No projects yet.');

  // There is no project, and therefore no milestone, anywhere in the view. Nothing is standing in
  // for a history the owner has not written.
  await expect(page.getByTestId('dashboard-projects')).toHaveCount(0);
  await expect(page.getByTestId('milestone')).toHaveCount(0);
  await expect(empty.getByTestId('dashboard-create-project')).toBeVisible();

  // The same truth from the other list.
  await page.getByTestId('dashboard-create-project').click();
  await expect(page).toHaveURL(/\/projects\/new$/);
  await page.getByTestId('project-cancel').click();

  await expect(page.getByTestId('projects-empty')).toBeVisible();
});

test('the owner records a project and a milestone, and both are persisted', async ({ page }) => {
  await signIn(page);

  // A project, created by clicking.
  await page.getByTestId('dashboard-create-project').click();
  await page.getByTestId('project-name-input').fill('Ledger');
  await page.getByTestId('project-description-input').fill('Double-entry accounting.');
  await page.getByTestId('project-save').click();

  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}$/);
  await expect(page.getByTestId('project-name')).toHaveText('Ledger');
  const projectId = projectIdFrom(page.url());

  // The empty project is honest about having no milestones, and offers the one action that changes it.
  await expect(page.getByTestId('milestones-empty')).toBeVisible();
  await expect(page.getByTestId('milestones-empty')).toContainText('No milestones recorded yet.');

  // A milestone the owner typed, with the fields a later story is written from.
  await page.getByTestId('milestone-new').click();
  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}\/milestones\/new$/);

  await page.getByTestId('milestone-title-input').fill('First import of the ledger');
  await page.getByTestId('milestone-type-input').selectOption('feature');
  await page.getByTestId('milestone-version-input').fill('v0.1.0');
  await page.getByTestId('milestone-occurred-at-input').fill('2026-09-28T09:30');
  await page
    .getByTestId('milestone-notes-input')
    .fill('Double-entry postings reconcile from the first day.');
  await page.getByTestId('milestone-limitations-input').fill('No multi-currency yet.');
  await page.getByTestId('milestone-next-steps-input').fill('Add the reconciliation report.');
  await page.getByTestId('milestone-save').click();

  // It is listed under its project, and the status is the one the rule gives a record the owner
  // wrote: confirmed, because saving it was the decision.
  await expect(page).toHaveURL(new RegExp(`/projects/${projectId}$`));

  const milestone = page.getByTestId('milestone');
  await expect(milestone).toHaveCount(1);
  await expect(milestone.getByTestId('milestone-title')).toHaveText('First import of the ledger');
  await expect(milestone.getByTestId('milestone-status')).toHaveText('confirmed');
  await expect(milestone.getByTestId('milestone-type')).toHaveText('Feature');
  await expect(milestone.getByTestId('milestone-version')).toContainText('v0.1.0');
  await expect(milestone.getByTestId('milestone-notes')).toHaveText(
    'Double-entry postings reconcile from the first day.',
  );
  await expect(milestone.getByTestId('milestone-limitations')).toContainText('No multi-currency yet.');
  await expect(milestone.getByTestId('milestone-next-steps')).toContainText('Add the reconciliation report.');

  // The server agrees, so the page is not showing a local copy of something that was never written.
  const stored = await readMilestonesFromThePage(page, projectId);
  expect(stored.status).toBe(200);
  expect(stored.body.items).toHaveLength(1);
  expect(stored.body.items[0]).toMatchObject({
    title: 'First import of the ledger',
    status: 'confirmed',
    source: 'manual',
    revision: 1,
    knownLimitations: ['No multi-currency yet.'],
    nextSteps: ['Add the reconciliation report.'],
  });
});

test('an imported candidate waits for the owner, and only the owner can confirm it', async ({ page }) => {
  const { projectId } = await createProjectWithMilestone(page, 'Reporting', 'Reporting');

  // A record that arrived from a source is stored as a candidate, and the page says so. It is not
  // listed as confirmed and it is not counted as history.
  const imported = await importCandidateFromThePage(page, projectId, 'github', 'release/v1.0.0', 'Released v1.0.0');
  expect(imported.status).toBe(201);
  expect(imported.body.items[0].created).toBe(true);

  await page.reload();
  await expect(page.getByTestId('milestone-status')).toHaveCount(2);

  const candidate = page.locator('[data-testid="milestone"][data-status="candidate"]');
  await expect(candidate).toHaveCount(1);
  await expect(candidate.getByTestId('milestone-title')).toHaveText('Released v1.0.0');

  // Confirming is the owner's own action, offered on the record that needs it.
  await candidate.getByTestId('milestone-confirm').click();

  const confirmed = page.locator('[data-testid="milestone"][data-status="confirmed"]');
  await expect(confirmed).toHaveCount(2);
  await expect(page.getByTestId('project-notice')).toContainText('is now confirmed');

  // And the server agrees, which is the only thing that would make the page honest.
  const stored = await readMilestonesFromThePage(page, projectId);
  const storedCandidate = stored.body.items.find((item: { sourceKey: string | null }) => item.sourceKey === 'release/v1.0.0');

  expect(storedCandidate).toMatchObject({ status: 'confirmed', revision: 2, source: 'github' });

  // The same key imported again is the same record, not a second one.
  const repeated = await importCandidateFromThePage(page, projectId, 'github', 'release/v1.0.0', 'Released v1.0.0');
  expect(repeated.status).toBe(200);
  expect(repeated.body.items[0].created).toBe(false);
  expect(repeated.body.items[0].milestoneId).toBe(storedCandidate.id);

  const afterRepeat = await readMilestonesFromThePage(page, projectId);
  expect(afterRepeat.body.items).toHaveLength(2);
});

test('dismissing a milestone keeps it, and confirming brings it back', async ({ page }) => {
  const { projectId } = await createProjectWithMilestone(page, 'Invoicing', 'Invoicing');

  await page.getByTestId('milestone-dismiss').click();

  const dismissed = page.locator('[data-testid="milestone"][data-status="dismissed"]');
  await expect(dismissed).toHaveCount(1);
  await expect(dismissed.getByTestId('milestone-title')).toHaveText('First import of the ledger');

  const stored = await readMilestonesFromThePage(page, projectId);
  expect(stored.body.items[0].status).toBe('dismissed');

  // A dismissed record is still stored and still readable; returning it is an explicit action.
  await dismissed.getByTestId('milestone-confirm').click();
  await expect(page.locator('[data-testid="milestone"][data-status="confirmed"]')).toHaveCount(1);
});

test('an edit made against a stale revision is refused and shown, not silently applied', async ({ page, context }) => {
  const { projectId } = await createProjectWithMilestone(page, 'Ledger', 'Ledger');

  // One editor is opened and left alone at revision 1.
  await page.getByTestId('milestone-edit').click();
  await expect(page.getByTestId('milestone-title-input')).toHaveValue('First import of the ledger');

  // A second browser tab, sharing this installation's session, edits the same milestone first.
  const other = await context.newPage();
  await other.goto(`/projects/${projectId}`);
  await other.getByTestId('milestone-edit').click();
  await other.getByTestId('milestone-title-input').fill('Edited in the other tab');
  await other.getByTestId('milestone-save').click();
  await expect(other.locator('[data-testid="milestone-title"]')).toHaveText('Edited in the other tab');

  // The first editor now saves against the revision it loaded, which is no longer the stored one.
  await page.getByTestId('milestone-title-input').fill('Edited in the stale tab');
  await page.getByTestId('milestone-save').click();

  const conflict = page.getByTestId('milestone-conflict');
  await expect(conflict).toBeVisible();
  await expect(conflict).toContainText('changed since you opened it');
  await expect(conflict).toContainText('the stored revision is 2');
  await expect(conflict).toContainText('Your edit was not saved');

  // The other tab's edit is the one that is stored; nothing was merged and nothing was lost.
  const stored = await readMilestonesFromThePage(page, projectId);
  expect(stored.body.items[0]).toMatchObject({ title: 'Edited in the other tab', revision: 2 });

  await other.close();
});

test('an archived project is read-only in the browser and keeps its history', async ({ page }) => {
  const { projectId } = await createProjectWithMilestone(page, 'Archive me', 'Archive me');

  await page.getByTestId('project-archive').click();
  await expect(page.getByTestId('project-archived')).toBeVisible();

  // The history is still there, and no control offers to change anything.
  await expect(page.getByTestId('milestone')).toHaveCount(1);
  await expect(page.getByTestId('milestone-edit')).toHaveCount(0);
  await expect(page.getByTestId('milestone-confirm')).toHaveCount(0);
  await expect(page.getByTestId('milestone-new')).toHaveCount(0);
  await expect(page.getByTestId('project-archive')).toHaveCount(0);

  // A direct request to change it is refused by the server, not merely hidden by the page.
  const refused = await page.evaluate(
    async (id) => {
      const metadata = await (await fetch('/api/session/antiforgery', { credentials: 'same-origin' })).json();
      const response = await fetch(`/api/projects/${id}`, {
        method: 'PATCH',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', [metadata.headerName]: metadata.token, 'If-Match': '2' },
        body: JSON.stringify({ name: 'Renamed after archiving' }),
      });

      return { status: response.status, body: await response.json() };
    },
    projectId,
  );

  expect(refused.status).toBe(409);
  expect(refused.body.code).toBe('project_archived');

  const stored = await readProjectFromThePage(page, projectId);
  expect(stored.body.name).toBe('Archive me');
  expect(stored.body.archivedAt).not.toBeNull();
});

test('a signed-out browser sees no project surface at all', async ({ page }) => {
  await signIn(page);
  await expect(page.getByTestId('product-area')).toBeVisible();

  await page.getByTestId('session-sign-out').click();
  await expect(page.getByRole('heading', { name: 'Owner sign-in' })).toBeVisible();

  // The product is behind the session, and so is the data: nothing of it is rendered without one.
  await expect(page.getByTestId('product-area')).toHaveCount(0);
  await expect(page.getByTestId('milestone')).toHaveCount(0);

  const refused = await page.evaluate(async () => {
    const response = await fetch('/api/projects', { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  });

  expect(refused.status).toBe(401);
  expect(refused.body.code).toBe('unauthenticated');
});

// -------------------------------------------------------------------------------------------
// Steps. Each one drives the page the way an owner would, or reads the server's own answer from
// inside the page so the request carries this browser's own session cookie.
// -------------------------------------------------------------------------------------------

/** Signs in with the keyboard alone: focus, type, Enter. */
async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/** Creates a project and one milestone in it, and leaves the page on that project. */
async function createProjectWithMilestone(page: Page, name: string, description: string) {
  await signIn(page);

  await page.goto('/projects/new');
  await page.getByTestId('project-name-input').fill(name);
  await page.getByTestId('project-description-input').fill(description);
  await page.getByTestId('project-save').click();

  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}$/);
  const projectId = projectIdFrom(page.url());

  await page.getByTestId('milestone-new').click();
  await page.getByTestId('milestone-title-input').fill('First import of the ledger');
  await page.getByTestId('milestone-type-input').selectOption('feature');
  await page.getByTestId('milestone-occurred-at-input').fill('2026-09-28T09:30');
  await page.getByTestId('milestone-save').click();

  await expect(page.getByTestId('milestone')).toHaveCount(1);
  await expect(page).toHaveURL(new RegExp(`/projects/${projectId}$`));

  return { projectId };
}

function projectIdFrom(url: string): string {
  return new URL(url).pathname.split('/').filter((segment) => segment.length > 0)[1] ?? '';
}

async function readMilestonesFromThePage(page: Page, projectId: string) {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/projects/${id}/milestones`, { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  }, projectId);
}

async function readProjectFromThePage(page: Page, projectId: string) {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/projects/${id}`, { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  }, projectId);
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
