import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';

import { expect, test, type Download, type Page } from '@playwright/test';

/**
 * VIDEO-STORY-PANEL-001/002 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh
 * starts.
 *
 * This installation starts with one project, one confirmed milestone with an
 * approved story ("Storyboard source") and one selected screenshot, plus one
 * bare milestone with no story at all. Every script generation and every
 * script approval below goes through the shipped panel and the published
 * server; only the approved story is seeded, because a story is produced by
 * a provider call and this host calls none.
 *
 * What this suite claims, and what it does not. A pass is evidence that
 * pick duration + source material → Generate → shot-order preview → approve
 * → download serves bytes byte-equal to the delivered export route fetched
 * directly; that the pre-approval and post-regenerate downloads are refused
 * in the server's own `approval_required`/`approval_stale` words; that the
 * native-publish attempt is refused in the server's own
 * `publisher_unsupported` words with no account action; that the empty
 * states are honest; and that nothing fires before its explicit control and
 * no request ever aims at a video host. It is NOT evidence that any video
 * was synthesized, any footage was rendered, or anything was published: no
 * renderer, encoder or transport exists, and the suite asserts no request
 * ever aims at one.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** The milestone the seed gave an approved story and one selected screenshot. */
const STORY_MILESTONE_TITLE = 'Storyboard source';
/** The milestone the seed left with no story at all. */
const BARE_MILESTONE_TITLE = 'Bare storyboard milestone';

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

