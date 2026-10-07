import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';

import { expect, test, type Download, type Page } from '@playwright/test';

/**
 * STATIC-BLOG-EXPORT-UI-001/002 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API: this installation starts with one project, two
 * confirmed milestones, one evidence bundle each, one approved story on the
 * first and one never-approved story on the second, all written by the suite
 * seed in scripts/e2e.sh. Every pick, export, watch and download below goes
 * through the shipped panel and the published server, whose worker really
 * builds the archive (jobs stay enabled for this suite).
 *
 * What this suite claims, and what it does not. A pass is evidence that an
 * approved selection is exported by clicking - pick milestone, check asset,
 * press Export - and that the offered download serves bytes byte-equal to
 * the delivered download route; that an unapproved selection is refused in
 * the server's own words with no download offered; that the empty states
 * are honest; and that nothing exported before the owner acted. It is NOT
 * evidence that the archive renders any particular page: the bytes inside
 * the ZIP are the .NET suite's claim, not this one's.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** The milestone the seed approved a story against, by the title it created it with. */
const APPROVED_STORY_TITLE = 'Export source story';
/** The milestone the seed left without any approval, by the title it created it with. */
const UNAPPROVED_STORY_TITLE = 'Unexported draft story';
/** The milestone the seed left with no draft and no variants. */
const BARE_MILESTONE_TITLE = 'Bare milestone';

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

test('select approved assets, export, watch, and the download matches the server byte for byte', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, APPROVED_STORY_TITLE);
  await page.getByTestId('project-area-publish').click();

  const panel = page.getByTestId('static-blog-export-panel');
  await expect(panel).toBeVisible();

  // Nothing may leave before the owner acts: arm the counter before any click, and no
  // export POST may fire until the Export button is pressed.
  const fired = armExportCounter(page);

  const milestones = panel.getByTestId('static-blog-export-milestone-picker');
  await expect(milestones.locator('option')).toContainText([APPROVED_STORY_TITLE]);
  await milestones.selectOption({ label: APPROVED_STORY_TITLE });

  const options = panel.getByTestId('static-blog-export-asset-option');
  await expect(options.first()).toBeVisible();
  await expect(panel.getByTestId('static-blog-export-included').first()).toBeVisible();

  await options.first().check();
  await expect(panel.getByTestId('static-blog-export-preview')).toContainText('1 included');
  expect(fired.exports).toBe(0);

  const [exportResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/projects\/[^/]+\/exports\/static-blog$/.test(new URL(response.url()).pathname),
    ),
    panel.getByTestId('static-blog-export-submit').click(),
  ]);

  expect(exportResponse.status()).toBe(202);
  const accepted = (await exportResponse.json()) as { operationId: string; created: boolean };
  expect(accepted.operationId).toMatch(/^[0-9a-f-]{36}$/);
  expect(fired.exports).toBe(1);

  // The worker really builds: the panel watches the delivered status route to its terminal
  // word, and the ready state names the server's own counts and digest.
  await expect(panel.getByTestId('static-blog-export-state')).toHaveText('succeeded', {
    timeout: 120_000,
  });
  await expect(panel.getByTestId('static-blog-export-operation')).toHaveText(
    accepted.operationId,
  );
  await expect(panel.getByTestId('static-blog-export-posts')).toHaveText('1');
  const digest = (await panel.getByTestId('static-blog-export-digest').textContent()) ?? '';
  expect(digest).toMatch(/^[0-9a-f]{64}$/);

  // The server says the same thing the panel does.
  const stored = await readExportFromThePage(page, accepted.operationId);
  expect(stored.status).toBe(200);
  expect(stored.body.status).toBe('succeeded');
  expect(stored.body.postCount).toBe(1);
  expect(stored.body.contentSha256).toBe(digest);

  // The offered download is the server's attachment, not a re-render: byte-equal to the
  // delivered download route fetched directly, and a ZIP by magic.
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    panel.getByTestId('static-blog-export-download').click(),
  ]);

  const offered = await downloadBytes(download);
  const direct = await downloadBytesFromThePage(page, accepted.operationId);
  expect(offered.equals(direct)).toBe(true);
  expect(offered.subarray(0, 2).toString('binary')).toBe('PK');

  // The served archive is deterministic: fetching the same completed job again serves
  // the identical bytes, so the panel could never offer a lookalike.
  const directAgain = await downloadBytesFromThePage(page, accepted.operationId);
  expect(directAgain.equals(direct)).toBe(true);
  expect(sha256Hex(offered)).toBe(sha256Hex(directAgain));
});

