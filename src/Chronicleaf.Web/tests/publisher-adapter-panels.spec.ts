import { expect, test, type Page } from '@playwright/test';

/**
 * PUBLISHER-ADAPTER-PANELS-002/003 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * This installation registers all six transports (github-pages, devto, hashnode,
 * ghost, wordpress, custom-blog) plus the export-only medium and linkedin entries,
 * silences the dispatch loop, and seeds one project, one confirmed milestone, one
 * approved story and one sealed `publisher` credential via `seed_publish_variant`.
 *
 * What this suite claims, and what it does not. A pass is evidence that picking a
 * target shows its adapter surface, that the mode box offers only what the registry
 * serves, that the preview names the bound configuration, that confirm reaches the
 * delivered publish route and the receipt names the server's answer, and that the
 * export-only refusals arrive in the server's own words with no send. It is NOT
 * evidence that anything was sent anywhere: the dispatch loop is silenced, so each
 * receipt stays at the acceptance the request committed, and the run half is the
 * .NET suites' claim, not this one's.
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

test('github-pages: adapter surface, publish-only mode, bound repository, confirm reaches receipt', async ({
  page,
}) => {
  const form = await openPublishForm(page, SEEDED_STORY_TITLE);

  // Nothing may leave before the owner acts: arm the counter before touching the
  // picker, and no preview or publish POST may fire until the preview button is.
  const fired = armPublishCounter(page);

  await form.getByTestId('publication-target-picker').selectOption('github-pages');
  await expect(form.getByTestId('publication-adapter-panel')).toBeVisible();
  await expect(form.getByTestId('publication-adapter-name')).toContainText('GitHub Pages');

  // The registry serves publish only for this target, so the mode box offers exactly that.
  await expect(form.getByTestId('publication-mode-input').locator('option')).toHaveCount(1);
  await expect(form.getByTestId('publication-mode-input')).toHaveValue('publish');

  expect(fired.previews).toBe(0);
  expect(fired.publishes).toBe(0);

  await form.getByTestId('publication-preview').click();
  await expect(form.getByTestId('publication-preview-hash')).not.toBeEmpty();

  // The preview authorized nothing, and it names the bound repository.
  expect(fired.previews).toBe(1);
  expect(fired.publishes).toBe(0);
  await expect(form.getByTestId('publication-adapter-config')).toContainText(
    'e2e-owner/e2e-docs@main',
  );

  await form.getByTestId('publication-idempotency-input').fill('pap-github-pages');
  await form.getByTestId('publication-confirm').check();

  const [publishResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        /\/api\/assets\/[^/]+\/publish$/.test(new URL(response.url()).pathname),
    ),
    form.getByTestId('publication-publish').click(),
  ]);

  expect(publishResponse.status()).toBe(202);

  const receipt = form.getByTestId('publication-receipt');
  await expect(receipt).toBeVisible();
  await expect(form.getByTestId('publication-state')).toHaveText('prepared');
  await expect(form.getByTestId('publication-target')).toHaveText('github-pages');
  await expect(form.getByTestId('publication-mode')).toHaveText('publish');
});

test('devto: adapter surface, draft mode, bound tags, confirm reaches receipt', async ({ page }) => {
  const form = await openPublishForm(page, SEEDED_STORY_TITLE);

  await form.getByTestId('publication-target-picker').selectOption('devto');
  await expect(form.getByTestId('publication-adapter-name')).toContainText('Dev.to');
  await expect(form.getByTestId('publication-adapter-modes')).toContainText('draft + publish');

  await form.getByTestId('publication-mode-input').selectOption('draft');
  await form.getByTestId('publication-preview').click();
  await expect(form.getByTestId('publication-preview-hash')).not.toBeEmpty();
  await expect(form.getByTestId('publication-adapter-config')).toContainText(
    'devto tags:[e2e,chronicleaf]',
  );

  await form.getByTestId('publication-idempotency-input').fill('pap-devto');
  await form.getByTestId('publication-confirm').check();
  await form.getByTestId('publication-publish').click();

  await expect(form.getByTestId('publication-receipt')).toBeVisible();
  await expect(form.getByTestId('publication-target')).toHaveText('devto');
  await expect(form.getByTestId('publication-mode')).toHaveText('draft');
});

test('hashnode: adapter surface, bound publication, confirm reaches receipt', async ({ page }) => {
  const form = await openPublishForm(page, SEEDED_STORY_TITLE);

  await form.getByTestId('publication-target-picker').selectOption('hashnode');
  await expect(form.getByTestId('publication-adapter-name')).toContainText('Hashnode');

  await form.getByTestId('publication-mode-input').selectOption('draft');
  await form.getByTestId('publication-preview').click();
  await expect(form.getByTestId('publication-preview-hash')).not.toBeEmpty();
  await expect(form.getByTestId('publication-adapter-config')).toContainText(
    'hashnode publication:e2e-publication-01',
  );

  await form.getByTestId('publication-idempotency-input').fill('pap-hashnode');
  await form.getByTestId('publication-confirm').check();
  await form.getByTestId('publication-publish').click();

  await expect(form.getByTestId('publication-receipt')).toBeVisible();
  await expect(form.getByTestId('publication-target')).toHaveText('hashnode');
});

test('ghost: adapter surface, bound origin and version, confirm reaches receipt', async ({
  page,
}) => {
  const form = await openPublishForm(page, SEEDED_STORY_TITLE);

  await form.getByTestId('publication-target-picker').selectOption('ghost');
  await expect(form.getByTestId('publication-adapter-name')).toContainText('Ghost');

  await form.getByTestId('publication-mode-input').selectOption('draft');
  await form.getByTestId('publication-preview').click();
  await expect(form.getByTestId('publication-preview-hash')).not.toBeEmpty();
  await expect(form.getByTestId('publication-adapter-config')).toContainText(
    'ghost origin:https://ghost.example.invalid version:v5.0',
  );

  await form.getByTestId('publication-idempotency-input').fill('pap-ghost');
  await form.getByTestId('publication-confirm').check();
  await form.getByTestId('publication-publish').click();

  await expect(form.getByTestId('publication-receipt')).toBeVisible();
  await expect(form.getByTestId('publication-target')).toHaveText('ghost');
});

test('wordpress: adapter surface, bound origin and user, confirm reaches receipt', async ({
  page,
}) => {
  const form = await openPublishForm(page, SEEDED_STORY_TITLE);

  await form.getByTestId('publication-target-picker').selectOption('wordpress');
  await expect(form.getByTestId('publication-adapter-name')).toContainText('WordPress');

  await form.getByTestId('publication-mode-input').selectOption('draft');
  await form.getByTestId('publication-preview').click();
  await expect(form.getByTestId('publication-preview-hash')).not.toBeEmpty();
  await expect(form.getByTestId('publication-adapter-config')).toContainText(
    'wordpress origin:https://wp.example.invalid user:e2euser',
  );

  await form.getByTestId('publication-idempotency-input').fill('pap-wordpress');
  await form.getByTestId('publication-confirm').check();
  await form.getByTestId('publication-publish').click();

  await expect(form.getByTestId('publication-receipt')).toBeVisible();
  await expect(form.getByTestId('publication-target')).toHaveText('wordpress');
});

test('custom-blog: adapter surface, bound origin and formats, confirm reaches receipt', async ({
  page,
}) => {
  const form = await openPublishForm(page, SEEDED_STORY_TITLE);

  await form.getByTestId('publication-target-picker').selectOption('custom-blog');
  await expect(form.getByTestId('publication-adapter-name')).toContainText('Custom blog');

  await form.getByTestId('publication-mode-input').selectOption('draft');
  await form.getByTestId('publication-preview').click();
  await expect(form.getByTestId('publication-preview-hash')).not.toBeEmpty();
  await expect(form.getByTestId('publication-adapter-config')).toContainText(
    'custom-blog origin:https://blog.example.invalid formats:[markdown]',
  );

  await form.getByTestId('publication-idempotency-input').fill('pap-custom-blog');
  await form.getByTestId('publication-confirm').check();
  await form.getByTestId('publication-publish').click();

  await expect(form.getByTestId('publication-receipt')).toBeVisible();
  await expect(form.getByTestId('publication-target')).toHaveText('custom-blog');
});

test('medium and linkedin previews are refused in the server own words with no send', async ({
  page,
}) => {
  const form = await openPublishForm(page, SEEDED_STORY_TITLE);
  const fired = armPublishCounter(page);

  for (const target of ['medium', 'linkedin'] as const) {
    await form.getByTestId('publication-target-picker').selectOption(target);
    await expect(form.getByTestId('publication-adapter-panel')).toBeVisible();

    // An export-only target serves no mode, so there is no mode to pick.
    await expect(form.getByTestId('publication-mode-input')).toHaveCount(0);
    await expect(form.getByTestId('publication-adapter-modes')).toContainText('export-only');

    await form.getByTestId('publication-preview').click();
    await expect(form.getByTestId('publication-error')).toContainText(
      'publisher_target_export_only',
    );
    await expect(form.getByTestId('publication-receipt')).toHaveCount(0);
  }

  // Two refused previews, and still no publish POST: a refusal records no dispatch.
  expect(fired.previews).toBe(2);
  expect(fired.publishes).toBe(0);
});

/**
 * Opens the Publish area's shared form bound to the seeded approved story:
 * milestone picked by title, the story asset picked by kind, approval read
 * by the panel itself. Returns the form locator the scenarios act on.
 */
