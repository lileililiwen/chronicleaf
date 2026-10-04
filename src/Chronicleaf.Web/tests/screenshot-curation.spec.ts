import { expect, test, type Page } from '@playwright/test';

/**
 * SCREENSHOT-CURATION-001/002 in a real browser, against the real published application and the real
 * disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API. This installation starts with one project, one milestone, one evidence
 * bundle and three offered candidates, all present before the first click: the project, the
 * milestone, its evidence and its media were created through the running server's own routes with
 * the real access token, and the three candidate rows were inserted the way the seed for the
 * grounded-draft suite inserts its rows - directly, with the rows read back and refused when they
 * are not there. Only the candidate rows are seeded, and that is the same technique for the same
 * reason: a candidate is produced by a browser, and this host cannot confine one.
 *
 * <b>What the seeded pictures are, stated up front.</b> The bytes behind all three candidates are
 * one real 1x1 PNG uploaded through the real media route and verified by the delivered store - a
 * fixture, never presented as something a browser photographed. What the suite asserts about those
 * bytes is only that the panel draws the pictures the server serves. Every choice below - select,
 * caption, primary, reject, reorder - is made by the same clicks an owner would make, and every
 * claim is read back from the page or from the server's own answer.
 *
 * A passing run here is therefore evidence that the picker records a human choice faithfully and
 * publishes it into evidence - and is NOT evidence that a screenshot was captured on this host.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;
const PROJECT_ID = process.env.CHRONICLEAF_E2E_CURATION_PROJECT;
const MILESTONE_ID = process.env.CHRONICLEAF_E2E_CURATION_MILESTONE;

test.beforeEach(async ({ page }) => {
  if (!ACCESS_TOKEN) {
    throw new Error(
      'CHRONICLEAF_E2E_ACCESS_TOKEN is not set. Run these fixtures through scripts/e2e.sh, which ' +
        'starts the published application with a real access token hash and a real database.',
    );
  }

  if (!PROJECT_ID || !MILESTONE_ID) {
    throw new Error(
      'CHRONICLEAF_E2E_CURATION_PROJECT and CHRONICLEAF_E2E_CURATION_MILESTONE are not set. Run ' +
        'these fixtures through scripts/e2e.sh, which seeds the project and milestone they name.',
    );
  }

  await page.context().clearCookies();
  await page.goto('/');
});

test('an offered milestone shows three candidates and no selection', async ({ page }) => {
  await openProject(page);

  const panel = page.getByTestId('curation-panel');
  await expect(panel).toBeVisible();
  await expect(panel.getByTestId('curation-heading')).toContainText('Selected screenshots');

  // Three offered pictures, each with its provenance and each unoffered as evidence.
  await expect(panel.getByTestId('curation-item')).toHaveCount(3);
  await expect(panel.getByTestId('curation-none-selected')).toBeVisible();
  await expect(panel.getByTestId('curation-none-selected')).toContainText(
    "Only a picture its owner selects enters this milestone's evidence",
  );

  for (const status of await panel.getByTestId('curation-item-status').allTextContents()) {
    expect(status).toBe('candidate');
  }

  // The pictures are drawn from the delivered media route: the bytes the server serves, not a
  // placeholder the panel invented.
  await expect(panel.getByTestId('curation-item-image')).toHaveCount(3);

  // And the server agrees that nothing is selected and the evidence holds one bundle.
  expect(await readEvidenceRevision(page)).toBe(1);
});

test('selecting with a caption publishes evidence and switching the primary moves the badge', async ({
  page,
}) => {
  await openProject(page);

  // A select moves the picture from the offered section into the selected one, so every
  // assertion below re-queries inside the retry: a locator captured before a move names whatever
  // moved into its position, and polling that position watches the wrong picture.
  const desktop = await pricingItem(page, 'desktop');

  // Select the desktop picture with a caption. One human action, one PATCH carrying both.
  await desktop.getByTestId('curation-item-caption-input').fill('The pricing table.');
  await desktop.getByTestId('curation-item-select').click();

  await expect
    .poll(async () => itemStatus(page, 'pricing', 'desktop'), { timeout: 15_000 })
    .toBe('selected');
  await expect
    .poll(async () => itemCaption(page, 'pricing', 'desktop'), { timeout: 15_000 })
    .toContain('The pricing table.');
  expect(await readEvidenceRevision(page)).toBe(2);

  // The evidence the selection published references the picture's media and its caption.
  const evidence = await readEvidence(page);
  expect(evidence.status).toBe(200);
  const screenshots = evidence.body.items[0].items.filter(
    (item: { kind: string }) => item.kind === 'screenshot',
  );
  expect(screenshots).toHaveLength(1);
  expect(screenshots[0].text).toBe('The pricing table.');
  expect(screenshots[0].mediaId).toBe(await mediaIdOf(page, 'desktop'));

  // Make it the primary picture.
  const primary = await pricingItem(page, 'desktop');
  await primary.getByTestId('curation-item-primary').click();
  await expect
    .poll(async () => primaryViewport(page), { timeout: 15_000 })
    .toBe('desktop');
  await expect
    .poll(async () => primaryBadgeCount(page), { timeout: 15_000 })
    .toBe(1);

  // Select the mobile picture without a caption, then move the primary to it. The badge moves in
  // the same server transaction that clears the old one, so no read between the two statements
  // could have shown both - or neither.
  const mobile = await pricingItem(page, 'mobile');
  await mobile.getByTestId('curation-item-select').click();

  await expect
    .poll(async () => itemStatus(page, 'pricing', 'mobile'), { timeout: 15_000 })
    .toBe('selected');
  expect(await readEvidenceRevision(page)).toBe(3);

  const selected = await pricingItem(page, 'mobile');
  await selected.getByTestId('curation-item-primary').click();
  await expect
    .poll(async () => primaryViewport(page), { timeout: 15_000 })
    .toBe('mobile');
  await expect
    .poll(async () => primaryBadgeCount(page), { timeout: 15_000 })
    .toBe(1);

  // A primary flip moves no evidence: the bundle still holds both selected pictures.
  expect(await readEvidenceRevision(page)).toBe(3);
});

test('rejecting removes a candidate from the choice and a stale change is refused', async ({
  page,
}) => {
  await openProject(page);

  // The earlier scenario selected two pictures and advanced the evidence to revision 3. Rejecting
  // the changelog picture - which was never selected - changes no bundle: it marks the row
  // without publishing anything.
  const changelog = await changelogItem(page);

  await changelog.getByTestId('curation-item-reject').click();

  await expect
    .poll(async () => itemStatus(page, 'changelog', 'desktop'), { timeout: 15_000 })
    .toBe('rejected');
  expect(await readEvidenceRevision(page)).toBe(3);

  // A change decided against a revision that has already moved is refused, and the panel says so
  // in words rather than leaving the choice looking applied.
  const refusal = await stalePatchFromThePage(page);
  expect(refusal.status).toBe(409);
  expect(refusal.body.code).toBe('revision_conflict');

  // The refused choice changed nothing: the rejected picture is still rejected, and the evidence
  // revision is still the one the earlier scenario left.
  await expect
    .poll(async () => itemStatus(page, 'changelog', 'desktop'), { timeout: 15_000 })
    .toBe('rejected');
  expect(await readEvidenceRevision(page)).toBe(3);
});

test('reordering the selected pictures persists across a reload', async ({ page }) => {
  await openProject(page);

  // The earlier scenarios left the desktop and mobile pictures selected. Whatever their current
  // sequence is, moving the first one down swaps exactly the two - and the order endpoint answers
  // the stored sequence rather than the panel guessing it.
  await expect
    .poll(async () => selectedViewports(page), { timeout: 15_000 })
    .toHaveLength(2);
  const before = await selectedViewports(page);

  const panel = page.getByTestId('curation-panel');
  const first = panel.getByTestId('curation-selected').getByTestId('curation-item').first();
  await first.getByTestId('curation-item-move-down').click();

  await expect.poll(async () => selectedViewports(page), { timeout: 10_000 }).toEqual([
    before[1],
    before[0],
  ]);

  // Stored, not just answered: a reload serves the same sequence.
  await page.reload();
  await expect(page.getByTestId('curation-panel')).toBeVisible();
  await expect
    .poll(async () => selectedViewports(page), { timeout: 10_000 })
    .toEqual([before[1], before[0]]);

  // Ordering moves no evidence: it sequences the rows, and the bundle is untouched.
  expect(await readEvidenceRevision(page)).toBe(3);
});

// ===========================================================================
// Helpers.
// ===========================================================================

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

async function openProject(page: Page) {
  await signIn(page);
  await page.goto(`/projects/${PROJECT_ID as string}`);
  await expect(page.getByTestId('curation-panel')).toBeVisible();
}

/**
 * The pricing-page picture at one viewport, found by what the server stored rather than by list
 * position: the two candidates share a capture instant and the order between them is not a
 * contract this fixture may lean on.
 */
