import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';

import { expect, test, type Download, type Page } from '@playwright/test';

/**
 * CONTENT-PACK-PANELS-001/002/003 in a real browser, against the real
 * published application and the real disposable PostgreSQL that
 * scripts/e2e.sh starts.
 *
 * This installation starts with one project, one confirmed milestone with an
 * approved story ("Pack source story") and one bare milestone with no draft
 * at all. Every pack derivation and every pack approval below goes through
 * the shipped panels and the published server; only the approved story is
 * seeded, because a story is produced by a provider call and this host calls
 * none.
 *
 * What this suite claims, and what it does not. A pass is evidence that
 * derive (LinkedIn, Xiaohongshu) or select (Medium) → preview → approve →
 * download serves bytes byte-equal to the delivered export route fetched
 * directly; that the native-publish attempt is refused in the server's own
 * `publisher_unsupported` words with no account action; that an edit after
 * approval ends the download until re-approval; that the empty states are
 * honest; and that nothing posts anywhere — an armed counter proves no POST
 * leaves before its explicit control and no request ever aims at a pack
 * host. It is NOT evidence that anything was sent to LinkedIn, Medium or
 * Xiaohongshu: no transport exists, and the suite asserts no request ever
 * aims at one.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** The milestone the seed approved a story against, by the title it created it with. */
const SEEDED_STORY_TITLE = 'Pack source story';
/** The milestone the seed left with no draft at all, by the title it created it with. */
const BARE_MILESTONE_TITLE = 'Bare pack milestone';

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

test('linkedin: derive, preview, approve, byte-equal download, verbatim refusal, nothing posts', async ({
  page,
}) => {
  await signIn(page);
  const milestone = await openContentRow(page, SEEDED_STORY_TITLE);
  const panel = milestone.getByTestId('linkedin-pack-panel');
  await expect(panel).toBeVisible();

  // The no-send boundary is stated on screen, not just absent.
  await expect(panel.getByTestId('linkedin-pack-about')).toContainText('Nothing here is sent');

  // Nothing may leave before the owner acts: arm the counters before any
  // click, and no POST may fire until its explicit control is pressed.
  const fired = armPostCounter(page);

  // Nothing derived yet: the panel says what it needs.
  await expect(panel.getByTestId('linkedin-pack-empty')).toBeVisible();
  expect(postsTo(fired, /\/api\/assets\//)).toEqual([]);

  await panel.getByTestId('linkedin-pack-cta').fill('Read the release notes.');
  await panel.getByTestId('linkedin-pack-hashtags').fill('#buildinpublic #releases');

  const [deriveResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/assets\/[^/]+\/adaptations\/linkedin$/.test(new URL(response.url()).pathname),
    ),
    panel.getByTestId('linkedin-pack-derive').click(),
  ]);

  expect(deriveResponse.status()).toBe(201);
  const derived = (await deriveResponse.json()) as { assetId: string; revision: number };
  expect(derived.assetId).toMatch(/^[0-9a-f-]{36}$/i);

  // The preview shows the server's text with its revision, hash and voice —
  // and the copy is a draft until it is approved on its own.
  await expect(panel.getByTestId('linkedin-pack-copy-text')).not.toBeEmpty();
  await expect(panel.getByTestId('linkedin-pack-review-status')).toHaveText('draft');
  await expect(panel.getByTestId('linkedin-pack-review-voice')).toContainText('voice revision');
  await expect(panel.getByTestId('linkedin-pack-download-unavailable')).toBeVisible();
  await expect(panel.getByTestId('linkedin-pack-download')).toHaveCount(0);
  expect(postsTo(fired, /\/api\/assets\//)).toEqual([
    expect.stringMatching(/\/adaptations\/linkedin$/),
  ]);

  // Approval binds this revision: the approve control is a button only a
  // press can reach, and the download appears only behind a live approval.
  await expect(panel.getByTestId('linkedin-pack-approve')).toHaveAttribute('type', 'button');
  await panel.getByTestId('linkedin-pack-approve').click();
  await expect(panel.getByTestId('linkedin-pack-review-status')).toHaveText('approved');

  // PG-TIGHTEN-FU-002 pin: the download button is enabled only when approved
  // is true, which the preceding toHaveText('approved') already proved - this
  // extra wait is the regression locator for the savePackDownload microtask
  // fix, so a future reader sees the click was wired to an enabled control.
  await expect(panel.getByTestId('linkedin-pack-download')).toBeEnabled();

  // The offered download is the server's attachment, not a re-render:
  // byte-equal to the delivered export route fetched directly.
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    panel.getByTestId('linkedin-pack-download').click(),
  ]);

  const offered = await downloadBytes(download);
  const direct = await exportBytesFromThePage(page, derived.assetId, 'linkedin');
  expect(offered.equals(direct)).toBe(true);
  expect(sha256Hex(offered)).toMatch(/^[0-9a-f]{64}$/);

  // The native publish is a refusal, not a transport: 422 in the server's
  // own words, with no account action and no socket.
  const [refusalResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/assets\/[^/]+\/adaptations\/linkedin\/publish$/.test(
          new URL(response.url()).pathname,
        ),
    ),
    panel.getByTestId('linkedin-pack-publish-attempt').click(),
  ]);

  expect(refusalResponse.status()).toBe(422);
  const refusal = (await refusalResponse.json()) as { code: string };
  expect(refusal.code).toBe('publisher_unsupported');
  await expect(panel.getByTestId('linkedin-pack-publish-refusal')).toContainText(
    'publisher_unsupported',
  );

  // The whole flow's POST footprint is exactly the three explicit controls
  // (compared by route suffix: the derive names the story id while the
  // approval and the refusal name the derived pack, so full paths sort by
  // id rather than by control).
  const suffixes = postsTo(fired, /\/api\/assets\//).map((path) =>
    path.replace(/^\/api\/assets\/[^/]+/, ''),
  );
  expect(suffixes.sort()).toEqual([
    '/adaptations/linkedin',
    '/adaptations/linkedin/publish',
    '/approve',
  ]);
  assertNothingLeaves(fired);
});

