import { expect, test, type Download, type Page } from '@playwright/test';

/**
 * MARKDOWN-REVIEW-EXPORT-001 and MARKDOWN-REVIEW-EXPORT-002 in a real browser, against the real
 * published application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * **What is real.** The project, the milestone, its evidence and the stored draft below are all
 * real rows in a real database. The one seeded draft is written by `scripts/e2e.sh` through the
 * same `INSERT` a completed provider run produces, with a body hash computed from its own bytes
 * and a voice snapshot the read route re-checks rather than trusts. Every edit, approval and
 * export below goes through the shipped panel and the published server - the approval row, the
 * new revision and the downloaded bytes are records the product itself created.
 *
 * **What is a fixture, and stated rather than implied.** The stored draft's *text* is a fixture.
 * No provider was called: the voice snapshot names an English voice the project never stored,
 * and the summary cites the real evidence item the seed created. So a pass is evidence that the
 * panel edits, approves and exports a stored draft faithfully - its inert preview, its
 * revision-bound approval and its byte-exact download - and is **not** evidence that any
 * provider can be called or that a draft was generated on this host. The .NET suite is where a
 * real provider answer flows through generation into review; this one is where a person reads,
 * edits, approves and downloads the result - in both shapes the route serves, Markdown bytes
 * and sanitized HTML.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** The milestone `scripts/e2e.sh` seeded a stored draft against, by the title it created it with. */
const SEEDED_REVIEW_TITLE = 'Reviewed release';

/**
 * A second seeded milestone with its own stored draft. The first test edits, approves and
 * re-edits its milestone, and a milestone cannot be un-edited through the UI - there is no
 * control that removes a revision, because an approval history is not a thing the UI may throw
 * away - so the Enter test reads its own installation row rather than state the first test left
 * behind.
 */
const SEEDED_SECOND_TITLE = 'Second review';

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

test('a stored draft is reviewed, edited, approved and exported as exact bytes', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_REVIEW_TITLE);

  const panel = row(page, SEEDED_REVIEW_TITLE).getByTestId('review-panel');
  await expect(panel).toBeVisible();

  // The stored state, read from the server: revision 1, never approved.
  await expect(panel.getByTestId('review-revision')).toContainText('Revision 1');
  await expect(panel.getByTestId('review-status')).toHaveText('draft');
  await expect(panel.getByTestId('review-findings-clean')).toBeVisible();

  // The preview renders the stored text as inert characters. The seeded body carries a raw
  // tag, an unsafe link and a safe link on purpose: a preview without them could not tell a
  // sanitizing renderer from one that happens to have nothing to refuse.
  const preview = panel.getByTestId('review-preview-body');
  await expect(preview).toContainText('<b>not bold</b>');
  expect(await preview.locator('script').count()).toBe(0);
  expect(await preview.locator('a[href^="javascript"]').count()).toBe(0);
  expect(await preview.locator('b').count()).toBe(0);
  const safe = preview.getByTestId('review-preview-link');
  await expect(safe).toHaveAttribute('href', 'https://example.invalid/notes');

  // The approve control is a button only a press can reach: no form here submits, so Enter in
  // the editor edits text and never approves.
  await expect(panel.getByTestId('review-approve')).toHaveAttribute('type', 'button');

  // An edit appends a revision rather than rewriting one. The editor starts on the stored
  // text, and saving stores the owner's words as revision 2. The unclosed bold run stays
  // literal characters: a special character that starts nothing must never hang the preview.
  const editor = panel.getByTestId('review-body');
  await expect(editor).toHaveValue(/The pipeline now reproduces/);
  await editor.fill(
    `${await editor.inputValue()}\n\nA human correction, in the owner's own words.\n\n**unclosed bold stays text\n`,
  );
  await panel.getByTestId('review-save').click();
  await expect(panel.getByTestId('review-revision')).toContainText('Revision 2');
  await expect(panel.getByTestId('review-status')).toHaveText('draft');
  await expect(panel.getByTestId('review-preview-body')).toContainText(
    '**unclosed bold stays text',
  );

  // The approval names the revision that was read. The export below downloads exactly those
  // bytes, so the text is captured here rather than trusted from the editor afterwards.
  const approvedText = await editor.inputValue();
  await panel.getByTestId('review-approve').click();
  await expect(panel.getByTestId('review-status')).toHaveText('approved');

  const download = page.waitForEvent('download');
  await panel.getByTestId('review-export').click();
  const served = await readDownload(await download);
  expect(served).toBe(approvedText);

  // The same approval in the second shape: a sanitized HTML file carrying the approved text
  // as escaped characters rather than runnable markup.
  const htmlDownload = page.waitForEvent('download');
  await panel.getByTestId('review-export-html').click();
  const htmlFile = await htmlDownload;
  expect(htmlFile.suggestedFilename()).toMatch(/\.html$/);
  const html = await readDownload(htmlFile);
  expect(html).toContain('<!DOCTYPE html>');
  expect(html).toContain('chronicleaf-template-version');
  expect(html).toContain('A human correction, in the owner&#39;s own words.');
  expect(html).toContain('&lt;b&gt;not bold&lt;/b&gt;');
  expect(html).toContain('**unclosed bold stays text');
  expect(html).not.toContain('<script');
  expect(html).not.toContain('href="javascript');

  // Editing after approval ends it: the status returns to invalidated, the export control
  // disables itself, and the approval cannot be re-used for the new head.
  await editor.fill(`${approvedText}\n\nA second thought.\n`);
  await panel.getByTestId('review-save').click();
  await expect(panel.getByTestId('review-revision')).toContainText('Revision 3');
  await expect(panel.getByTestId('review-status')).toHaveText('invalidated');
  await expect(panel.getByTestId('review-invalidated')).toBeVisible();
  await expect(panel.getByTestId('review-export')).toBeDisabled();
});

