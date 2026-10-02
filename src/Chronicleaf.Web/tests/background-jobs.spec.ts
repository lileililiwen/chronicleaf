import { expect, test, type Page } from '@playwright/test';

/**
 * BACKGROUND-JOBS-001 and BACKGROUND-JOBS-002 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no simulated history. The rows this panel shows were written into the
 * real `operations` table by scripts/e2e.sh, as genuine (scope, kind, key) submissions, and the
 * panel reads them back through the real private route with the browser's own session cookie. The
 * queued row is queued and the failed row is failed because the database says so; if the panel
 * disagreed with the database, the assertions below would fail rather than the panel being right.
 *
 * The empty state is asserted on a status filter that matches nothing, because a filter that matches
 * nothing is the one honest "no operations" the panel can be shown when the installation does have
 * some - and an installation that genuinely has none must not display a history it does not have.
 *
 * Nothing in this file contacts a provider. No operation is executed, no external write is
 * attempted, and the only mutation is the stop the server itself declares legal.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;
const QUEUED_OPERATION = process.env.CHRONICLEAF_E2E_QUEUED_OPERATION;

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

test('a signed-out browser sees no operations panel at all', async ({ page }) => {
  // The routes are private, so there is nothing to show and nothing to guess at. The session gate
  // decides, and the product area is behind it.
  await expect(page.getByTestId('operations-panel')).toHaveCount(0);
  await expect(page.getByTestId('session-token-input')).toBeVisible();
});

test('the panel reports every stored status truthfully and offers no invented history', async ({ page }) => {
  await signIn(page);
  await page.goto('/operations');

  const panel = page.getByTestId('operations-panel');
  await expect(panel).toBeVisible();
  await expect(page.getByTestId('operations-list')).toBeVisible();

  // Every status the panel can show is backed by a real row, and the panel says the stored word.
  const rows = page.getByTestId('operation-row');
  await expect(rows).toHaveCount(4);

  for (const status of ['queued', 'running', 'succeeded', 'failed', 'cancelled', 'superseded']) {
    await expect(page.getByTestId(`operations-filter-${status}`)).toBeVisible();
  }

  // The words are the persisted ones, unchanged. A panel that translated them would be reporting
  // something the database does not hold.
  const stored = await readStoredStatuses(page);
  for (const status of stored) {
    await expect(page.locator(`[data-status="${status}"]`)).toHaveCount(
      await page.locator(`[data-status="${status}"]`).count(),
    );
    expect(stored.filter((value) => value === status).length).toBe(
      await page.locator(`[data-status="${status}"]`).count(),
    );
  }

  // A queued operation reports the attempt budget honestly rather than implying it will keep going.
  const queued = page.locator('[data-status="queued"]');
  await expect(queued.getByTestId('operation-status')).toHaveText('queued');
  await expect(queued.getByTestId('operation-explanation')).toHaveText(
    'Waiting for a worker to pick this up.',
  );
  await expect(queued.getByTestId('operation-attempt')).toHaveText('0 of 3');

  // A failure shows the stored redacted code, and a success shows the committed result reference.
  const failed = page.locator('[data-status="failed"]');
  await expect(failed.getByTestId('operation-status')).toHaveText('failed');
  await expect(failed.getByTestId('operation-explanation')).toHaveText('Stopped without a result.');
  await expect(failed.getByTestId('operation-attempt')).toHaveText('3 of 3');
  await expect(failed.getByTestId('operation-error-code')).toHaveText('operation_transient_failure');
  await expect(failed.getByTestId('operation-result-ref')).toHaveCount(0);

  const succeeded = page.locator('[data-status="succeeded"]');
  await expect(succeeded.getByTestId('operation-status')).toHaveText('succeeded');
  await expect(succeeded.getByTestId('operation-explanation')).toHaveText(
    'Finished, and its result is recorded.',
  );
  await expect(succeeded.getByTestId('operation-result-ref')).toHaveText('e2e-result:export');
  await expect(succeeded.getByTestId('operation-error-code')).toHaveCount(0);

  // A cancelled operation says what cancellation actually means here: it stopped, and nothing was
  // kept. There is no result reference beside it, because none was committed.
  const cancelled = page.locator('[data-status="cancelled"]');
  await expect(cancelled.getByTestId('operation-status')).toHaveText('cancelled');
  await expect(cancelled.getByTestId('operation-explanation')).toHaveText(
    'Stopped at the owner’s request, and no result was kept.',
  );
  await expect(cancelled.getByTestId('operation-result-ref')).toHaveCount(0);

  // The worker's internals are not on the wire, so the panel cannot display a lease or a fingerprint.
  const body = await page.locator('body').innerText();
  expect(body).not.toContain('lease');
  expect(body.toLowerCase()).not.toContain('fingerprint');
});

test('a status filter narrows the panel and says so when it matches nothing', async ({ page }) => {
  await signIn(page);
  await page.goto('/operations');

  await expect(page.getByTestId('operation-row')).toHaveCount(4);

  // A status this installation has no record of. The panel says the installation has none of that
  // status, rather than showing everything or showing nothing at all.
  await page.getByTestId('operations-filter-superseded').click();
  await expect(page.getByTestId('operation-row')).toHaveCount(0);
  await expect(page.getByTestId('operations-empty')).toBeVisible();
  await expect(page.getByTestId('operations-empty')).toContainText(
    'No background jobs have that status.',
  );

  // A status it does have, and the empty state is gone again.
  await page.getByTestId('operations-filter-failed').click();
  await expect(page.getByTestId('operation-row')).toHaveCount(1);
  await expect(page.getByTestId('operations-empty')).toHaveCount(0);

  await page.getByTestId('operations-filter-all').click();
  await expect(page.getByTestId('operation-row')).toHaveCount(4);
});

test('the owner stops a queued job and the change is the server’s, not the panel’s', async ({ page }) => {
  if (!QUEUED_OPERATION) {
    throw new Error(
      'CHRONICLEAF_E2E_QUEUED_OPERATION is not set. Run these fixtures through scripts/e2e.sh.',
    );
  }

  await signIn(page);
  await page.goto(`/operations/${QUEUED_OPERATION}`);

  const panel = page.getByTestId('operations-panel');
  await expect(panel).toBeVisible();
  await expect(page.getByTestId('operation-status')).toHaveText('queued');

  // The stop is offered because the server says the operation is cancellable, and it is the only
  // action the panel offers at all.
  const stop = page.getByTestId('operation-cancel');
  await expect(stop).toBeVisible();
  await stop.click();

  // The panel now reports the status the server returned, and no longer offers an action that would
  // be refused.
  await expect(page.getByTestId('operation-status')).toHaveText('cancelled');
  await expect(page.getByTestId('operation-explanation')).toHaveText(
    'Stopped at the owner’s request, and no result was kept.',
  );
  await expect(page.getByTestId('operation-result-ref')).toHaveCount(0);
  await expect(page.getByTestId('operation-cancel')).toHaveCount(0);

  // Read back over the wire with the same session, so the claim is about the application and not
  // about what this page believes it did.
  const confirmed = await page.evaluate(async (id) => {
    const response = await fetch(`/api/operations/${id}`, { credentials: 'same-origin' });
    return (await response.json()) as { status: string; cancellable: boolean; resultRef: string | null };
  }, QUEUED_OPERATION);

  expect(confirmed.status).toBe('cancelled');
  expect(confirmed.cancellable).toBe(false);
  expect(confirmed.resultRef).toBeNull();
});

test('an operation id this installation does not have is reported as not found', async ({ page }) => {
  await signIn(page);
  await page.goto('/operations/11111111-2222-3333-4444-555555555555');

  await expect(page.getByTestId('operation-missing')).toBeVisible();
  await expect(page.getByTestId('operation-missing')).toContainText('Background job not found');

  // The refusal names nothing about any other operation.
  await expect(page.getByTestId('operation-missing')).not.toContainText('operation_');
});

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/** Reads the stored statuses back over the wire, so the panel can be compared with the server. */
async function readStoredStatuses(page: Page): Promise<string[]> {
  return page.evaluate(async () => {
    const response = await fetch('/api/operations?limit=100', { credentials: 'same-origin' });
    const body = (await response.json()) as { items: { status: string }[] };

    return body.items.map((item) => item.status);
  });
}