async function pricingItem(page: Page, viewport: 'desktop' | 'mobile') {
  const panel = page.getByTestId('curation-panel');
  const items = panel.getByTestId('curation-item');

  // The list loads after the panel mounts, and a count does not wait: expect the rows first so
  // the search below runs against what the server holds rather than against an empty list.
  await expect(items.first()).toBeVisible({ timeout: 15_000 });
  const count = await items.count();

  for (let index = 0; index < count; index++) {
    const item = items.nth(index);
    const provenance = await item.getByTestId('curation-item-source').textContent();

    if (provenance?.includes('pricing') !== true) {
      continue;
    }

    const viewportText = await item.getByTestId('curation-item-viewport').textContent();

    if (viewportText?.startsWith(viewport) === true) {
      return item;
    }
  }

  throw new Error(`no pricing-page ${viewport} candidate is on screen`);
}

async function changelogItem(page: Page) {
  const panel = page.getByTestId('curation-panel');
  const items = panel.getByTestId('curation-item');

  await expect(items.first()).toBeVisible({ timeout: 15_000 });
  const count = await items.count();

  for (let index = 0; index < count; index++) {
    const item = items.nth(index);
    const provenance = await item.getByTestId('curation-item-source').textContent();

    if (provenance?.includes('changelog') === true) {
      return item;
    }
  }

  throw new Error('no changelog candidate is on screen');
}