test('Enter in the editor never approves', async ({ page }) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_SECOND_TITLE);

  const panel = row(page, SEEDED_SECOND_TITLE).getByTestId('review-panel');
  await expect(panel).toBeVisible();
  await expect(panel.getByTestId('review-status')).toHaveText('draft');

  // Dirty the editor and press Enter in it. Enter edits text - a newline - and the approve
  // control is a button, not a submission, so the review state must not move to approved.
  const editor = panel.getByTestId('review-body');
  await editor.focus();
  await page.keyboard.press('Enter');
  await expect(panel.getByTestId('review-status')).toHaveText('draft');
  await expect(panel.getByTestId('review-approve')).toBeVisible();
});

/** Reads a download's bytes as text, whatever the browser did with the file. */
async function readDownload(file: Download): Promise<string> {
  const stream = await file.createReadStream();

  if (stream === null) {
    throw new Error('the export did not produce a file');
  }

  const chunks: Buffer[] = [];
  for await (const chunk of stream) {
    chunks.push(chunk as Buffer);
  }

  return Buffer.concat(chunks).toString('utf8');
}

async function signIn(page: Page): Promise<void> {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/**
 * Opens the project the milestone with this title lives in, and waits for that milestone's row.
 *
 * The project is found through the owner's own read routes rather than by clicking the first card, because
 * a suite that seeds a project and then creates more of its own cannot assume its own row is first, and a
 * fixture that read the wrong project would assert about a record it never looked at.
 */
async function openProjectHolding(page: Page, title: string): Promise<void> {
  const found = await locateMilestone(page, title);

  if (found === null) {
    throw new Error(`no milestone titled "${title}" exists in this installation`);
  }

  await page.goto(`/projects/${found.projectId}`);
  await expect(page.getByTestId('project-name')).toBeVisible();
  await expect(row(page, title)).toBeVisible();

  // PROJECT-AREA-IA-001 migration: the review panel moved to the Content
  // area; the same selectors are asserted there with the same expectations.
  await page.getByTestId('project-area-content').click();
  await expect(row(page, title)).toBeVisible();
}

/** The milestone row with this exact title, so two milestones in two projects cannot be confused. */
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

      const list = (await response.json()) as {
        readonly items: readonly { readonly id: string; readonly title: string }[];
      };

      const found = list.items.find((item) => item.title === wanted);

      if (found !== undefined) {
        return { projectId: project.id, milestoneId: found.id };
      }
    }

    return null;
  }, title);
}