test('storyboard: select, preview, approve, byte-equal download, verbatim refusals, nothing fires early', async ({
  page,
}) => {
  await signIn(page);
  const milestone = await openEvolutionRow(page, STORY_MILESTONE_TITLE);
  const panel = milestone.getByTestId('video-story-panel');
  await expect(panel).toBeVisible();

  // The no-synthesis boundary is stated on screen, not just absent.
  await expect(panel.getByTestId('video-story-about')).toContainText(
    'no video is synthesized',
  );
  await expect(panel.getByTestId('video-story-about')).toContainText('nothing is published');

  // Nothing may leave before the owner acts: arm the counters before any
  // click, and no POST may fire until its explicit control is pressed.
  const fired = armPostCounter(page);

  // No script yet: the panel says what it needs.
  await expect(panel.getByTestId('video-story-no-script')).toBeVisible();
  expect(postsTo(fired, /\/api\/milestones\//)).toEqual([]);

  // Duration over a select, source material over delivered pickers: the one
  // selected screenshot is offered and nothing is typed as an id.
  await panel.getByTestId('video-story-duration').selectOption('15');

  await panel.getByTestId('video-story-shot-add').click();
  await panel.getByTestId('video-story-shot-0-caption').fill('The pipeline digest view.');
  await panel
    .getByTestId('video-story-shot-0-narration')
    .fill('The pipeline now reproduces from a clean checkout.');
  await panel.getByTestId('video-story-shot-0-transition').selectOption('cut');
  await expect(
    panel.locator('[data-testid^="video-story-shot-0-evidence-"]:checked'),
  ).not.toHaveCount(0);
  await expect(panel.getByTestId('video-story-shot-0-screenshot').locator('option')).toHaveCount(
    1,
  );

  await panel.getByTestId('video-story-shot-add').click();
  await panel
    .getByTestId('video-story-shot-1-caption')
    .fill('The pipeline digest view at the end of the run.');
  await panel
    .getByTestId('video-story-shot-1-narration')
    .fill('The build ran twice and produced the same digest.');
  await panel.getByTestId('video-story-shot-1-transition').selectOption('fade');
  await expect(
    panel.locator('[data-testid^="video-story-shot-1-evidence-"]:checked'),
  ).not.toHaveCount(0);

  // Timings are derived, never typed: two shots split fifteen seconds exactly.
  await expect(panel.getByTestId('video-story-shot-0-timing')).toContainText(
    '00:00:00,000',
  );
  await expect(panel.getByTestId('video-story-shot-1-timing')).toContainText(
    '00:00:15,000',
  );

  const [generateResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/milestones\/[^/]+\/video-stories$/.test(new URL(response.url()).pathname),
    ),
    panel.getByTestId('video-story-generate').click(),
  ]);

  expect(generateResponse.status()).toBe(201);
  const generated = (await generateResponse.json()) as {
    assetId: string;
    revision: number;
    durationSeconds: number;
    shotCount: number;
  };
  expect(generated.assetId).toMatch(/^[0-9a-f-]{36}$/i);
  expect(generated.revision).toBe(1);
  expect(generated.durationSeconds).toBe(15);
  expect(generated.shotCount).toBe(2);

  // The preview shows the stored shot order with screenshots, timings,
  // subtitles, narration and transitions — and the script is a draft until
  // it is approved on its own.
  await expect(panel.getByTestId('video-story-review-status')).toHaveText('draft');
  await expect(panel.getByTestId('video-story-review-revision')).toContainText('Revision 1');
  await expect(panel.getByTestId('video-story-review-voice')).toContainText('voice revision');
  await expect(panel.getByTestId('video-story-review-shot-0-timing')).toContainText('cut');
  await expect(panel.getByTestId('video-story-review-shot-0-caption')).not.toBeEmpty();
  await expect(panel.getByTestId('video-story-review-shot-0-narration')).not.toBeEmpty();
  await expect(panel.getByTestId('video-story-review-shot-0-evidence')).not.toBeEmpty();
  await expect(panel.getByTestId('video-story-review-shot-0-image')).toBeVisible();
  await expect(panel.getByTestId('video-story-review-shot-1-timing')).toContainText('fade');
  await expect(panel.getByTestId('video-story-download-gated')).toBeVisible();
  expect(postsTo(fired, /\/api\/milestones\//)).toEqual([
    expect.stringMatching(/\/video-stories$/),
  ]);

  // The download without a live approval is the server's refusal, not a
  // file: approval_required verbatim.
  await panel.getByTestId('video-story-download').click();
  await expect(panel.getByTestId('video-story-download-failure')).toContainText(
    'approval_required',
  );

  // Approval binds this revision: the approve control is a button only a
  // press can reach, and the exact bytes download only behind a live approval.
  await expect(panel.getByTestId('video-story-approve')).toHaveAttribute('type', 'button');
  await panel.getByTestId('video-story-approve').click();
  await expect(panel.getByTestId('video-story-review-status')).toHaveText('approved');

  const [download] = await Promise.all([
    page.waitForEvent('download'),
    panel.getByTestId('video-story-download').click(),
  ]);

  // The offered download is the server's attachment, not a re-render:
  // byte-equal to the delivered export route fetched directly.
  const offered = await downloadBytes(download);
  expect(offered.subarray(0, 2).toString('binary')).toBe('PK');
  const direct = await exportBytesFromThePage(page, generated.assetId);
  expect(offered.equals(direct)).toBe(true);
  expect(sha256Hex(offered)).toMatch(/^[0-9a-f]{64}$/);

  // The native video publish is a refusal, not a transport: 422 in the
  // server's own words, with no account action and no socket.
  const [refusalResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/assets\/[^/]+\/video-story\/publish$/.test(new URL(response.url()).pathname),
    ),
    panel.getByTestId('video-story-publish-attempt').click(),
  ]);

  expect(refusalResponse.status()).toBe(422);
  const refusal = (await refusalResponse.json()) as { code: string };
  expect(refusal.code).toBe('publisher_unsupported');
  await expect(panel.getByTestId('video-story-publish-refusal')).toContainText(
    'publisher_unsupported',
  );

  // Regenerating appends a revision that starts unapproved: the download is
  // refused approval_stale verbatim until re-approval re-opens it.
  await panel
    .getByTestId('video-story-shot-0-narration')
    .fill('The pipeline now reproduces from a clean checkout every run.');

  const [regenerateResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/milestones\/[^/]+\/video-stories$/.test(new URL(response.url()).pathname),
    ),
    panel.getByTestId('video-story-generate').click(),
  ]);
  expect(regenerateResponse.status()).toBe(201);
  await expect(panel.getByTestId('video-story-review-revision')).toContainText('Revision 2');
  await expect(panel.getByTestId('video-story-review-status')).toHaveText('invalidated');

  await panel.getByTestId('video-story-download').click();
  await expect(panel.getByTestId('video-story-download-failure')).toContainText(
    'approval_stale',
  );

  await panel.getByTestId('video-story-approve').click();
  await expect(panel.getByTestId('video-story-review-status')).toHaveText('approved');

  const [secondDownload] = await Promise.all([
    page.waitForEvent('download'),
    panel.getByTestId('video-story-download').click(),
  ]);
  const secondOffered = await downloadBytes(secondDownload);
  const secondDirect = await exportBytesFromThePage(page, generated.assetId);
  expect(secondOffered.equals(secondDirect)).toBe(true);

  // The whole flow's POST footprint is exactly the explicit controls: two
  // scripts, two approvals and one refused publish.
  const suffixes = postsTo(fired, /\/api\/(assets|milestones)\//).map((path) =>
    path.replace(/^\/api\/(assets|milestones)\/[^/]+/, ''),
  );
  expect(suffixes.sort()).toEqual([
    '/approve',
    '/approve',
    '/video-stories',
    '/video-stories',
    '/video-story/publish',
  ]);
  assertNothingLeaves(fired);
});