test('medium: select approved article, byte-equal download, verbatim refusal, no upstream call', async ({
  page,
}) => {
  await signIn(page);
  const milestone = await openContentRow(page, SEEDED_STORY_TITLE);
  const panel = milestone.getByTestId('medium-pack-panel');
  await expect(panel).toBeVisible();

  // Export-only is stated on screen: no API, no login, nothing leaves.
  await expect(panel.getByTestId('medium-pack-about')).toContainText('export-only');

  const fired = armPostCounter(page);

  // The approved story is offered; nothing unapproved is.
  const picker = panel.getByTestId('medium-pack-picker');
  await expect(picker.locator('option')).toHaveCount(1);
  await expect(picker.locator('option')).toContainText(['story (revision 1)']);
  expect(postsTo(fired, /\/api\/assets\//)).toEqual([]);

  // The preview shows the article text with the manifest note — and the
  // seeded story is already approved, so the download is offered at once.
  await expect(panel.getByTestId('medium-pack-article-text')).not.toBeEmpty();
  await expect(panel.getByTestId('medium-pack-manifest-note')).toContainText('manifest');
  await expect(panel.getByTestId('medium-pack-review-status')).toHaveText('approved');

  const [download] = await Promise.all([
    page.waitForEvent('download'),
    panel.getByTestId('medium-pack-download').click(),
  ]);

  // The ZIP magic proves the hand-carry package shape; byte-equality proves
  // it is the server's attachment rather than a client-side lookalike.
  const offered = await downloadBytes(download);
  expect(offered.subarray(0, 2).toString('binary')).toBe('PK');

  const storyId = await storyAssetIdFromThePage(page, SEEDED_STORY_TITLE);
  const direct = await exportBytesFromThePage(page, storyId, 'medium');
  expect(offered.equals(direct)).toBe(true);

  // Reporting publisher_unsupported opens no socket: the attempt is the only
  // POST this panel ever issues besides approval, and the answer is 422.
  const [refusalResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/assets\/[^/]+\/adaptations\/medium\/publish$/.test(
          new URL(response.url()).pathname,
        ),
    ),
    panel.getByTestId('medium-pack-publish-attempt').click(),
  ]);

  expect(refusalResponse.status()).toBe(422);
  const refusal = (await refusalResponse.json()) as { code: string };
  expect(refusal.code).toBe('publisher_unsupported');
  await expect(panel.getByTestId('medium-pack-publish-refusal')).toContainText(
    'publisher_unsupported',
  );

  expect(postsTo(fired, /\/api\/assets\//)).toEqual([
    expect.stringMatching(/\/adaptations\/medium\/publish$/),
  ]);
  assertNothingLeaves(fired);
});