/** The viewports of the selected pictures, in the sequence the panel serves them. */
async function selectedViewports(page: Page): Promise<readonly string[]> {
  const selected = page.getByTestId('curation-selected').getByTestId('curation-item-viewport');

  return (await selected.allTextContents()).map((text) =>
    text.startsWith('mobile') ? 'mobile' : 'desktop',
  );
}

/**
 * Finds one picture by what the server stored - the source fragment and the viewport - and reads
 * one of its rendered values. Every call re-queries from the panel root, so callers inside a
 * poll never watch a position the picture has since left.
 */
async function findItem(page: Page, source: string, viewport: 'desktop' | 'mobile') {
  const items = page.getByTestId('curation-panel').getByTestId('curation-item');
  const count = await items.count();

  for (let index = 0; index < count; index++) {
    const item = items.nth(index);
    const provenance = await item.getByTestId('curation-item-source').textContent();

    if (provenance?.includes(source) !== true) {
      continue;
    }

    const viewportText = await item.getByTestId('curation-item-viewport').textContent();

    if (viewportText?.startsWith(viewport) === true) {
      return item;
    }
  }

  return null;
}

/** One picture's status, or null while it is not on screen. */
async function itemStatus(
  page: Page,
  source: string,
  viewport: 'desktop' | 'mobile',
): Promise<string | null> {
  const item = await findItem(page, source, viewport);

  return item === null ? null : await item.getByTestId('curation-item-status').textContent();
}

/** One picture's rendered caption, or null while it is not on screen. */
async function itemCaption(
  page: Page,
  source: string,
  viewport: 'desktop' | 'mobile',
): Promise<string | null> {
  const item = await findItem(page, source, viewport);

  if (item === null) {
    return null;
  }

  if ((await item.getByTestId('curation-item-caption').count()) === 0) {
    return null;
  }

  return item.getByTestId('curation-item-caption').textContent();
}

