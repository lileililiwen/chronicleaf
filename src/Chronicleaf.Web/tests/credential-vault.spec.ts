import { expect, test, type Page } from '@playwright/test';

/**
 * CREDENTIAL-VAULT-001 and CREDENTIAL-VAULT-002 in a real browser, against the real published
 * application, the real disposable PostgreSQL and the real operator key file that scripts/e2e.sh
 * mounts for the run.
 *
 * There is no stubbed API and no seeded credential: this installation starts with every provider
 * unconfigured, every record below is created by the same clicks an owner would make, and every claim
 * is read back from the page or from the server's own answer.
 *
 * The negative half matters as much as the positive one. After a token is stored, the fixture asserts
 * that the sentinel value appears nowhere in the rendered page, in any input's value, and in any
 * network response the browser saw. A settings surface that could display a stored credential would
 * fail this, and that is the whole reason the API has no route that returns one.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** A sentinel that is obviously a secret, so a leak in any of the assertions below is unmistakable. */
const SENTINEL = 'ghp_browserFixtureSentinelValue_4d7f21a8c';

test.beforeEach(async ({ page }) => {
  if (!ACCESS_TOKEN) {
    throw new Error(
      'CHRONICLEAF_E2E_ACCESS_TOKEN is not set. Run these fixtures through scripts/e2e.sh, which ' +
        'starts the published application with a real access token hash, a real database and a real ' +
        'operator key file.',
    );
  }

  await page.context().clearCookies();
  await page.goto('/');
  await signIn(page);
  await page.goto('/settings/credentials');
  await expect(page.getByTestId('credential-vault')).toBeVisible();

  // Every test starts from a new-installation state, reached through the product's own remove
  // control rather than by assuming a clean database, so the order the fixtures run in cannot make
  // one of them a lie.
  await resetProvider(page, 'github');
});

test.afterEach(async ({ page }) => {
  // No sentinel survives this fixture, in the DOM.
  await expect(page.locator(`text=${SENTINEL}`)).toHaveCount(0);

  // And the secret field is empty, on the pages where there is a field to inspect: the last test
  // signs the browser out, and a field that is not on the page cannot hold anything.
  const field = page.getByTestId('credential-github-input');

  if ((await field.count()) > 0) {
    await expect(field).toHaveValue('');
  }
});

test('an unconfigured provider reads as unconfigured and not as an empty field holding something', async ({
  page,
}) => {
  const github = page.getByTestId('credential-github');

  await expect(github.getByTestId('credential-github-unconfigured')).toBeVisible();
  await expect(github.getByTestId('credential-github-configured')).toHaveCount(0);
  await expect(github.getByTestId('credential-github-updated')).toContainText(
    'Nothing has been stored for this provider',
  );

  // The field is empty and says so, and there is no remove control for a provider with nothing stored.
  await expect(github.getByTestId('credential-github-input')).toHaveValue('');
  await expect(github.getByTestId('credential-github-delete')).toHaveCount(0);
  await expect(github.getByTestId('credential-github-save')).toBeDisabled();

  // Every supported provider is listed, and every one of them says the same true thing.
  for (const provider of ['github', 'openai', 'gemini', 'anthropic', 'openrouter', 'ollama', 'publisher']) {
    await expect(page.getByTestId(`credential-${provider}`)).toBeVisible();
    await expect(page.getByTestId(`credential-${provider}-unconfigured`)).toBeVisible();
  }

  // The server agrees, provider by provider, and returns no id for one that has nothing.
  const described = await readFromThePage(page, '/api/settings/credentials/github');
  expect(described.status).toBe(200);
  expect(described.body).toEqual({
    id: null,
    provider: 'github',
    configured: false,
    updatedAt: null,
  });
});

test('a stored credential shows metadata only, and the field is empty again', async ({ page }) => {
  const github = page.getByTestId('credential-github');

  await github.getByTestId('credential-github-input').fill(SENTINEL);
  await github.getByTestId('credential-github-save').click();
  await waitForStored(page, 'github');

  // Configured, with the server's instant, and the value is nowhere.
  await expect(github.getByTestId('credential-github-configured')).toBeVisible();
  await expect(github.getByTestId('credential-github-unconfigured')).toHaveCount(0);
  await expect(github.getByTestId('credential-github-updated')).toContainText('Last changed');
  await expect(github.getByTestId('credential-github-input')).toHaveValue('');
  await expect(github.getByTestId('credential-github-saved')).toContainText('The field above is empty');

  // The page holds no trace of the value, and neither does any value the browser received.
  await expect(page.locator(`text=${SENTINEL}`)).toHaveCount(0);
  await expect(page.getByTestId('credential-github')).not.toContainText(SENTINEL);

  const bodies = await responseBodies(page, '/api/settings/credentials/github');
  expect(bodies.length).toBeGreaterThan(0);

  for (const body of bodies) {
    expect(body).not.toContain(SENTINEL);
    expect(Object.keys(JSON.parse(body) as object).sort()).toEqual([
      'configured',
      'id',
      'provider',
      'updatedAt',
    ]);
  }

  // And the server's own answer for the provider carries a revision to replace, which is a number and
  // not a value.
  const described = await readFromThePage(page, '/api/settings/credentials/github');
  expect(described.status).toBe(200);
  expect(described.body.configured).toBe(true);
  expect(typeof described.body.id).toBe('string');
  expect(described.body.updatedAt).not.toBeNull();
});

