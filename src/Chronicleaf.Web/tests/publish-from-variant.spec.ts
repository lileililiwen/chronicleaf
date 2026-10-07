import { expect, test, type Page } from '@playwright/test';

/**
 * PUBLISH-FROM-VARIANT-001/002/003 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API: this installation starts with one project, one confirmed
 * milestone, one evidence bundle, one approved story and one sealed `publisher`
 * credential, all written by the suite seed in scripts/e2e.sh. Every derive, approval,
 * preview and publish below goes through the shipped panels and the published server.
 *
 * What this suite claims, and what it does not. A pass is evidence that an approved
 * variant is published by clicking - pick variant, pick target, preview, confirm - and
 * that the receipt names the server's answer; that refusals arrive in the server's own
 * words; that the publication and journey pickers list what the delivered routes serve;
 * and that nothing previewed or published before the owner acted. It is NOT evidence
 * that anything was sent anywhere: the dispatch loop is silenced for this installation,
 * so the receipt stays at the acceptance the request committed, and the run half is the
 * .NET suite's claim, not this one's.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** The milestone the seed approved a story against, by the title it created it with. */
const SEEDED_STORY_TITLE = 'Dispatch source story';

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

test('pick variant, pick target, preview, confirm, and the receipt names the server answer', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_STORY_TITLE);

  const panel = row(page, SEEDED_STORY_TITLE).getByTestId('variant-panel');
  await expect(panel).toBeVisible();

  // Nothing may leave before the owner acts: arm the counter before deriving, and no
  // preview or publish POST may fire until the preview button is pressed.
  const fired = armPublishCounter(page);

  await panel.getByTestId('variant-format').selectOption('changelog');
  await panel.getByTestId('variant-derive').click();
  await expect(panel.getByTestId('variant-select-changelog')).toBeVisible();

  await panel.getByTestId('variant-approve').click();
  await expect(panel.getByTestId('variant-preview-status')).toHaveText('approved');

  // A draft offers no publish control; an approved row does. The Publish action selects
  // the row and reveals the shared confirm flow bound to its own approval.
  await panel.getByTestId('variant-publish-changelog').click();
  const section = panel.getByTestId('variant-publish-section');
  await expect(section).toBeVisible();
  await expect(section.getByTestId('variant-publish-approval-input')).not.toHaveValue('');

  // The target picker lists what the target route serves, including the configured
  // transport this installation registered.
  await expect(section.getByTestId('variant-publish-target-picker').locator('option[value="github-pages"]')).toHaveCount(1);
  await section.getByTestId('variant-publish-target-picker').selectOption('github-pages');
  await section.getByTestId('variant-publish-mode-input').selectOption('publish');

  expect(fired.previews).toBe(0);
  expect(fired.publishes).toBe(0);

  await section.getByTestId('variant-publish-preview').click();
  await expect(section.getByTestId('variant-publish-preview-hash')).not.toBeEmpty();

  // The preview authorized nothing: still no publish POST after it.
  expect(fired.previews).toBe(1);
  expect(fired.publishes).toBe(0);

  await section.getByTestId('variant-publish-idempotency-input').fill('pfv-happy-path');
  await section.getByTestId('variant-publish-confirm').check();

  const [publishResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/assets\/[^/]+\/publish$/.test(new URL(response.url()).pathname),
    ),
    section.getByTestId('variant-publish-publish').click(),
  ]);

  expect(publishResponse.status()).toBe(202);
  const accepted = (await publishResponse.json()) as { operationId: string; attemptId: string };
  expect(accepted.operationId).toMatch(/^[0-9a-f-]{36}$/);
  expect(accepted.attemptId).toMatch(/^[0-9a-f-]{36}$/);

  await expect(section.getByTestId('variant-publish-notice')).toContainText(accepted.operationId);

  // The receipt names what was sent where. The dispatch loop is silenced here, so the
  // stored answer is the acceptance itself: prepared, for this target, in draft mode.
  const receipt = section.getByTestId('variant-publish-receipt');
  await expect(receipt).toBeVisible();
  await expect(section.getByTestId('variant-publish-state')).toHaveText('prepared');
  await expect(section.getByTestId('variant-publish-target')).toHaveText('github-pages');
  await expect(section.getByTestId('variant-publish-mode')).toHaveText('publish');

  // The server says the same thing the panel does.
  const stored = await readReceiptFromThePage(page, accepted.attemptId);
  expect(stored.status).toBe(200);
  expect(stored.body.state).toBe('prepared');
  expect(stored.body.targetId).toBe('github-pages');
  expect(stored.body.attemptId).toBe(accepted.attemptId);

  // An identical resubmission answers the original receipt, not a second dispatch.
  await section.getByTestId('variant-publish-publish').click();
  await expect(section.getByTestId('variant-publish-notice')).toContainText('identical request');
  expect(fired.publishes).toBe(2);
});

