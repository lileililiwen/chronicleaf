import { expect, test, type Page } from '@playwright/test';

/**
 * MILESTONE-EVIDENCE-001 and MILESTONE-EVIDENCE-002 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded evidence: this installation starts with an empty project
 * list because nothing has been recorded in it, every record below is created by the same clicks an
 * owner would make, and every claim is read back from the page or from the server's own answer. The
 * empty state is asserted before anything is created, precisely because an empty milestone must not
 * display evidence it does not have.
 *
 * One step is not a click: advancing the evidence revision from inside the page before pressing the
 * form's button. That is deliberate - it is the only way to make the panel's revision genuinely
 * stale - and it is the real route with this browser's own session cookie, so the 409 the UI then
 * shows is the server's own honest answer and not a stubbed one.
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

test('an empty milestone shows an honest empty state and no invented evidence', async ({ page }) => {
  const milestoneId = await createProjectWithMilestone(page, 'Ledger', 'Double-entry accounting.');

  // The milestone exists and has no evidence. The panel says exactly that, and offers the one thing
  // the owner can add by hand rather than standing in a bundle that was never stored.
  const panel = page.getByTestId('evidence-panel');
  await expect(panel).toBeVisible();

  const empty = panel.getByTestId('evidence-empty');
  await expect(empty).toBeVisible();
  await expect(empty).toContainText('Nothing has been collected for this milestone');
  await expect(empty).toContainText('That is what is stored');

  // No bundle, no item and no availability badge is drawn for evidence that does not exist.
  await expect(panel.getByTestId('evidence-bundle')).toHaveCount(0);
  await expect(panel.getByTestId('evidence-item')).toHaveCount(0);
  await expect(panel.getByTestId('evidence-bundles')).toHaveCount(0);

  // The form says the truth about what pressing save would do: the first bundle is revision 1.
  await expect(panel.getByTestId('evidence-form-hint')).toHaveText(
    'There is no evidence yet, so this becomes revision 1.',
  );

  // And the server agrees that there is nothing, without anybody having to ask the page.
  const read = await readEvidenceFromThePage(page, milestoneId);
  expect(read.status).toBe(200);
  expect(read.body.revision).toBe(0);
  expect(read.body.items).toEqual([]);
});

test('the owner appends manual limitations and the view shows the stored bundles', async ({ page }) => {
  const milestoneId = await createProjectWithMilestone(page, 'Ledger', 'Double-entry accounting.');

  const panel = page.getByTestId('evidence-panel');

  // 1. The first append. A note, one limitation and one next step, all in one snapshot.
  await panel.getByTestId('evidence-notes').fill('The import works for one account only.');
  await panel.getByTestId('evidence-limitations').fill('Dates arrive in the browser locale.\nNo undo yet.');
  await panel.getByTestId('evidence-next-steps').fill('Normalise to UTC on ingest.');
  await panel.getByTestId('evidence-submit').click();

  await expect(panel.getByTestId('evidence-empty')).toHaveCount(0);
  await expect(panel.getByTestId('evidence-notice')).toContainText('Revision 1');
  await expect(panel.getByTestId('evidence-form-hint')).toHaveText(
    'Adding to revision 1. Earlier evidence is kept, not replaced.',
  );

  // The four stored items are on the page, each with the kind and availability the server sent.
  await expect(panel.getByTestId('evidence-revision')).toHaveText(/Revision 1/);
  await expect(panel.getByTestId('evidence-item')).toHaveCount(4);
  await expect(panel.getByTestId('evidence-item-kind').filter({ hasText: 'Limitations' })).toHaveCount(2);
  await expect(panel.getByTestId('evidence-item-kind').filter({ hasText: 'Next Steps' })).toHaveCount(1);
  await expect(panel.getByTestId('evidence-item-kind').filter({ hasText: 'Manual' })).toHaveCount(1);

  for (const badge of await panel.getByTestId('evidence-item-availability').all()) {
    await expect(badge).toHaveText('Collected');
  }

  // The owner's own source is shown as the owner, at the revision they were looking at, so a reader
  // can tell a statement of the owner's from something a source produced.
  await expect(panel.getByTestId('evidence-item-source').first()).toHaveText('owner at 0');

  // 2. A second append. The earlier evidence is still there and the new revision is a whole
  //    snapshot, not a replacement - which is the claim the requirement makes, read off the page.
  await panel.getByTestId('evidence-notes').fill('Undo is on the way.');
  await panel.getByTestId('evidence-submit').click();

  await expect(panel.getByTestId('evidence-notice')).toContainText('Revision 2');
  await expect(panel.getByTestId('evidence-revision').first()).toHaveText(/Revision 2/);
  await expect(panel.getByTestId('evidence-revision')).toHaveCount(2);

  // Revision 2 is the whole history: the first note is still on the page beside the new one, and
  // the second bundle on the page still shows the first note on its own.
  const newest = panel.getByTestId('evidence-bundle').first();
  const older = panel.getByTestId('evidence-bundle').nth(1);

  await expect(newest.getByTestId('evidence-item')).toHaveCount(5);
  await expect(older.getByTestId('evidence-item')).toHaveCount(4);

  await expect(newest.getByTestId('evidence-item-text').filter({ hasText: 'The import works for one account only.' })).toHaveCount(1);
  await expect(newest.getByTestId('evidence-item-text').filter({ hasText: 'Undo is on the way.' })).toHaveCount(1);

  // An earlier bundle is a whole snapshot too: it says what revision 1 said, unchanged.
  await expect(older.getByTestId('evidence-item-text').filter({ hasText: 'The import works for one account only.' })).toHaveCount(1);
  await expect(older.getByTestId('evidence-item-text').filter({ hasText: 'Undo is on the way.' })).toHaveCount(0);

  // The server's own answer matches what the page drew, revision by revision.
  const read = await readEvidenceFromThePage(page, milestoneId);
  expect(read.status).toBe(200);
  expect(read.body.revision).toBe(2);
  expect(read.body.items.map((bundle: { revision: number }) => bundle.revision)).toEqual([2, 1]);
  expect(read.body.items[0].items).toHaveLength(5);
  expect(read.body.items[1].items).toHaveLength(4);

  // Every item is attributed to the owner's own statement with the revision it answered, and none
  // of them claims to be something a source collected.
  for (const item of read.body.items[0].items) {
    expect(item.sourceRef).toBe('owner');
    expect(item.availability).toBe('complete');
    expect(item.payloadSha256).toMatch(/^[0-9a-f]{64}$/);
  }
});

test('a stale append surfaces the server 409 instead of discarding the owner statement', async ({ page }) => {
  const milestoneId = await createProjectWithMilestone(page, 'Ledger', 'Double-entry accounting.');

  const panel = page.getByTestId('evidence-panel');

  // The owner writes a note but has not pressed save yet.
  await panel.getByTestId('evidence-notes').fill('Dates arrive in the browser locale.');

  // Meanwhile the milestone's evidence moves on - here from the real route, with this browser's own
  // session, which is exactly what another tab or another device would do.
  const advanced = await appendManualFromThePage(
    page,
    milestoneId,
    0,
    'Somebody else got there first.',
  );
  expect(advanced.status).toBe(201);

  // The panel still believes it is looking at revision 0, and pressing save presents that. The
  // server refuses, and the refusal is shown rather than swallowed.
  await panel.getByTestId('evidence-submit').click();

  const failure = panel.getByTestId('evidence-append-failed');
  await expect(failure).toBeVisible();
  await expect(failure).toContainText('Nothing was added');
  await expect(failure).toContainText('somebody else added evidence to this milestone first');

  // No notice claims the statement was saved, which is the failure this scenario exists to prevent.
  await expect(panel.getByTestId('evidence-notice')).toHaveCount(0);

  // The panel re-read, and the newer evidence is on the page: revision 1, holding what the other
  // writer said and not the note the owner typed.
  await expect(panel.getByTestId('evidence-revision').first()).toHaveText(/Revision 1/);

  const newest = panel.getByTestId('evidence-bundle').first();
  await expect(newest.getByTestId('evidence-item')).toHaveCount(1);
  await expect(newest.getByTestId('evidence-item-text')).toHaveText('Somebody else got there first.');
  await expect(newest.getByTestId('evidence-item-text').filter({ hasText: 'browser locale' })).toHaveCount(0);

  // The owner's text is still in the form, because it was refused rather than thrown away. Pressing
  // save again - now against the revision the panel read - records it.
  await expect(panel.getByTestId('evidence-notes')).toHaveValue('Dates arrive in the browser locale.');
  await panel.getByTestId('evidence-submit').click();

  await expect(panel.getByTestId('evidence-notice')).toContainText('Revision 2');
  await expect(panel.getByTestId('evidence-revision').first()).toHaveText(/Revision 2/);
  await expect(panel.getByTestId('evidence-bundle').first().getByTestId('evidence-item')).toHaveCount(2);

  // And the owner's statement is now stored, inside a snapshot that kept what was already there.
  const stored = await readEvidenceFromThePage(page, milestoneId);
  expect(stored.status).toBe(200);
  expect(stored.body.revision).toBe(2);
  expect(stored.body.items[0].items).toHaveLength(2);
  expect(stored.body.items[1].items).toHaveLength(1);
});

// ===========================================================================================
// Helpers.
// ===========================================================================================

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/**
 * Creates a project and one milestone in it, leaves the page on that project, and returns the
 * milestone id read from the server's own answer rather than guessed from the URL.
 */