test('an unapproved selection is refused in the server own words with no download', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, UNAPPROVED_STORY_TITLE);
  await page.getByTestId('project-area-publish').click();

  const panel = page.getByTestId('static-blog-export-panel');
  await expect(panel).toBeVisible();

  const milestones = panel.getByTestId('static-blog-export-milestone-picker');
  await milestones.selectOption({ label: UNAPPROVED_STORY_TITLE });

  const options = panel.getByTestId('static-blog-export-asset-option');
  await expect(options.first()).toBeVisible();
  await expect(panel.getByTestId('static-blog-export-excluded').first()).toContainText(
    'excluded (draft)',
  );

  await options.first().check();
  await expect(panel.getByTestId('static-blog-export-preview')).toContainText('0 included');

  await panel.getByTestId('static-blog-export-submit').click();
  await expect(panel.getByTestId('static-blog-export-error')).toContainText('approval_required');
  await expect(panel.getByTestId('static-blog-export-download')).toHaveCount(0);

  // The server says the same thing the panel does: nothing was ever built for this selection.
  const operationId = await watchedOperationFromThePage(page);
  expect(operationId).toBeNull();
});

test('a milestone with no draft and no variants states its empty case honestly', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, APPROVED_STORY_TITLE);
  await page.getByTestId('project-area-publish').click();

  const panel = page.getByTestId('static-blog-export-panel');
  await expect(panel).toBeVisible();

  const milestones = panel.getByTestId('static-blog-export-milestone-picker');
  await expect(milestones.locator('option')).toContainText([BARE_MILESTONE_TITLE]);
  await milestones.selectOption({ label: BARE_MILESTONE_TITLE });

  await expect(panel.getByTestId('static-blog-export-assets-empty')).toContainText(
    'nothing to select from it',
  );
  await expect(panel.getByTestId('static-blog-export-submit')).toBeDisabled();
});

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/**
 * Opens the project the milestone with this title lives in, and waits for that
 * milestone's row - the same route the content-asset-variants suite reads, because a
 * fixture that opened the wrong project would assert about a record it never looked at.
 */
async function openProjectHolding(page: Page, title: string): Promise<void> {
  const found = await locateMilestone(page, title);

  if (found === null) {
    throw new Error(`no milestone titled "${title}" exists in this installation`);
  }

  await page.goto(`/projects/${found.projectId}`);
  await expect(page.getByTestId('project-name')).toBeVisible();
  await expect(row(page, title)).toBeVisible();

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

/**
 * Counts the export POSTs the page issues from here on. A panel that merely renders must
 * not export: the count is read after the clicks, never awaited.
 */
function armExportCounter(page: Page): { exports: number } {
  const fired = { exports: 0 };

  page.on('request', (request) => {
    if (request.method() !== 'POST') {
      return;
    }

    const path = new URL(request.url()).pathname;

    if (/\/api\/projects\/[^/]+\/exports\/static-blog$/.test(path)) {
      fired.exports += 1;
    }
  });

  return fired;
}

async function readExportFromThePage(page: Page, operationId: string) {
  return page.evaluate(async (id) => {
    const projects = await fetch('/api/projects', { credentials: 'same-origin' });
    const catalogue = (await projects.json()) as { readonly items: readonly { readonly id: string }[] };

    for (const project of catalogue.items) {
      const response = await fetch(
        `/api/projects/${project.id}/exports/static-blog/${id}`,
        { credentials: 'same-origin' },
      );

      if (response.ok) {
        return { status: response.status, body: await response.json() };
      }
    }

    return { status: 404, body: null };
  }, operationId);
}

async function downloadBytesFromThePage(page: Page, operationId: string): Promise<Buffer> {
  const base64 = await page.evaluate(async (id) => {
    const projects = await fetch('/api/projects', { credentials: 'same-origin' });
    const catalogue = (await projects.json()) as { readonly items: readonly { readonly id: string }[] };

    for (const project of catalogue.items) {
      const response = await fetch(
        `/api/projects/${project.id}/exports/static-blog/${id}/download`,
        { credentials: 'same-origin' },
      );

      if (response.ok) {
        const buffer = await response.arrayBuffer();
        const bytes = new Uint8Array(buffer);
        let binary = '';
        for (const byte of bytes) {
          binary += String.fromCharCode(byte);
        }
        return btoa(binary);
      }
    }

    throw new Error('no project served the completed archive for this job');
  }, operationId);

  return Buffer.from(base64, 'base64');
}

async function watchedOperationFromThePage(page: Page): Promise<string | null> {
  return page.evaluate(() => {
    const operation = document.querySelector('[data-testid="static-blog-export-operation"]');

    return operation?.textContent?.trim() || null;
  });
}

async function downloadBytes(download: Download): Promise<Buffer> {
  const path = await download.path();

  if (!path) {
    throw new Error('the browser offered no file for the completed archive');
  }

  return readFile(path);
}

function sha256Hex(bytes: Buffer): string {
  return createHash('sha256').update(bytes).digest('hex');
}