async function openPublishForm(page: Page, title: string) {
  await signIn(page);
  await openProjectHolding(page, title);

  await page.getByTestId('project-area-publish').click();
  const publications = page.getByTestId('publication-panel');
  await expect(publications).toBeVisible();

  await publications.getByTestId('publication-milestone-picker').selectOption({ label: title });

  const assets = publications.getByTestId('publication-asset-picker');
  await expect(assets.locator('option')).toContainText([/story \(revision 1\)/]);

  const form = publications.getByTestId('publication-form');
  await expect(form).toBeVisible();

  // The panel reads the live approval itself; the publish needs the id it read.
  await expect(form.getByTestId('publication-approval-input')).not.toHaveValue('', {
    timeout: 15000,
  });

  return form;
}

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/**
 * Opens the project the milestone with this title lives in, and waits for that
 * milestone's row - the same route the publish-from-variant suite reads, because a
 * fixture that opened the wrong project would assert about a record it never looked at.
 */
async function openProjectHolding(page: Page, title: string): Promise<void> {
  const found = await locateMilestone(page, title);

  if (found === null) {
    throw new Error(`no milestone titled "${title}" exists in this installation`);
  }

  await page.goto(`/projects/${found.projectId}`);
  await expect(page.getByTestId('project-name')).toBeVisible();
  await expect(
    page
      .getByTestId('milestone')
      .filter({ has: page.getByTestId('milestone-title').filter({ hasText: title }) })
      .first(),
  ).toBeVisible();

  await page.getByTestId('project-area-publish').click();
  await expect(page.getByTestId('publication-panel')).toBeVisible();
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
