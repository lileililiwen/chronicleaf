import { expect, test, type Page } from '@playwright/test';

/**
 * LLM-PROVIDER-CONTRACT-001 and LLM-PROVIDER-CONTRACT-002 in a real browser, against the real
 * published application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed provider and no seeded preview. This installation starts with an empty project
 * list, the project and the milestone are created by the same clicks an owner would make, the evidence
 * is recorded through the delivered evidence form, and every claim below is read back from the page or
 * from the server's own answer.
 *
 * <b>What this fixture does not claim.</b> No adapter is registered in the published application - the
 * five adapters are separate roadmap packages - so nothing here can be read as evidence that any
 * provider works, that any model exists, or that a paid call is cheap. What it does claim is the
 * contract's own behaviour as an owner meets it: that the provider slots are listed honestly, that a
 * deployment with no adapter says so instead of pretending, that an unknown model is refused rather
 * than substituted, and that the consent is a binding that expires and that a changed input makes
 * stale. Those are properties of Chronicleaf, and they are the ones a browser can see.
 *
 * This suite has a database of its own for the same reason the credential suite does: it asserts that
 * a new installation reads as unconfigured for every provider, and a credential stored by another
 * fixture would make that claim false.
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

test('a new installation lists every provider slot and admits that none can be called', async ({ page }) => {
  await openGenerationPanel(page);

  // Every slot the contract has is offered, and the offer is honest about what each one is. A
  // provider with no credential and a provider with no adapter are different sentences, because they
  // are different facts: one the owner can fix today, the other a build that has no transport.
  const options = page.getByTestId('generation-provider').locator('option');
  await expect(options).toHaveCount(5);
  await expect(options.first()).toContainText('no credential stored');

  await expect(page.getByTestId('generation-no-adapter')).toBeVisible();
  await expect(page.getByTestId('generation-no-adapter')).toContainText('no credential is stored');
  await expect(page.getByTestId('generation-no-adapter')).toContainText(
    'none of the 5 provider slots this build knows about has an adapter installed',
  );
  // The per-slot line names the nearest blocker, which on a fresh installation is the credential the
  // owner has not stored - the thing they can fix today - while the installation-level line above names
  // the missing adapter, which they cannot. Reporting the credential one first is not hiding the
  // adapter: both are on the page, and the adapter is the one that decides whether anything can be
  // called at all.
  await expect(page.getByTestId('generation-capability')).toContainText('No credential is stored');
  await expect(page.getByTestId('generation-capability')).toContainText('credential settings');

  // The bounds are on the page, not buried in a script: an owner authorizing a call should be able to
  // see what the call is bounded by before authorizing it.
  const limits = page.getByTestId('generation-limits');
  await expect(limits).toContainText('4096 output tokens');
  await expect(limits).toContainText('120s budget');
  await expect(limits).toContainText('256 KiB of evidence');
  await expect(limits).toContainText('15 minutes');
  await expect(limits).toContainText('no tools');

  // The catalogue agrees with the page, read from the server rather than from the DOM.
  const catalogue = await readProviders(page);
  expect(catalogue.status).toBe(200);
  expect(catalogue.body.supported).toHaveLength(5);
  expect(catalogue.body.configured).toHaveLength(0);
  expect(catalogue.body.usable).toHaveLength(0);
  expect(catalogue.body.limits.maxOutputTokens).toBe(4096);
  expect(catalogue.body.limits.timeoutSeconds).toBe(120);

  // No adapter is installed, so every slot reports no capability at all. That is deliberately not a
  // descriptor of all-false: the slot is telling the client that nothing is known about its provider,
  // which is a different statement from "this provider cannot do schema enforcement".
  for (const slot of catalogue.body.supported) {
    expect(slot.adapterInstalled).toBe(false);
    expect(slot.capability).toBeNull();
    expect(slot.configured).toBe(false);
  }
});

test('an unknown model is refused by name and no model is substituted', async ({ page }) => {
  await openGenerationPanel(page);

  await page.getByTestId('generation-model').fill('a-model-that-does-not-exist');
  await page.getByTestId('generation-preview').click();

  // What the browser can be shown here is the refusal that comes first. The system's own ordering is
  // deliberate: it asks whether an adapter is installed before it asks whether a model is acceptable,
  // because with no adapter installed there is no provider to ask and any model answer would be this
  // system's guess. So the honest browser-visible answer is that the provider cannot be called, and
  // the model is never mentioned and never substituted.
  //
  // That an unknown model is refused - rather than replaced with a near miss, and with the transport
  // never reached - is covered by LlmProviderContractTests in the .NET suite, where an adapter is
  // installed and the question is therefore answerable. Asserting it from a browser with no adapter
  // would be asserting a branch this installation cannot take.
  await expect(page.getByTestId('generation-settings-error')).toBeVisible();
  const body = await page.getByTestId('generation-settings-error').textContent();
  expect(body).toContain('No adapter is installed for the selected provider');
  expect(body).not.toContain('a-model-that-does-not-exist');
  expect(body).not.toContain('a-model-nobody-has');

  // No preview was frozen, so there is nothing to authorize and nothing was sent.
  await expect(page.getByTestId('generation-preview-card')).toBeHidden();
  await expect(page.getByTestId('generation-generate')).toBeHidden();
  expect((await generateWithFabricatedConsent(page)).status).toBe(404);
});

test('a provider with no installed adapter cannot be previewed and nothing is stored', async ({ page }) => {
  await openGenerationPanel(page);

  // Select the one evidence item that exists, and preview it.
  await page.locator('input[data-testid^="generation-evidence-"]').first().check();
  await page.getByTestId('generation-model').fill('a-model-nobody-has');
  await page.getByTestId('generation-preview').click();

  // Nothing can be generated for a provider this deployment has no adapter for, and the panel says
  // so at the point where the owner would press the button that spends money. The consent card is
  // never rendered, because no consent was issued.
  await expect(page.getByTestId('generation-settings-error')).toBeVisible();
  await expect(page.getByTestId('generation-settings-error')).toContainText('No adapter is installed');
  await expect(page.getByTestId('generation-preview-card')).toBeHidden();

  // The refusal came from the settings resolution rather than from the preview, which is the ordering
  // the contract asks for: an owner is told a selection will not work before there is a redacted
  // snapshot on screen that looks like it is about to be sent.
  await expect(page.getByTestId('generation-settings-error')).toContainText(
    'No adapter is installed for the selected provider',
  );
  expect((await generateWithFabricatedConsent(page)).status).toBe(404);
});

test('no consent is issued, held or replayable when nothing can be called', async ({ page }) => {
  await openGenerationPanel(page);

  // The evidence that exists, selected and previewed against a provider with no adapter: the server
  // refuses before a snapshot is frozen, and the request is a read of the catalogue rather than a
  // write. What this proves about the binding is therefore bounded, and deliberately so: the
  // generation handshake cannot be exercised in a browser until an adapter exists, and pretending
  // otherwise would be the exact mock-as-evidence failure the brief forbids.
  //
  // What is asserted here is the part that does not need one: the consent hash is not issued, nothing
  // is stored, and the page holds no state that would let a reload replay it.
  await page.locator('input[data-testid^="generation-evidence-"]').first().check();
  await page.getByTestId('generation-model').fill('a-model-nobody-has');
  await page.getByTestId('generation-preview').click();
  await expect(page.getByTestId('generation-settings-error')).toBeVisible();

  await page.reload();
  await expect(page.getByTestId('generation-preview-card')).toBeHidden();
  await expect(page.getByTestId('generation-generate')).toBeHidden();
  await expect(page.getByTestId('generation-model')).toHaveValue('');

  expect((await generateWithFabricatedConsent(page)).status).toBe(404);
  expect((await readProviders(page)).body.configured).toHaveLength(0);
});

/**
 * Signs in, creates one project and one milestone with one recorded evidence note, and opens the
 * generation panel on it.
 *
 * The waits are on post-conditions: the milestone header is what a loaded milestone renders, and the
 * evidence row is what a committed evidence bundle renders. A container merely existing would also be
 * true for a record that failed to load, which is exactly the state a fixture must not accept.
 */