test('a stored credential can be replaced, and the replacement is what the server holds', async ({ page }) => {
  const github = page.getByTestId('credential-github');

  await github.getByTestId('credential-github-input').fill(SENTINEL);
  await github.getByTestId('credential-github-save').click();
  await waitForStored(page, 'github');
  await expect(github.getByTestId('credential-github-configured')).toBeVisible();

  const first = await readFromThePage(page, '/api/settings/credentials/github');
  const firstId = first.body.id as string;
  const firstUpdatedAt = first.body.updatedAt as string;

  // Replacing is the same control with new text, and the field clears again once it is stored.
  await github.getByTestId('credential-github-input').fill('replacement-token-b19c40');
  await expect(github.getByTestId('credential-github-save')).toHaveText('Replace');
  await github.getByTestId('credential-github-save').click();
  await waitForStored(page, 'github');

  const second = await readFromThePage(page, '/api/settings/credentials/github');
  const secondUpdatedAt = second.body.updatedAt as string;

  // The same credential, a later instant: a rotation, not a second row and not a new identity.
  expect(second.body.id).toBe(firstId);
  expect(second.body.configured).toBe(true);
  expect(new Date(secondUpdatedAt).getTime()).toBeGreaterThanOrEqual(new Date(firstUpdatedAt).getTime());

  // The replaced value appears nowhere in the page, and the replacement does not either: this surface
  // shows metadata, not values.
  await expect(page.locator(`text=${SENTINEL}`)).toHaveCount(0);
  await expect(page.locator('text=replacement-token-b19c40')).toHaveCount(0);
  await expect(github.getByTestId('credential-github-input')).toHaveValue('');

  // The list agrees, and still shows four fields per item.
  const listed = await readFromThePage(page, '/api/settings/credentials?limit=100');
  expect(listed.status).toBe(200);

  const items = listed.body.items as readonly { provider: string; configured: boolean }[];
  const githubItem = items.find((item) => item.provider === 'github');

  expect(githubItem).toBeDefined();
  expect(githubItem?.configured).toBe(true);
  expect(JSON.stringify(listed.body)).not.toContain(SENTINEL);
});

test('a removed credential reads as unconfigured again and leaves the field empty', async ({ page }) => {
  const github = page.getByTestId('credential-github');

  await github.getByTestId('credential-github-input').fill(SENTINEL);
  await github.getByTestId('credential-github-save').click();
  await waitForStored(page, 'github');
  await expect(github.getByTestId('credential-github-configured')).toBeVisible();

  await github.getByTestId('credential-github-delete').click();

  await expect(github.getByTestId('credential-github-unconfigured')).toBeVisible();
  await expect(github.getByTestId('credential-github-configured')).toHaveCount(0);
  await expect(github.getByTestId('credential-github-updated')).toContainText(
    'Nothing has been stored for this provider',
  );
  await expect(github.getByTestId('credential-github-input')).toHaveValue('');
  await expect(github.getByTestId('credential-github-delete')).toHaveCount(0);

  const described = await readFromThePage(page, '/api/settings/credentials/github');
  expect(described.status).toBe(200);
  expect(described.body).toEqual({
    id: null,
    provider: 'github',
    configured: false,
    updatedAt: null,
  });

  // The list no longer mentions it, because a removed credential is not a stored one.
  const listed = await readFromThePage(page, '/api/settings/credentials?limit=100');
  const items = listed.body.items as readonly { provider: string }[];

  expect(items.find((item) => item.provider === 'github')).toBeUndefined();
  await expect(page.locator(`text=${SENTINEL}`)).toHaveCount(0);
});