test('xiaohongshu: derive, edit needs re-approval, byte-equal download, verbatim refusal', async ({
  page,
}) => {
  await signIn(page);
  const milestone = await openContentRow(page, SEEDED_STORY_TITLE);
  const panel = milestone.getByTestId('xiaohongshu-pack-panel');
  await expect(panel).toBeVisible();

  // The human-publishes boundary is stated on screen: no login, no auto-post.
  await expect(panel.getByTestId('xiaohongshu-pack-about')).toContainText('No account is logged in');

  const fired = armPostCounter(page);

  await expect(panel.getByTestId('xiaohongshu-pack-empty')).toBeVisible();

  // An empty title derives under the milestone title, which is the
  // renderer's first derived candidate — so it is chosen, not invented.
  await panel.getByTestId('xiaohongshu-pack-tags').fill('#buildinpublic #releases');
  await panel.getByTestId('xiaohongshu-pack-cover').fill('A note on the release pipeline.');
  await panel.getByTestId('xiaohongshu-pack-cta').fill('Share how you verify releases.');

  const [deriveResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/assets\/[^/]+\/adaptations\/xiaohongshu$/.test(new URL(response.url()).pathname),
    ),
    panel.getByTestId('xiaohongshu-pack-derive').click(),
  ]);

  expect(deriveResponse.status()).toBe(201);
  const derived = (await deriveResponse.json()) as {
    assetId: string;
    selectedTitle: string;
    titleCandidates: string[];
  };
  expect(derived.selectedTitle).toBe(SEEDED_STORY_TITLE);
  expect(derived.titleCandidates).toContain(SEEDED_STORY_TITLE);

  await expect(panel.getByTestId('xiaohongshu-pack-candidates')).toContainText(
    SEEDED_STORY_TITLE,
  );
  await expect(panel.getByTestId('xiaohongshu-pack-body-text')).not.toBeEmpty();
  await expect(panel.getByTestId('xiaohongshu-pack-review-status')).toHaveText('draft');

  await panel.getByTestId('xiaohongshu-pack-approve').click();
  await expect(panel.getByTestId('xiaohongshu-pack-review-status')).toHaveText('approved');

  // An edit after approval ends the download until re-approval: export needs
  // a live approval per contract, and the panel names the state honestly.
  await panel.getByTestId('xiaohongshu-pack-edit-body').fill('Edited pack body for re-approval.');
  await panel.getByTestId('xiaohongshu-pack-edit-save').click();
  await expect(panel.getByTestId('xiaohongshu-pack-review-revision')).toContainText('Revision 2');
  await expect(panel.getByTestId('xiaohongshu-pack-review-status')).toHaveText('invalidated');
  await expect(panel.getByTestId('xiaohongshu-pack-download')).toHaveCount(0);
  await expect(panel.getByTestId('xiaohongshu-pack-download-unavailable')).toBeVisible();

  await panel.getByTestId('xiaohongshu-pack-approve').click();
  await expect(panel.getByTestId('xiaohongshu-pack-review-status')).toHaveText('approved');

  const [download] = await Promise.all([
    page.waitForEvent('download'),
    panel.getByTestId('xiaohongshu-pack-download').click(),
  ]);

  const offered = await downloadBytes(download);
  const direct = await exportBytesFromThePage(page, derived.assetId, 'xiaohongshu');
  expect(offered.equals(direct)).toBe(true);

  const [refusalResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/assets\/[^/]+\/adaptations\/xiaohongshu\/publish$/.test(
          new URL(response.url()).pathname,
        ),
    ),
    panel.getByTestId('xiaohongshu-pack-publish-attempt').click(),
  ]);

  expect(refusalResponse.status()).toBe(422);
  const refusal = (await refusalResponse.json()) as { code: string };
  expect(refusal.code).toBe('publisher_unsupported');
  await expect(panel.getByTestId('xiaohongshu-pack-publish-refusal')).toContainText(
    'publisher_unsupported',
  );

  assertNothingLeaves(fired);
});