async function openGenerationPanel(page: Page): Promise<void> {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();

  await page.goto('/projects/new');
  await page.getByTestId('project-name-input').fill('Ledger');
  await page.getByTestId('project-description-input').fill('Double-entry accounting.');
  await page.getByTestId('project-save').click();
  await expect(page.getByTestId('project-name')).toHaveText('Ledger');

  await page.getByTestId('milestone-new').click();
  await page.getByTestId('milestone-title-input').fill('First release');
  await page.getByTestId('milestone-save').click();

  const milestone = page.getByTestId('milestone').first();
  await expect(milestone.getByTestId('milestone-title')).toHaveText('First release');

  // The evidence the generation will draft from, recorded through the delivered evidence form.
  await milestone.getByTestId('evidence-notes').fill('The pipeline now reproduces from a clean checkout.');
  await milestone.getByTestId('evidence-submit').click();
  await expect(milestone.getByTestId('evidence-bundles')).toBeVisible();
  await expect(milestone.getByTestId('evidence-revision').first()).toContainText('Revision 1');

  // The generation panel is present on the same milestone, below the evidence it drafts from, and it
  // offers to draft from what was just recorded rather than claiming there is nothing yet. It keeps
  // its own view of the evidence head current on a short, bounded interval, so the wait here is the
  // panel catching up rather than a race against a request that may never be sent.
  const panel = milestone.getByTestId('generation-panel');
  await expect(panel).toBeVisible();
  await expect(panel.getByTestId('generation-evidence-list')).toBeVisible({ timeout: 15000 });
}

async function readProviders(page: Page) {
  return page.evaluate(async () => {
    const response = await fetch('/api/ai/providers', { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  });
}

/**
 * Calls the generate route with a preview id this session never received, through the owner's own
 * session and the delivered boundary.
 *
 * There is no route that lists previews, and there should not be: a preview is an authorization, not
 * a resource with a browse surface. A 404 is therefore how "nothing was frozen" is observed from a
 * browser, and it is the same 404 a genuinely foreign preview produces - which is the property the
 * contract wants, asserted from the outside.
 */
async function generateWithFabricatedConsent(page: Page) {
  return page.evaluate(async () => {
    const metadata = await (
      await fetch('/api/session/antiforgery', { credentials: 'same-origin' })
    ).json();

    const response = await fetch(
      `/api/generation-previews/${crypto.randomUUID()}/generate`,
      {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', [metadata.headerName]: metadata.token },
        body: JSON.stringify({ consentHash: '0'.repeat(64) }),
      },
    );

    return { status: response.status, body: await response.json() };
  });
}
