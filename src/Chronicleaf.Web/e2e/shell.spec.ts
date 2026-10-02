import { expect, test } from '@playwright/test';

const TAGLINE = 'Turn project milestones into publishable stories.';
const SETUP_PENDING = 'Project setup coming in a later change.';

test('the browser entry point shows the Chronicleaf shell', async ({ page }) => {
  const response = await page.goto('/');

  expect(response?.status()).toBe(200);
  await expect(page.getByRole('heading', { name: 'Chronicleaf', exact: true })).toBeVisible();
  await expect(page.getByTestId('tagline')).toHaveText(TAGLINE);
  await expect(page.getByTestId('setup-pending')).toHaveText(SETUP_PENDING);
});

test('a client-side route renders the same shell in a real browser', async ({ page }) => {
  const response = await page.goto('/projects/some-client-side-route');

  expect(response?.status()).toBe(200);
  await expect(page.getByRole('heading', { name: 'Chronicleaf', exact: true })).toBeVisible();
  await expect(page.getByTestId('setup-pending')).toHaveText(SETUP_PENDING);
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
