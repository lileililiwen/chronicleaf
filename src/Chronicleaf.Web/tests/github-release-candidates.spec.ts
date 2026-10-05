import { expect, test, type Page } from '@playwright/test';

/**
 * GITHUB-RELEASE-CANDIDATES-001/002 in a real browser, against the real published application and the
 * real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * <b>What this suite proves.</b> That the panel reads the server's candidates and renders each one as
 * what it is - something somebody else published, which Chronicleaf is suggesting - with its source and
 * its received instant, and that it carries no control anywhere that could confirm one. That a project
 * with no repository connected is told that, and is told it differently from a project with nothing
 * proposed yet, because those are different facts. That the stored total and the page are reported as
 * two numbers, so a project with more proposals than one page holds is not told it has none.
 *
 * <b>What this suite does not prove, and it is worth being exact about.</b> The candidates are seeded
 * the way the review and timeline suites seed their rows - directly, and read back before the suite
 * goes on, because a seed that silently wrote nothing has already cost this repository one green suite
 * that proved nothing. Nothing is stubbed: these are the real rows the real stores wrote, the panel
 * reads them through the real route, and the real envelope answers. But a pass is **not** evidence that
 * GitHub sent a delivery, that a signature was accepted, that a poll read a repository, or that a
 * candidate can be confirmed. The last of those is asserted negatively below and is structural rather
 * than a convention: there is no such verb in this product, in this panel or in the API, and the .NET
 * suite `GithubReleaseCandidatesTests` is where a real signed delivery is actually run through the
 * production HMAC comparison and the real outbox consumer.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** The project scripts/e2e.sh seeded, and the panel under test on it. */
const PROJECT_NAME = 'Fieldnotes';

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

test('a project with no repository connected says so rather than showing an empty list', async ({ page }) => {
  await signIn(page);

  // This project is created here rather than seeded: the seed connects the one project the other
  // scenarios read, and a second seeded project would be a second connection to keep subscribed.
  // Creating it through the page is what an owner does, and it leaves the seeded project alone.
  await page.goto('/projects/new');
  await page.getByTestId('project-name-input').fill('Unconnected notes');
  await page.getByTestId('project-description-input').fill('No repository here yet.');
  await page.getByTestId('project-save').click();
  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}$/);
  await expect(page.getByTestId('github-candidate-panel')).toBeVisible();

  // The framing is above everything else, because it is the thing an owner must not misread: a row on
  // this panel has been proposed and has not been recorded.
  await expect(page.getByTestId('github-candidate-framing')).toContainText('proposals, not Milestones');

  // Two different empty states, and which one is shown is itself the claim: a panel that showed the
  // second for the first would be telling an owner a project with no repository has a quiet one.
  await expect(page.getByTestId('github-candidate-no-connection')).toBeVisible();
  await expect(page.getByTestId('github-candidate-no-connection-title')).toHaveText(
    'This project has no repository connected',
  );
  await expect(page.getByTestId('github-candidate-empty')).toHaveCount(0);
});

test('nothing on the panel could confirm a candidate', async ({ page }) => {
  await signIn(page);
  await openCandidatePanel(page);

  const panel = page.getByTestId('github-candidate-panel');
  await expect(panel).toBeVisible();

  // Every verb this package must never have, checked against the panel's controls rather than its
  // prose. The panel must truthfully name published releases and merged pull requests, so whole-text
  // matching would refuse honest sentences; a new button with a dangerous name is still caught here,
  // and a reflow that changed the layout is not - a layout change must not be able to make this pass.
  // The only controls this package owns are the poll and the listening switch, asserted below.
  const controls = panel.getByRole('button');
  const controlCount = await controls.count();
  for (let index = 0; index < controlCount; index += 1) {
    const control = controls.nth(index);
    for (const forbidden of [
      /confirm/i,
      /accept (a |this )?candidate/i,
      /add (a )?milestone/i,
      /record (it|this|a milestone)/i,
      /publish/i,
      /promote/i,
    ]) {
      await expect(control).not.toContainText(forbidden);
    }
  }

  // The panel offers exactly the two things this package has verbs for: ask for a poll, and turn
  // listening on or off. Both read GitHub's state or change this project's configuration - neither
  // writes a milestone, and there is nothing else to click. The seed subscribes the connection, so
  // the switch reads listening here: the poll and the stop control are offered, and no start control
  // is rendered beside them.
  await expect(page.getByTestId('github-candidate-poll')).toBeVisible();
  await expect(page.getByTestId('github-candidate-stop-listening')).toBeVisible();
  await expect(page.getByTestId('github-candidate-start-listening')).toHaveCount(0);
});