test('an unconfirmed publish is refused in the server own words', async ({ page }) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_STORY_TITLE);

  const panel = row(page, SEEDED_STORY_TITLE).getByTestId('variant-panel');
  await expect(panel).toBeVisible();

  await panel.getByTestId('variant-format').selectOption('technical-blog');
  await panel.getByTestId('variant-derive').click();
  await expect(panel.getByTestId('variant-select-technical-blog')).toBeVisible();

  await panel.getByTestId('variant-approve').click();
  await expect(panel.getByTestId('variant-preview-status')).toHaveText('approved');

  await panel.getByTestId('variant-publish-technical-blog').click();
  const section = panel.getByTestId('variant-publish-section');
  await expect(section).toBeVisible();

  await section.getByTestId('variant-publish-target-picker').selectOption('github-pages');
  await section.getByTestId('variant-publish-mode-input').selectOption('publish');
  await section.getByTestId('variant-publish-preview').click();
  await expect(section.getByTestId('variant-publish-preview-hash')).not.toBeEmpty();

  // The key is named but the confirmation is not checked: consent is a separate act.
  await section.getByTestId('variant-publish-idempotency-input').fill('pfv-unconfirmed');

  await section.getByTestId('variant-publish-publish').click();
  await expect(section.getByTestId('variant-publish-error')).toContainText(
    'publisher_confirmation_required',
  );
  await expect(section.getByTestId('variant-publish-receipt')).toHaveCount(0);
});

test('an unapproved variant offers no publish control and previews its refusal', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_STORY_TITLE);

  const panel = row(page, SEEDED_STORY_TITLE).getByTestId('variant-panel');
  await expect(panel).toBeVisible();

  await panel.getByTestId('variant-format').selectOption('social-short');
  await panel.getByTestId('variant-derive').click();
  await expect(panel.getByTestId('variant-select-social-short')).toBeVisible();
  await expect(panel.getByTestId('variant-preview-status')).toHaveText('draft');

  await panel.getByTestId('variant-publish-social-short').click();

  // No approval, no form: the caption names the remedy rather than offering a send.
  await expect(panel.getByTestId('variant-publish-section')).toHaveCount(0);
  await expect(panel.getByTestId('variant-publish-unavailable')).toContainText('live approval');

  // The server says the same thing the caption does: a draft cannot be previewed for send.
  const assetId = await variantAssetIdFromThePage(page, 'social-short');
  const answer = await previewFromThePage(page, assetId, 'github-pages', 'publish');
  expect(answer.status).toBe(409);
  expect(answer.body.code).toBe('approval_required');
});

test('the publication pickers list the seeded milestone, asset and registry targets', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_STORY_TITLE);

  await page.getByTestId('project-area-publish').click();
  const publications = page.getByTestId('publication-panel');
  await expect(publications).toBeVisible();

  // The hand-typed inputs are gone: picking replaced typing.
  await expect(publications.getByTestId('publication-asset-input')).toHaveCount(0);
  await expect(publications.getByTestId('publication-target-input')).toHaveCount(0);

  const milestones = publications.getByTestId('publication-milestone-picker');
  await expect(milestones.locator('option')).toContainText([SEEDED_STORY_TITLE]);
  await milestones.selectOption({ label: SEEDED_STORY_TITLE });

  const assets = publications.getByTestId('publication-asset-picker');
  await expect(assets.locator('option')).toContainText([/story \(revision 1\)/]);

  const form = publications.getByTestId('publication-form');
  await expect(form.getByTestId('publication-target-picker').locator('option[value="github-pages"]')).toHaveCount(1);
});

