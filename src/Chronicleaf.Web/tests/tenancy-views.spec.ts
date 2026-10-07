import { expect, test, type Page } from '@playwright/test';

/**
 * FEATURE-TRIGGER-PANELS-002 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded data: this installation is a Community
 * single-tenant one, and every claim below is read back from the page or from
 * the server's own answer.
 *
 * What this suite claims, and what it does not. The tenancy views call the
 * cloud-tenant-boundary routes and render whatever the server answers -
 * including the honest not-enabled states - and never a client-side tenancy
 * emulation. In this Community installation the server refuses with its own
 * generic refusal, so the scenarios assert that the views surface that answer
 * verbatim and invent no tenant. A pass is evidence that the triggers reach
 * the delivered routes and report what the server says, and is NOT evidence
 * that a tenant was created, selected or imported on this host. The .NET suite
 * is where the Hosted flows are proven.
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

test('a Community installation shows the server own tenancy answer and no invented tenant', async ({
  page,
}) => {
  await signIn(page);
  await page.goto('/settings/tenancy');

  await expect(page.getByTestId('tenancy-panel')).toBeVisible();
  await expect(page.getByTestId('tenancy-heading')).toContainText('Hosted tenancy');

  // The server's own Community answer is a refusal, shown verbatim; no tenant is listed
  // that the server did not serve.
  await expect(page.getByTestId('tenancy-error')).toBeVisible();
  await expect(page.getByTestId('tenancy-error')).toContainText('oidc_login_failed');
  await expect(page.getByTestId('tenancy-item')).toHaveCount(0);

  // The server says the same thing the panel does.
  const answer = await listTenantsFromThePage(page);
  expect(answer.status).toBe(401);
  expect(answer.body.code).toBe('oidc_login_failed');
});

test('creating a tenant from a Community installation answers the route refusal', async ({
  page,
}) => {
  await signIn(page);
  await page.goto('/settings/tenancy');

  await expect(page.getByTestId('tenancy-error')).toBeVisible();

  await page.getByTestId('tenancy-name-input').fill('Northwind Traders');
  await page.getByTestId('tenancy-create').click();

  await expect(page.getByTestId('tenancy-error')).toContainText('oidc_login_failed');
  await expect(page.getByTestId('tenancy-item')).toHaveCount(0);
});

test('recording a Community import from a Community installation answers the route refusal', async ({
  page,
}) => {
  await signIn(page);
  await page.goto('/settings/tenancy');

  await page.getByTestId('tenancy-import-input').fill('11111111-1111-1111-1111-111111111111');
  await page.getByTestId('tenancy-import').click();

  await expect(page.getByTestId('tenancy-error')).toBeVisible();
  await expect(page.getByTestId('tenancy-error')).toContainText('oidc_login_failed');
});

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/**
 * Reads the server's own answer for the tenant list through the delivered
 * route, with this browser's own session cookie.
 */
async function listTenantsFromThePage(page: Page) {
  return page.evaluate(async () => {
    const response = await fetch('/api/hosted/tenants', { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  });
}