/** The viewport of the picture carrying the primary badge, or null while none does. */
async function primaryViewport(page: Page): Promise<string | null> {
  const badges = page.getByTestId('curation-panel').getByTestId('curation-item-primary-badge');

  if ((await badges.count()) === 0) {
    return null;
  }

  const item = badges.first().locator('xpath=ancestor::li[@data-testid="curation-item"][1]');
  const viewportText = await item.getByTestId('curation-item-viewport').textContent();

  if (viewportText?.startsWith('mobile') === true) {
    return 'mobile';
  }

  if (viewportText?.startsWith('desktop') === true) {
    return 'desktop';
  }

  return null;
}

/** How many primary badges are on screen. Exactly one, whenever one is. */
async function primaryBadgeCount(page: Page): Promise<number> {
  return page.getByTestId('curation-panel').getByTestId('curation-item-primary-badge').count();
}

interface EvidenceBody {
  revision: number;
  items: ReadonlyArray<{
    revision: number;
    items: ReadonlyArray<{ id: string; kind: string; text: string; mediaId: string | null }>;
  }>;
}

async function readEvidence(page: Page): Promise<{ status: number; body: EvidenceBody }> {
  return page.evaluate(async (id): Promise<{ status: number; body: EvidenceBody }> => {
    const response = await fetch(`/api/milestones/${id}/evidence`, { credentials: 'same-origin' });

    return { status: response.status, body: (await response.json()) as EvidenceBody };
  }, MILESTONE_ID);
}

async function readEvidenceRevision(page: Page): Promise<number> {
  const evidence = await readEvidence(page);

  expect(evidence.status).toBe(200);

  return evidence.body.revision;
}

/** The media id of one viewport's candidate, read from the server rather than the page. */
async function mediaIdOf(page: Page, viewport: string): Promise<string> {
  const candidates = await page.evaluate(async (id) => {
    const response = await fetch(`/api/milestones/${id}/screenshots`, {
      credentials: 'same-origin',
    });

    return (await response.json()) as {
      items: ReadonlyArray<{ viewport: string; mediaId: string }>;
    };
  }, MILESTONE_ID);

  const found = candidates.items.find((item) => item.viewport === viewport);

  if (!found) {
    throw new Error(`no ${viewport} candidate is stored`);
  }

  return found.mediaId;
}

/**
 * A curation decided against a revision that has already moved, sent through the browser's own
 * session: the server must refuse it with 409 rather than silently rebasing it.
 */
async function stalePatchFromThePage(page: Page) {
  return page.evaluate(async (id): Promise<{ status: number; body: { code: string } }> => {
    const list = await fetch(`/api/milestones/${id}/screenshots`, { credentials: 'same-origin' });
    const page = (await list.json()) as {
      items: ReadonlyArray<{ id: string; revision: number }>;
    };
    const first = page.items[0];

    if (!first) {
      throw new Error('the seed holds no candidate to curate');
    }

    const antiforgery = (await (
      await fetch('/api/session/antiforgery', { credentials: 'same-origin' })
    ).json()) as { headerName: string; token: string };

    // A screenshot revision of zero against rows that have all moved at least once: stale by
    // construction, whatever the suite before this one did. The caption carries a fresh nonce so
    // the request is always a change: a request that happens to describe the stored state is a
    // no-op 200 rather than a conflict, which is the honest answer for a repeated choice.
    const nonce = Math.random().toString(36).slice(2);
    const response = await fetch(`/api/screenshots/${first.id}`, {
      method: 'PATCH',
      credentials: 'same-origin',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        [antiforgery.headerName]: antiforgery.token,
        'If-Match': '0',
      },
      body: JSON.stringify({
        caption: `stale words ${nonce}`,
        expectedRevision: 0,
        expectedBundleRevision: 0,
      }),
    });

    return { status: response.status, body: (await response.json()) as { code: string } };
  }, MILESTONE_ID);
}