async function createProjectWithMilestone(
  page: Page,
  name: string,
  description: string,
): Promise<string> {
  await signIn(page);

  await page.goto('/projects/new');
  await page.getByTestId('project-name-input').fill(name);
  await page.getByTestId('project-description-input').fill(description);
  await page.getByTestId('project-save').click();

  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}$/);

  await page.getByTestId('milestone-new').click();
  await page.getByTestId('milestone-title-input').fill('First import of the ledger');
  await page.getByTestId('milestone-type-input').selectOption('feature');
  await page.getByTestId('milestone-version-input').fill('v0.1.0');
  await page.getByTestId('milestone-occurred-at-input').fill('2026-09-28T09:30');
  await page.getByTestId('milestone-notes-input').fill('Double-entry postings reconcile.');
  await page.getByTestId('milestone-save').click();

  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}$/);
  await expect(page.getByTestId('milestone')).toHaveCount(1);

  const projectId = new URL(page.url()).pathname.split('/').filter((segment) => segment.length > 0)[1] ?? '';
  const answer = await page.evaluate(async (id) => {
    const response = await fetch(`/api/projects/${id}/milestones`, { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  }, projectId);

  expect(answer.status).toBe(200);

  return answer.body.items[0].id as string;
}

async function readEvidenceFromThePage(page: Page, milestoneId: string) {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/milestones/${id}/evidence`, { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  }, milestoneId);
}

/**
 * Appends one manual note through the real route, with this browser's own session cookie and a fresh
 * antiforgery value, presenting the revision it is based on.
 */
async function appendManualFromThePage(page: Page, milestoneId: string, revision: number, notes: string) {
  return page.evaluate(
    async ([id, expected, note]) => {
      const metadata = await (
        await fetch('/api/session/antiforgery', { credentials: 'same-origin' })
      ).json();

      const response = await fetch(`/api/milestones/${id}/evidence/manual`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'If-Match': String(expected),
          [metadata.headerName]: metadata.token,
        },
        body: JSON.stringify({ notes: note, expectedRevision: expected }),
      });

      return { status: response.status, body: await response.json() };
    },
    [milestoneId, revision, notes],
  );
}