test('the journey picker lists the seeded asset and previews a hash for it', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_STORY_TITLE);

  await page.getByTestId('project-area-publish').click();
  const journey = page.getByTestId('journey-panel');
  await expect(journey).toBeVisible();

  // The hand-typed asset box is gone: picking replaced typing.
  await expect(journey.getByTestId('journey-assets-input')).toHaveCount(0);

  const milestones = journey.getByTestId('journey-milestone-picker');
  await expect(milestones.locator('option')).toContainText([SEEDED_STORY_TITLE]);
  await milestones.selectOption({ label: SEEDED_STORY_TITLE });

  const options = journey.getByTestId('journey-asset-option');
  await expect(options.first()).toBeVisible();

  // The seeded story is approved, so checking it and previewing computes the hash a
  // publication must carry - the picker wiring proved through the delivered route.
  await options.first().check();
  await journey.getByTestId('journey-preview').click();
  await expect(journey.getByTestId('journey-selection-hash')).not.toBeEmpty();
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
 * Counts the publish-bound POSTs the page issues from here on. A preview that
 * authorizes nothing must not publish, and a panel that merely renders must not
 * preview: the counts are read after the clicks, never awaited.
 */
function armPublishCounter(page: Page): { previews: number; publishes: number } {
  const fired = { previews: 0, publishes: 0 };

  page.on('request', (request) => {
    if (request.method() !== 'POST') {
      return;
    }

    const path = new URL(request.url()).pathname;

    if (/\/api\/assets\/[^/]+\/publish-preview$/.test(path)) {
      fired.previews += 1;
    } else if (/\/api\/assets\/[^/]+\/publish$/.test(path)) {
      fired.publishes += 1;
    }
  });

  return fired;
}

async function variantAssetIdFromThePage(page: Page, type: string): Promise<string> {
  return page.evaluate(async (wanted) => {
    const projects = await fetch('/api/projects', { credentials: 'same-origin' });
    const catalogue = (await projects.json()) as { readonly items: readonly { readonly id: string }[] };

    for (const project of catalogue.items) {
      const milestones = (await (
        await fetch(`/api/projects/${project.id}/milestones`, { credentials: 'same-origin' })
      ).json()) as { readonly items: readonly { readonly id: string }[] };

      for (const milestone of milestones.items) {
        const listed = (await (
          await fetch(`/api/milestones/${milestone.id}/assets`, { credentials: 'same-origin' })
        ).json()) as { readonly variants: readonly { readonly assetId: string; readonly type: string }[] };

        const found = listed.variants.find((variant) => variant.type === wanted);

        if (found) {
          return found.assetId;
        }
      }
    }

    throw new Error(`no derived variant of type "${wanted}" exists in this installation`);
  }, type);
}

async function previewFromThePage(page: Page, assetId: string, targetId: string, mode: string) {
  return page.evaluate(
    async ({ asset, target, previewMode }) => {
      const antiforgery = (await (
        await fetch('/api/session/antiforgery', { credentials: 'same-origin' })
      ).json()) as { headerName: string; token: string };

      const response = await fetch(`/api/assets/${asset}/publish-preview`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          [antiforgery.headerName]: antiforgery.token,
        },
        body: JSON.stringify({ targetId: target, mode: previewMode }),
      });

      return { status: response.status, body: await response.json() };
    },
    { asset: assetId, target: targetId, previewMode: mode },
  );
}

async function readReceiptFromThePage(page: Page, attemptId: string) {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/publications/${id}`, { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  }, attemptId);
}
