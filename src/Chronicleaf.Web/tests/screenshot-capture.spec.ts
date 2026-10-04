import { expect, test, type Page } from '@playwright/test';

/**
 * SCREENSHOT-CAPTURE-001/002 in a real browser, against the real published application and the real
 * disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded candidate. This installation starts with an empty project
 * list, every record below is created by the same clicks an owner would make, and every claim is read
 * back from the page or from the server's own answer. The empty state is asserted before anything is
 * created, precisely because an empty milestone must not display a picture it does not have.
 *
 * <b>What this fixture can and cannot claim, stated up front.</b> A capture needs a browser confined
 * with no access to the host's network. On a host that cannot provide that - no user namespaces, no
 * CAP_NET_ADMIN, no iptables and no nft - the job answers `capture_sandbox_unavailable` before any
 * browser starts, and that is the design's answer rather than a defect: native capture outside that
 * sandbox is unavailable rather than a less-secure fallback. So what is asserted here is the honest
 * reporting of that refusal: the page says the host cannot capture, it says no page was visited and no
 * picture exists, it draws no candidate and no image, and it offers nothing that looks like a retry.
 * That is a real claim about the delivered product on this host, and it is the claim the requirement
 * exists to protect. A passing run here is therefore not evidence that screenshots can be captured
 * anywhere; it is evidence that this host's refusal is visible rather than silent.
 *
 * The request-time refusal is asserted too, and it is the half that does not need a browser at all: a
 * private destination is refused by the server's URL policy before a job is created, so the panel can
 * be shown refusing a page without a capture ever being attempted.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** The one operation kind this fixture's feature creates, as the server names it. */
const CAPTURE_JOB_KIND = 'milestone.screenshot-capture';

/**
 * How long a queued capture job is given to reach a status it will not leave.
 *
 * Generous and bounded. A capture is browser work in the delivered queue and this is the honest wait
 * for it; the alternative - asserting against a fixed short delay - would be asserting that the queue
 * is fast rather than that the product reports correctly, and would be a race dressed as a test.
 */
const JOB_TIMEOUT_MS = 120_000;

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

test('an empty milestone shows an honest empty state and no invented screenshot', async ({ page }) => {
  const milestoneId = await createProjectWithMilestone(page, 'Pricing page', 'The pricing table.');

  const panel = page.getByTestId('capture-panel');
  await expect(panel).toBeVisible();

  // Nothing has been captured, and the panel says that in words that cannot be read as a failure.
  const empty = panel.getByTestId('capture-empty');
  await expect(empty).toBeVisible();
  await expect(empty).toContainText('No screenshot has been captured');
  await expect(empty).toContainText('That is what is stored');
  await expect(empty).toContainText('it is not a placeholder and not a failed read');

  // No candidate, no image, and no progress claim, because there is nothing to show.
  await expect(panel.getByTestId('capture-candidate')).toHaveCount(0);
  await expect(panel.getByTestId('capture-candidate-image')).toHaveCount(0);
  await expect(panel.getByTestId('capture-job-running')).toHaveCount(0);
  await expect(panel.getByTestId('capture-sandbox-unavailable')).toHaveCount(0);

  // The form tells the owner the truth about what pressing the button does: a queued job, not a
  // picture, and a candidate that is not selected or attached to anything.
  await expect(panel.getByTestId('capture-form-hint')).toContainText('A capture is queued work');
  await expect(panel.getByTestId('capture-form-hint')).toContainText(
    'Candidates are not selected or attached to evidence here',
  );

  // And the server agrees that there is nothing, without anybody having to ask the page.
  const read = await readCandidatesFromThePage(page, milestoneId);
  expect(read.status).toBe(200);
  expect(read.body.items).toEqual([]);
  expect(read.body.mediaBaseUrl).toBe('/api/media');
});

