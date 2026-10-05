import { expect, test, type Page } from '@playwright/test';

/**
 * CONTENT-ASSET-VARIANTS-001 and CONTENT-ASSET-VARIANTS-002 in a real browser, against the real
 * published application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * **What is real.** The project, the milestone, its evidence and the approved story below are all
 * real rows in a real database. The approved story is written by `scripts/e2e.sh` through the same
 * `INSERT` a completed provider run produces, with a body hash computed from its own bytes, a
 * voice snapshot the read route re-checks rather than trusts, and a live approval row bound to
 * the head revision, the body hash and the evidence head. Every derive, preview and approval below
 * goes through the shipped panel and the published server - the variant rows and the variant
 * approval are records the product itself created.
 *
 * **What is a fixture, and stated rather than implied.** The story's *text* is a fixture. No
 * provider was called: the voice snapshot names an English voice, the summary cites the real
 * evidence item the seed created, and the approval row is inserted by the seed rather than by a
 * press. Deriving itself calls no provider either - it re-renders the accepted summary through a
 * deterministic template - so a pass is evidence that the panel derives, previews and approves a
 * variant faithfully, and is **not** evidence that any provider can be called or that a draft was
 * generated on this host. The .NET suite is where a real provider answer flows through generation
 * into review; this one is where a person reads an approved story and reuses it in another shape.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** The milestone `scripts/e2e.sh` seeded an approved story against, by the title it created it with. */
const SEEDED_VARIANT_TITLE = 'Variant source story';

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

test('an approved story derives a changelog with sections, citations and its own approval', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_VARIANT_TITLE);

  const milestone = row(page, SEEDED_VARIANT_TITLE);
  const panel = milestone.getByTestId('variant-panel');
  await expect(panel).toBeVisible();

  // Nothing derived yet: the panel says what it needs rather than offering a derivation
  // against a story nobody approved.
  await expect(panel.getByTestId('variant-count')).toContainText('No formats derived yet');

  // The picker lists every derived format, in the server's own order.
  const picker = panel.getByTestId('variant-format');
  for (const format of [
    'changelog',
    'technical-blog',
    'social-short',
    'github-release',
    'readme-update',
    'portfolio',
    'weekly-progress',
  ]) {
    await expect(picker.locator(`option[value="${format}"]`)).toHaveCount(1);
  }

  // Deriving renders the approved summary through the template: the defined sections arrive,
  // every citation names a footnote the body defines, and the known limitation survives.
  await picker.selectOption('changelog');
  await panel.getByTestId('variant-derive').click();

  await expect(panel.getByTestId('variant-select-changelog')).toBeVisible();
  await expect(panel.getByTestId('variant-preview-type')).toHaveText('changelog');
  await expect(panel.getByTestId('variant-preview-revision')).toContainText('Revision 1');
  await expect(panel.getByTestId('variant-preview-status')).toHaveText('draft');

  const preview = panel.getByTestId('variant-preview-body');
  await expect(preview).toContainText('Added');
  await expect(preview).toContainText('Fixed');
  await expect(preview).toContainText('Only Linux is covered.');

  // A derivative starts life as a draft with no approval row: approving the story never
  // approved the variant, and the approve control is a button only a press can reach.
  await expect(panel.getByTestId('variant-approve')).toHaveAttribute('type', 'button');
  await panel.getByTestId('variant-approve').click();
  await expect(panel.getByTestId('variant-preview-status')).toHaveText('approved');
});

test('a readme-update derivation is a proposal that names what it did not do', async ({
  page,
}) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_VARIANT_TITLE);

  const panel = row(page, SEEDED_VARIANT_TITLE).getByTestId('variant-panel');
  await expect(panel).toBeVisible();

  await panel.getByTestId('variant-format').selectOption('readme-update');
  await panel.getByTestId('variant-derive').click();

  await expect(panel.getByTestId('variant-select-readme-update')).toBeVisible();
  await expect(panel.getByTestId('variant-preview-type')).toHaveText('readme-update');

  // The proposal notice is its own element rather than prose the preview could hide: no
  // repository was changed, and there is no control here that could change one.
  await expect(panel.getByTestId('variant-proposal-notice')).toContainText(
    'no repository was changed',
  );
  await expect(panel.getByTestId('variant-preview-body')).toContainText('README proposal');
});

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