test('candidates are shown as proposals with their source, instant and provider key', async ({ page }) => {
  await signIn(page);
  await openCandidatePanel(page);

  await expect(page.getByTestId('github-candidate-list')).toBeVisible();

  // The seed stores twenty-six candidates and this route asks for twenty-five, so a page that reported
  // itself as the whole history would be caught here rather than by an owner who lost a proposal.
  const rows = page.getByTestId('github-candidate-row');
  await expect(rows).toHaveCount(25);

  // Every row says the thing was proposed. Asserted per row rather than once on the panel, because a
  // list whose first row said "proposed" and whose last said "added" would satisfy a weaker check and
  // be exactly the failure this panel must not have.
  for (let index = 0; index < 25; index += 1) {
    await expect(rows.nth(index).getByTestId('github-candidate-sentence')).toContainText(
      'was proposed as a Milestone',
    );
  }

  // The release row: the design's own display requirement is the source and the received instant, and
  // the provider's own source key is beside them so an owner can see why a redelivery is one row.
  const release = rowWithTitle(page, 'Fifth release');
  await expect(release.getByTestId('github-candidate-kind')).toHaveText('Release');
  await expect(release.getByTestId('github-candidate-meta')).toContainText('From a signed delivery');
  await expect(release.getByTestId('github-candidate-meta')).toContainText('Received 2026-09-30T');
  await expect(release.getByTestId('github-candidate-key')).toContainText('Source key release:9301.');

  // The tag row carries the commit its key names, so an owner can see which commit was proposed rather
  // than a branch name that could move.
  const tag = rowWithTitle(page, 'v6.0.0');
  await expect(tag.getByTestId('github-candidate-kind')).toHaveText('Tag');
  await expect(tag.getByTestId('github-candidate-key')).toContainText('Source key tag:v6.0.0:');
  await expect(tag.getByTestId('github-candidate-key')).toContainText(
    'Commit 3333333333333333333333333333333333333333',
  );

  // The merged pull request is the kind whose provenance is somebody else's decision rather than a
  // publication, and it is proposed only because it merged.
  const pull = rowWithTitle(page, 'Add the reproducible pipeline');
  await expect(pull.getByTestId('github-candidate-kind')).toHaveText('Merged pull request');

  // The threshold row says what crossed, in the words the requirement uses.
  await expect(rowWithTitle(page, '100 stars').getByTestId('github-candidate-kind')).toHaveText('Stars');

  // The unmerged pull request is not here at all: it was delivered, recorded, and deliberately produced
  // nothing, and a panel that listed it would be proposing a reversal as an achievement.
  await expect(rowWithTitle(page, 'Abandon the pipeline')).toHaveCount(0);
});

test('a window the server cannot read is refused by name and changes nothing', async ({ page }) => {
  await signIn(page);
  await openCandidatePanel(page);

  await page.getByTestId('github-candidate-since').fill('last tuesday');
  await page.getByTestId('github-candidate-poll').click();

  // The server refuses an instant it cannot read and says which field, and the panel shows that
  // sentence. A wrong window is never silently replaced by a default: a poll that quietly read
  // everything would be an answer to a question nobody asked, and it would look like a correct one.
  await expect(page.getByTestId('github-candidate-failed')).toContainText(
    'Some of those values are not ones a request may carry',
  );

  // The candidates are unchanged, because a refused request is a request that wrote nothing.
  await expect(page.getByTestId('github-candidate-row')).toHaveCount(25);
});

test('the stored total is reported beside the page rather than as the page length', async ({ page }) => {
  await signIn(page);
  await openCandidatePanel(page);

  // The seed stores more candidates than one page holds, so a panel that reported the page as the total
  // would be telling the owner they had lost the rest of them.
  // Twenty-six stored, twenty-five shown. Both numbers, and the difference named, because a panel that
  // reported the page as the total would be telling this owner they had lost a proposal.
  await expect(page.getByTestId('github-candidate-count')).toContainText(
    '26 candidates are proposed for this project.',
  );
  await expect(page.getByTestId('github-candidate-count')).toContainText('Showing the newest 25.');
});

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/** Opens the seeded project and waits for the candidate panel to have finished its read. */
async function openCandidatePanel(page: Page) {
  await page.goto('/');
  // The dashboard opens a project through a button, not a link: the application is a single-page
  // shell and `onOpen` routes client-side, so there is no anchor to click.
  await page.getByTestId('dashboard-project').filter({ hasText: PROJECT_NAME }).first().click();
  await expect(page.getByRole('heading', { name: PROJECT_NAME })).toBeVisible();

  // Waiting for the panel to appear is not waiting for its read: the panel renders its framing and its
  // buttons before the list arrives, so an assertion on the list has to wait for the list.
  await expect(page.getByTestId('github-candidate-panel')).toBeVisible();
}

/** The one row whose title is exactly this, so a fixture change cannot silently widen a locator. */
function rowWithTitle(page: Page, title: string) {
  return page
    .getByTestId('github-candidate-row')
    .filter({ has: page.getByTestId('github-candidate-title').getByText(title, { exact: true }) });
}