test('a capture request is answered by a job and this host refuses it without starting a browser', async ({
  page,
}) => {
  const milestoneId = await createProjectWithMilestone(page, 'Pricing page', 'The pricing table.');

  const panel = page.getByTestId('capture-panel');

  await panel.getByTestId('capture-url').fill('https://example.com/pricing');
  await panel.getByTestId('capture-viewport-desktop').check();
  await panel.getByTestId('capture-viewport-mobile').check();
  await panel.getByTestId('capture-submit').click();

  // The 202 is reported as what it is: work accepted, no picture. A panel that drew a placeholder here
  // would be claiming a capture that has not happened.
  await expect(panel.getByTestId('capture-job-running')).toBeVisible();
  await expect(panel.getByTestId('capture-job-running')).toContainText('No picture exists yet');

  // Then the job reaches a status it will not leave, and the panel reports that status on its own
  // terms. On a host that cannot confine a browser the code is capture_sandbox_unavailable, and the
  // panel has to say the refusal in words rather than leaving a spinner that stopped.
  const outcome = await waitForTerminalJob(page, milestoneId);

  if (outcome === 'capture_sandbox_unavailable') {
    const refusal = panel.getByTestId('capture-sandbox-unavailable');
    // The panel follows the same job this fixture just read, on its own two-second poll, so it is given
    // a real wait for the answer to arrive rather than a fixed sleep that would be a race.
    await expect(refusal).toBeVisible({ timeout: 30_000 });
    await expect(refusal).toContainText('This host cannot capture screenshots');
    await expect(refusal).toContainText('capture_sandbox_unavailable');
    await expect(refusal).toContainText('before a browser');
    await expect(refusal).toContainText('No page was visited');
    await expect(refusal).toContainText('no picture was created');
    await expect(refusal).toContainText('no choice on this page changes it');

    // No candidate and no image are drawn for a capture that produced nothing, and the empty state is
    // still the one on screen: the reason the list is empty is the refusal, not a lost read.
    await expect(panel.getByTestId('capture-candidate')).toHaveCount(0);
    await expect(panel.getByTestId('capture-candidate-image')).toHaveCount(0);
    await expect(panel.getByTestId('capture-empty')).toBeVisible();

    // Nothing that looks like a retry. The host's answer does not change with a second press, and a
    // control that suggested it might would be misleading.
    await expect(panel.getByTestId('capture-submit')).toBeEnabled();
    await expect(panel.getByTestId('capture-request-failed')).toHaveCount(0);
  } else {
    // A host that can confine a browser, or a job that stopped for some other stated reason. The claim
    // is then the ordinary one, and a failure inside it is a real failure rather than a skipped
    // assertion - which is why the code is required to be one this fixture knows.
    expect(['capture_timeout', 'succeeded']).toContain(outcome);

    if (outcome === 'succeeded') {
      await expect(panel.getByTestId('capture-job-outcome')).toContainText('The job finished', {
        timeout: 30_000,
      });
      await expect(panel.getByTestId('capture-candidate').first()).toBeVisible({ timeout: 30_000 });

      // A candidate is a candidate: this package does not select, reorder, mark a primary, caption or
      // append to evidence, so none of those controls may exist for one.
      await expect(panel.getByTestId('capture-candidate-status').first()).toHaveText('candidate');
      await expect(panel.getByRole('button', { name: /make (this|the)|primary|select/i })).toHaveCount(0);
    } else {
      await expect(panel.getByTestId('capture-job-outcome')).toContainText('capture_timeout', {
        timeout: 30_000,
      });
      await expect(panel.getByTestId('capture-candidate')).toHaveCount(0);
    }
  }

  // Whatever happened, the table is the server's own: a candidate exists only if the server holds one.
  const read = await readCandidatesFromThePage(page, milestoneId);
  expect(read.status).toBe(200);
  expect(read.body.items.length).toBe(outcome === 'succeeded' ? 2 : 0);
});