test('a milestone with no story states every empty case honestly', async ({ page }) => {
  await signIn(page);
  const milestone = await openContentRow(page, BARE_MILESTONE_TITLE);

  await expect(milestone.getByTestId('linkedin-pack-panel')).toBeVisible();
  await expect(milestone.getByTestId('medium-pack-panel')).toBeVisible();
  await expect(milestone.getByTestId('xiaohongshu-pack-panel')).toBeVisible();

  await expect(milestone.getByTestId('linkedin-pack-no-story')).toContainText(
    'nothing to derive a copy from',
  );
  await expect(milestone.getByTestId('medium-pack-empty')).toContainText('No approved article');
  await expect(milestone.getByTestId('xiaohongshu-pack-no-story')).toContainText(
    'nothing to pack from',
  );

  // No derive control is offered where there is nothing to derive from.
  await expect(milestone.getByTestId('linkedin-pack-derive')).toHaveCount(0);
  await expect(milestone.getByTestId('xiaohongshu-pack-derive')).toHaveCount(0);
});

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/**
 * Opens the project the milestone with this title lives in on the Content
 * area, and returns that milestone's row — the same route the
 * content-asset-variants suite reads, because a fixture that opened the
 * wrong project would assert about a record it never looked at.
 */
async function openContentRow(page: Page, title: string) {
  const found = await locateMilestone(page, title);

  if (found === null) {
    throw new Error(`no milestone titled "${title}" exists in this installation`);
  }

  await page.goto(`/projects/${found.projectId}`);
  await expect(page.getByTestId('project-name')).toBeVisible();

  await page.getByTestId('project-area-content').click();

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

async function storyAssetIdFromThePage(page: Page, title: string): Promise<string> {
  return page.evaluate(async (wanted) => {
    const projects = await fetch('/api/projects', { credentials: 'same-origin' });
    const catalogue = (await projects.json()) as { readonly items: readonly { readonly id: string }[] };

    for (const project of catalogue.items) {
      const milestones = await fetch(`/api/projects/${project.id}/milestones`, {
        credentials: 'same-origin',
      });

      if (!milestones.ok) {
        continue;
      }

      const listing = (await milestones.json()) as {
        readonly items: readonly { readonly id: string; readonly title: string }[];
      };

      for (const milestone of listing.items) {
        if (milestone.title !== wanted) {
          continue;
        }

        const draft = await fetch(`/api/milestones/${milestone.id}/drafts`, {
          credentials: 'same-origin',
        });

        if (!draft.ok) {
          continue;
        }

        const body = (await draft.json()) as { readonly assetId: string | null };

        if (body.assetId !== null) {
          return body.assetId;
        }
      }
    }

    throw new Error(`no story asset for milestone "${wanted}"`);
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
 * Nothing posts anywhere: every request stayed on the loopback application,
 * and every POST went to a delivered pack or review route. Called at the end
 * of each flow, when the full footprint is known.
 */
function assertNothingLeaves(fired: { posts: string[]; remote: string[] }) {
  expect(fired.remote, 'a request left the loopback application').toEqual([]);

  for (const path of fired.posts) {
    expect(path, `unexpected POST to ${path}`).toMatch(
      /\/api\/assets\/[^/]+\/(adaptations\/(linkedin|medium|xiaohongshu)(\/publish)?|approve|unapprove)$/,
    );
  }
}

async function exportBytesFromThePage(
  page: Page,
  assetId: string,
  format: 'linkedin' | 'medium' | 'xiaohongshu',
): Promise<Buffer> {
  const base64 = await page.evaluate(
    async ({ id, kind }) => {
      const response = await fetch(`/api/assets/${id}/export?format=${kind}`, {
        credentials: 'same-origin',
      });

      if (!response.ok) {
        throw new Error(`the export route answered ${response.status} for format ${kind}`);
      }

      const buffer = await response.arrayBuffer();
      const bytes = new Uint8Array(buffer);
      let binary = '';

      for (const byte of bytes) {
        binary += String.fromCharCode(byte);
      }

      return btoa(binary);
    },
    { id: assetId, kind: format },
  );

  return Buffer.from(base64, 'base64');
}

async function downloadBytes(download: Download): Promise<Buffer> {
  const path = await download.path();

  if (!path) {
    throw new Error('the browser offered no file for the approved pack');
  }

  return readFile(path);
}

function sha256Hex(bytes: Buffer): string {
  return createHash('sha256').update(bytes).digest('hex');
}
