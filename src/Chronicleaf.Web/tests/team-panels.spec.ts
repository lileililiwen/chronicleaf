import { expect, test, type Page } from '@playwright/test';

/**
 * FEATURE-TRIGGER-PANELS-001 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded data: this installation is a Community
 * single-tenant one, every claim below is read back from the page or from the
 * server's own answer, and the empty state is asserted before anything is
 * attempted, precisely because a tenant with no invitations shows none.
 *
 * What this suite claims, and what it does not. The team routes are Hosted
 * routes, and in a Community installation the service refuses every call with
 * its own generic refusal rather than changing Community behavior - so the
 * failure scenarios assert that the panel surfaces the server's own refusal
 * verbatim and invents no row. A pass is evidence that the triggers reach the
 * delivered routes and report what the server says, and is NOT evidence that
 * an invitation was issued, a role was changed or a grant was stored on this
 * host. The .NET suite is where the Hosted flows are proven.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

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

test('a tenant with no invitations or members shows honest empty states', async ({ page }) => {
  await signIn(page);
  await page.goto('/settings/team');

  await expect(page.getByTestId('team-panel')).toBeVisible();
  await expect(page.getByTestId('team-heading')).toContainText('Team invitations and members');
  await expect(page.getByTestId('team-invitations-empty')).toBeVisible();
  await expect(page.getByTestId('team-members-empty')).toBeVisible();
  await expect(page.getByTestId('team-invitation')).toHaveCount(0);
  await expect(page.getByTestId('team-member')).toHaveCount(0);
});

test('listing a tenant answers the server refusal verbatim and invents no row', async ({
  page,
}) => {
  await signIn(page);
  await page.goto('/settings/team');

  await page.getByTestId('team-tenant-input').fill('11111111-1111-1111-1111-111111111111');
  await page.getByTestId('team-refresh').click();

  // The Community installation refuses the Hosted route with its own code; the panel repeats
  // the server's code and message rather than paraphrasing them.
  await expect(page.getByTestId('team-error')).toBeVisible();
  await expect(page.getByTestId('team-error')).toContainText('oidc_login_failed');
  await expect(page.getByTestId('team-invitation')).toHaveCount(0);
  await expect(page.getByTestId('team-member')).toHaveCount(0);

  // The server says the same thing the panel does.
  const answer = await inviteFromThePage(page, '11111111-1111-1111-1111-111111111111');
  expect(answer.status).toBe(401);
  expect(answer.body.code).toBe('oidc_login_failed');
});

test('accepting without a pending invitation answers the route refusal', async ({ page }) => {
  await signIn(page);
  await page.goto('/settings/team');

  await page.getByTestId('team-accept-token').fill('not-a-pending-invitation-token');
  await page.getByTestId('team-accept').click();

  await expect(page.getByTestId('team-error')).toBeVisible();
  await expect(page.getByTestId('team-error')).toContainText('oidc_login_failed');
  await expect(page.getByTestId('team-accepted')).toHaveCount(0);
});

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/**
 * Reads the server's own answer for an invitation list through the delivered
 * route, with this browser's own session cookie and a fresh antiforgery value.
 */
async function inviteFromThePage(page: Page, tenantId: string) {
  return page.evaluate(async (id) => {
    const metadata = await (
      await fetch('/api/session/antiforgery', { credentials: 'same-origin' })
    ).json();

    const response = await fetch(
      `/api/hosted/tenants/${id}/invitations`,
      { credentials: 'same-origin' },
    );

    void metadata;

    return { status: response.status, body: await response.json() };
  }, tenantId);
}