test('a private destination is refused before a job exists and the page says so', async ({ page }) => {
  const milestoneId = await createProjectWithMilestone(page, 'Pricing page', 'The pricing table.');

  const panel = page.getByTestId('capture-panel');

  // How many capture jobs this installation has already, because the claim below is about this
  // request and not about the suite: an earlier scenario in this same installation asked for a capture
  // and the server answered it. The refusal under test is "this one created nothing", which is a claim
  // about a difference, so the difference has to be measured.
  const before = await readCaptureJobCount(page);

  // The loopback address this very browser is talking to. The server's URL policy refuses it, and it
  // does so before the job exists - so there is nothing for a worker to pick up and dial later.
  await panel.getByTestId('capture-url').fill('http://127.0.0.1:8080/admin');
  await panel.getByTestId('capture-submit').click();

  const refusal = panel.getByTestId('capture-request-failed');
  await expect(refusal).toBeVisible();
  await expect(refusal).toContainText('Nothing was requested');
  await expect(refusal).toContainText('refused this destination before a job was created');
  await expect(refusal).toContainText('no browser was ever started');
  await expect(refusal).toContainText('Captures may only go to public pages');

  // The refusal deliberately does not say which address it refused: confirming the shape of a host on
  // a network is itself a disclosure, so the page must not have printed the address back.
  await expect(refusal).not.toContainText('127.0.0.1');
  await expect(refusal).not.toContainText('localhost');

  // No new job and no candidate, both read from the server rather than inferred from the page. The
  // count is unchanged, which is the whole claim: a refused destination never became queued work.
  expect(await readCaptureJobCount(page)).toBe(before);

  const read = await readCandidatesFromThePage(page, milestoneId);
  expect(read.body.items).toEqual([]);

  // And the panel never claimed a job was running.
  await expect(panel.getByTestId('capture-job-running')).toHaveCount(0);
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
  await page.getByTestId('milestone-title-input').fill('Ship the pricing page');
  await page.getByTestId('milestone-type-input').selectOption('feature');
  await page.getByTestId('milestone-version-input').fill('v0.2.0');
  await page.getByTestId('milestone-occurred-at-input').fill('2026-10-04T09:30');
  await page.getByTestId('milestone-notes-input').fill('Both viewports agree on price.');
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

async function readCandidatesFromThePage(page: Page, milestoneId: string) {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/milestones/${id}/screenshots`, { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  }, milestoneId);
}

async function readOperationsFromThePage(page: Page) {
  return page.evaluate(async () => {
    const response = await fetch('/api/operations', { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  });
}

/**
 * How many capture jobs this installation holds, read from the delivered operations route.
 *
 * The kind is the server's own string rather than a prefix guess, so a future capture kind cannot
 * silently start counting towards this fixture's claims.
 */
async function readCaptureJobCount(page: Page): Promise<number> {
  const operations = await readOperationsFromThePage(page);

  expect(operations.status).toBe(200);

  return (operations.body.items as ReadonlyArray<{ readonly kind: string }>).filter(
    (operation) => operation.kind === CAPTURE_JOB_KIND,
  ).length;
}

/**
 * Waits for the capture job this page started to reach a status it will not leave, and reports what
 * stopped it in the server's own words.
 *
 * This reads the delivered operations route rather than the page, because the page is what is under
 * test and it must not be the thing that decides when to stop waiting. The timeout is generous and
 * bounded: a job that has not finished by then has failed the fixture rather than silently passing.
 */
async function waitForTerminalJob(page: Page, milestoneId: string): Promise<string> {
  const deadline = Date.now() + JOB_TIMEOUT_MS;

  for (;;) {
    const operations = await readOperationsFromThePage(page);
    const captures = (operations.body.items as ReadonlyArray<{
      readonly kind: string;
      readonly status: string;
      readonly errorCode: string | null;
      readonly createdAt: string;
    }>).filter((operation) => operation.kind === CAPTURE_JOB_KIND);

    expect(captures.length, 'the capture request created a job').toBeGreaterThan(0);

    // The newest job on this installation is the one this page just asked for.
    const finished = [...captures]
      .sort((left, right) => (left.createdAt < right.createdAt ? 1 : -1))
      .find((operation) => !isActive(operation.status));

    if (finished !== undefined) {
      // The candidates are read once more through the milestone's own route, so a job that published
      // something is visible to the page before the assertions read it.
      await readCandidatesFromThePage(page, milestoneId);

      return finished.status === 'succeeded' ? 'succeeded' : (finished.errorCode ?? finished.status);
    }

    if (Date.now() > deadline) {
      throw new Error(`the capture job was still ${captures[0]?.status} after 120s`);
    }

    await page.waitForTimeout(2_000);
  }
}

function isActive(status: string): boolean {
  return status === 'queued' || status === 'running' || status === 'retrying';
}