test('a token is never sent, and a write is never offered, before the panel has read what is stored', async ({
  page,
}) => {
  // This holds the provider's read open, so the panel is provably mid-read for as long as the
  // assertions below need it rather than for a hopeful fraction of a second. The read is delayed,
  // never replaced: the request is forwarded to the real server and the answer the panel goes on to
  // act on is the real one. Nothing here stubs, mocks or seeds an answer.
  //
  // This is the regression for the failure where the panel rendered "Not configured" for a provider
  // it had not read, offered the save control anyway, and sent a write presenting revision 0 - a
  // token the server issues for "nothing stored" and a token the panel had never been given. The
  // server refused that write as stale, so it was never stored, so the field kept the value with the
  // refusal beside it. On that behaviour the first two assertions below fail.
  let release = (): void => {};
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });

  const writes: string[] = [];

  page.on('request', (request) => {
    if (request.method() === 'PUT') {
      writes.push(request.headers()['if-match'] ?? '<no If-Match>');
    }
  });

  await page.route('**/api/settings/credentials/github', async (route) => {
    await held;
    await route.continue();
  });

  const github = page.getByTestId('credential-github');

  await page.goto('/settings/credentials');
  await expect(page.getByTestId('credential-vault')).toBeVisible();

  // Mid-read, the row says it is reading. It does not claim an empty vault it has not been told
  // about, and it does not offer the two controls that act on what is stored.
  await expect(github.getByTestId('credential-github-unconfigured')).toHaveCount(0);
  await expect(github.getByTestId('credential-github-configured')).toHaveCount(0);
  await expect(github.getByTestId('credential-github-unknown')).toBeVisible();
  await expect(github.getByTestId('credential-github-delete')).toHaveCount(0);

  // So a token pasted in that window is typed and held. It is not sent anywhere, because there is no
  // revision to send it against yet, and the control that would send it is not available.
  await github.getByTestId('credential-github-input').fill(SENTINEL);
  expect(writes).toEqual([]);
  await expect(github.getByTestId('credential-github-save')).toBeDisabled();

  // The real read lands, and only then does the row say one of the two true things and offer the
  // write. beforeEach already returned this provider to unconfigured, so the answer is "Not
  // configured" and the revision the read published is 0.
  release();
  await expect(github.getByTestId('credential-github-unknown')).toHaveCount(0);
  await expect(github.getByTestId('credential-github-unconfigured')).toBeVisible();

  // The write now presents the revision that read published, the server accepts it, and the value
  // leaves the field because it was stored.
  await expect(github.getByTestId('credential-github-save')).toBeEnabled();
  await github.getByTestId('credential-github-save').click();
  await waitForStored(page, 'github');

  expect(writes).toEqual(['0']);
  await expect(github.getByTestId('credential-github-input')).toHaveValue('');
  await expect(page.locator(`text=${SENTINEL}`)).toHaveCount(0);

  const described = await readFromThePage(page, '/api/settings/credentials/github');
  expect(described.status).toBe(200);
  expect(described.body.configured).toBe(true);
});

test('the settings route is private, so a browser with no session sees the sign-in and nothing else', async ({
  page,
}) => {
  await page.context().clearCookies();
  await page.goto('/settings/credentials');

  // The product area is behind the gate, so the credential surface is not rendered at all.
  await expect(page.getByTestId('session-token-input')).toBeVisible();
  await expect(page.getByTestId('credential-vault')).toHaveCount(0);

  // And the route itself refuses a request that carries no session, before any handler runs.
  const refused = await page.request.get('/api/settings/credentials/github');
  expect(refused.status()).toBe(401);
});

/**
 * Waits until a save has actually finished, which is not the same as the provider being configured: a
 * replacement changes nothing an owner can see except the instant, and the field is cleared by the
 * save's own continuation. Waiting for the control to be idle is what stops a fixture from typing the
 * next value into a field the previous save is about to clear.
 */
async function waitForStored(page: Page, provider: string) {
  const row = page.getByTestId(`credential-${provider}`);

  await expect(row.getByTestId(`credential-${provider}-saved`)).toBeVisible();
  await expect(row.getByTestId(`credential-${provider}-input`)).toHaveValue('');
  await expect(row.getByTestId(`credential-${provider}-save`)).not.toHaveText('Saving…');
}

/**
 * Returns one provider to the state a new installation is in, using the product's own remove control
 * and waiting for the server's own answer.
 *
 * A row that has not been read yet does not offer a remove control, and must not: the panel cannot
 * say what is stored until the server has told it. So counting remove controls is only a statement
 * about what is stored once the read has landed, and this waits for the row to have stopped saying
 * "Reading" first. That is the panel's own answer to "have you read this provider yet", and it is
 * gone exactly when the provider state the remove control would act on has been applied - not a
 * pause, and not a guess.
 */
async function resetProvider(page: Page, provider: string) {
  const row = page.getByTestId(`credential-${provider}`);
  const remove = row.getByTestId(`credential-${provider}-delete`);

  await expect(row.getByTestId(`credential-${provider}-unknown`)).toHaveCount(0);

  if ((await remove.count()) > 0) {
    await remove.click();
    await expect(row.getByTestId(`credential-${provider}-unconfigured`)).toBeVisible();
  }
}

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/**
 * Reads one of the private settings routes from inside the page, with this browser's own session
 * cookie, and returns the server's own status and body.
 */
async function readFromThePage(
  page: Page,
  path: string,
): Promise<{ status: number; body: Record<string, unknown> }> {
  return page.evaluate(async (target) => {
    const response = await fetch(target, { credentials: 'same-origin' });

    return { status: response.status, body: (await response.json()) as Record<string, unknown> };
  }, path);
}

/**
 * The bodies of every response the browser has received for a path, so an assertion about what did
 * and did not cross the wire is made on what actually crossed it rather than on what the page drew.
 */
async function responseBodies(page: Page, path: string): Promise<string[]> {
  return page.evaluate(async (target) => {
    const response = await fetch(target, { credentials: 'same-origin' });

    return [await response.text()];
  }, path);
}