test('a milestone with no story states every empty case honestly', async ({ page }) => {
  await signIn(page);
  const milestone = await openEvolutionRow(page, BARE_MILESTONE_TITLE);
  const panel = milestone.getByTestId('video-story-panel');
  await expect(panel).toBeVisible();

  await expect(panel.getByTestId('video-story-no-story')).toContainText(
    'nothing to script a storyboard from',
  );
  await expect(panel.getByTestId('video-story-no-script')).toContainText(
    'No storyboard scripted yet',
  );

  // No generate control is offered where there is nothing to script from.
  await expect(panel.getByTestId('video-story-generate')).toHaveCount(0);
  await expect(panel.getByTestId('video-story-duration')).toHaveCount(0);
});

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/**
 * Opens the project the milestone with this title lives in on the Evolution
 * area, and returns that milestone's row — the same route the evolution
 * suites read, because a fixture that opened the wrong project would assert
 * about a record it never looked at.
 */
async function openEvolutionRow(page: Page, title: string) {
  const found = await locateMilestone(page, title);

  if (found === null) {
    throw new Error(`no milestone titled "${title}" exists in this installation`);
  }

  await page.goto(`/projects/${found.projectId}`);
  await expect(page.getByTestId('project-name')).toBeVisible();

  await page.getByTestId('project-area-evolution').click();

  const milestone = row(page, title);
  await expect(milestone).toBeVisible();
  return milestone;
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

/**
 * Counts the POSTs the page issues from here on. A panel that merely renders
 * must not post: the paths are read after the clicks, never awaited.
 */
function armPostCounter(page: Page): { posts: string[]; remote: string[] } {
  const fired = { posts: [] as string[], remote: [] as string[] };

  page.on('request', (request) => {
    const url = new URL(request.url());

    if (url.hostname !== '127.0.0.1' && url.hostname !== 'localhost') {
      fired.remote.push(request.url());
    }

    if (request.method() === 'POST') {
      fired.posts.push(url.pathname);
    }
  });

  return fired;
}

/** The POST paths fired so far whose pathname matches this pattern. */
function postsTo(fired: { posts: string[] }, pattern: RegExp): string[] {
  return fired.posts.filter((path) => pattern.test(path));
}

/**
 * Nothing synthesizes and nothing is sent: every request stayed on the
 * loopback application, and every POST went to a delivered video-story or
 * review route. Called at the end of the flow, when the full footprint is
 * known.
 */
function assertNothingLeaves(fired: { posts: string[]; remote: string[] }) {
  expect(fired.remote, 'a request left the loopback application').toEqual([]);

  for (const path of fired.posts) {
    expect(path, `unexpected POST to ${path}`).toMatch(
      /\/api\/(assets|milestones)\/[^/]+\/(video-stories|video-story\/publish|approve|unapprove)$/,
    );
  }
}

async function exportBytesFromThePage(page: Page, assetId: string): Promise<Buffer> {
  const base64 = await page.evaluate(async (id) => {
    const response = await fetch(`/api/assets/${id}/export?format=video-story`, {
      credentials: 'same-origin',
    });

    if (!response.ok) {
      throw new Error(`the export route answered ${response.status} for format video-story`);
    }

    const buffer = await response.arrayBuffer();
    const bytes = new Uint8Array(buffer);
    let binary = '';

    for (const byte of bytes) {
      binary += String.fromCharCode(byte);
    }

    return btoa(binary);
  }, assetId);

  return Buffer.from(base64, 'base64');
}

async function downloadBytes(download: Download): Promise<Buffer> {
  const path = await download.path();

  if (!path) {
    throw new Error('the browser offered no file for the approved storyboard');
  }

  return readFile(path);
}

function sha256Hex(bytes: Buffer): string {
  return createHash('sha256').update(bytes).digest('hex');
}
