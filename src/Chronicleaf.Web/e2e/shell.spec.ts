import { expect, test } from '@playwright/test';

/**
 * PRODUCT-SHELL-UX-001/003 migration (named, in-package).
 *
 * The pre-restyle shell asserted here was a single centered card repeating
 * the global `Chronicleaf` brand heading plus the Owner-session panel on
 * every page, with a `later-packages` / `setup-pending` block
 * ("Not in this change", "Project setup coming in a later change.") at its
 * foot. That block is deleted by owner decision (design.md ledger item 4),
 * the global brand heading is replaced by per-page h1s behind a top-level
 * nav, and session internals render only under `/settings`.
 *
 * What this suite now asserts instead: the signed-out shell still shows the
 * brand and tagline; no `setup-pending` / `later-packages` testid or text
 * exists anywhere; a client-side route renders the same signed-out shell;
 * and the served API answers the liveness and unknown-route contract.
 */
const TAGLINE = 'Turn project milestones into publishable stories.';

test('the browser entry point shows the Chronicleaf shell', async ({ page }) => {
  const response = await page.goto('/');

  expect(response?.status()).toBe(200);
  await expect(page.getByRole('heading', { name: 'Chronicleaf', exact: true })).toBeVisible();
  await expect(page.getByTestId('tagline')).toHaveText(TAGLINE);

  // PRODUCT-SHELL-UX-003: the scaffolding block is gone, not renamed.
  await expect(page.getByTestId('setup-pending')).toHaveCount(0);
  await expect(page.getByTestId('later-packages')).toHaveCount(0);
  await expect(page.getByText('Not in this change', { exact: true })).toHaveCount(0);
  await expect(
    page.getByText('Project setup coming in a later change.', { exact: true }),
  ).toHaveCount(0);
});

test('a client-side route renders the same shell in a real browser', async ({ page }) => {
  const response = await page.goto('/projects/some-client-side-route');

  expect(response?.status()).toBe(200);
  await expect(page.getByRole('heading', { name: 'Chronicleaf', exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-pending')).toHaveCount(0);
  await expect(page.getByTestId('later-packages')).toHaveCount(0);
});

test('an unknown static asset is a 404 rather than the shell', async ({ request }) => {
  const response = await request.get('/assets/does-not-exist-a1b2c3.js');

  expect(response.status()).toBe(404);
  expect(response.headers()['content-type']).toContain('application/json');
  expect(await response.json()).toEqual({ code: 'not_found' });
});

test('the served API answers the liveness and unknown-route contract', async ({ request }) => {
  const health = await request.get('/api/health');

  expect(health.status()).toBe(200);
  expect(health.headers()['content-type']).toContain('application/json');
  expect(await health.json()).toEqual({ status: 'ok', service: 'chronicleaf' });

  const unknown = await request.get('/api/unknown');

  expect(unknown.status()).toBe(404);
  expect(await unknown.json()).toEqual({ code: 'not_found' });
});
