import { expect, test, type Page } from '@playwright/test';

/**
 * VOICE-PROFILES-001 and VOICE-PROFILES-002 in a real browser, against the real published application
 * and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed API and no seeded profile: this installation starts with an empty project list,
 * the project is created by the same clicks an owner would make, and every claim below is read back
 * from the page or from the server's own answer.
 *
 * Three waits in this file are on a post-condition rather than on the absence of a control, because
 * an unread row and an empty one render the same thing and a fixture that cannot tell them apart
 * passes for the wrong reason:
 *
 *  1. the profile read has to have published its revision before the save control is offered, and the
 *     fixture waits for the row that says so rather than for the button merely being present;
 *  2. the effective table is waited on by the specific field's value, not by a container existing;
 *  3. a finding is waited on by its span, which is the thing the requirement is actually about.
 *
 * The same discipline governs the writes: no step presents a revision it has not read. A write with a
 * placeholder revision is refused by the server, and the owner's typed text would be stranded behind
 * a conflict they did not cause.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** A Chinese title inside the technical-blog preset's 60-character budget. */
const COMPLIANT_TITLE = '把发布流程改成可复现的流水线';

/** The same term in a different case: the configured one is lower-case, the typed one is not. */
const CASE_VARIANT_BODY =
  '这是一次 Game-Changing 的发布，我们把流水线的构建时间从四十分钟压到了九分钟。';

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

test('an explicit override turns emoji off over a preset that asked for it', async ({ page }) => {
  await openVoicePanel(page);

  // The preset row is read as a fact, not assumed: if the preset did not ask for emoji there would
  // be nothing for the override to win against and the scenario would prove nothing.
  const presetRow = page.getByTestId('voice-effective-emoji');
  await expect(presetRow).toBeVisible();
  await expect(page.getByTestId('voice-preset-emoji')).toHaveText('true');
  await expect(page.getByTestId('voice-source-emoji')).toHaveText('Preset');

  // Turn emoji off through the owner's own control.
  await page.getByTestId('voice-override-emoji').selectOption('false');

  // The effective value changed, and the row says the override is what changed it. Both halves
  // matter: the value alone would not show that the preset had been beaten.
  await expect(page.getByTestId('voice-value-emoji')).toHaveText('false');
  await expect(page.getByTestId('voice-source-emoji')).toHaveText('Your override');
  await expect(page.getByTestId('voice-preset-emoji')).toHaveText('true');
  await expect(page.getByTestId('voice-effective-summary')).toContainText('Emoji is off');

  // The save control is only enabled once the read that publishes the revision has landed, and the
  // fixture waits for that answer rather than assuming the button is ready.
  const save = page.getByTestId('voice-profile-save');
  await expect(save).toBeEnabled();
  await save.click();

  // Wait for the save's own post-condition - the state line naming the revision it produced - before
  // asking the server anything. Reading immediately after the click would race the request and could
  // read the pre-save state.
  await expect(page.getByTestId('voice-profile-state')).toContainText('revision 1');

  const stored = await readProfile(page);
  expect(stored.status).toBe(200);
  expect(stored.body.stored).toBe(true);
  expect(stored.body.revision).toBe(1);
  expect(stored.body.effectiveValues.emoji).toBe(false);
  expect(stored.body.presetValues.emoji).toBe(true);
  expect(stored.body.overriddenFields).toContain('emoji');
});

test('a case-variant forbidden term is reported with the span it fired on', async ({ page }) => {
  await openVoicePanel(page);

  await page.getByTestId('voice-override-forbiddenTerms').fill('game-changing');

  const save = page.getByTestId('voice-profile-save');
  await expect(save).toBeEnabled();
  await save.click();

  await expect(page.getByTestId('voice-value-forbiddenTerms')).toHaveText('game-changing');
  await expect(page.getByTestId('voice-source-forbiddenTerms')).toHaveText('Your override');

  // Type real text that breaks the rule, and check it.
  await page.getByTestId('voice-check-title').fill(COMPLIANT_TITLE);
  await page.getByTestId('voice-check-body').fill(CASE_VARIANT_BODY);
  await page.getByTestId('voice-check-run').click();

  // Wait on the finding itself - its rule, its term and its span - because a panel that merely
  // exists proves nothing about what was reported.
  const finding = page.getByTestId('voice-finding-forbidden_term');
  await expect(finding).toBeVisible();
  await expect(page.getByTestId('voice-finding-rule-forbidden_term')).toContainText('Forbidden term');
  await expect(page.getByTestId('voice-finding-term-forbidden_term')).toContainText('game-changing');
  await expect(page.getByTestId('voice-finding-excerpt-forbidden_term')).toHaveText('Game-Changing');

  // The span is a real offset into the text that was typed, shown in the owner's own coordinates.
  const expectedStart = CASE_VARIANT_BODY.indexOf('Game-Changing');
  await expect(page.getByTestId('voice-finding-span-forbidden_term')).toContainText(
    `characters ${expectedStart + 1}`,
  );
  await expect(page.getByTestId('voice-finding-slice-forbidden_term')).toContainText('Game-Changing');

  // The server agrees, and the text it judged is still exactly what was typed.
  const checked = await checkFromThePage(page, COMPLIANT_TITLE, CASE_VARIANT_BODY);
  expect(checked.status).toBe(200);
  expect(checked.body.clean).toBe(false);
  expect(checked.body.findings).toHaveLength(1);
  expect(checked.body.findings[0].excerpt).toBe('Game-Changing');
  expect(checked.body.findings[0].start).toBe(expectedStart);
  expect(checked.body.findings[0].term).toBe('game-changing');
});

test('compliant text reports no violation and a stored draft is approved unchanged', async ({ page }) => {
  await openVoicePanel(page);

  await page.getByTestId('voice-check-title').fill(COMPLIANT_TITLE);
  await page
    .getByTestId('voice-check-body')
    .fill('我们把发布流程从手工脚本改成了一条可复现的流水线，每一次发布都能追溯到具体的提交。');
  await page.getByTestId('voice-check-run').click();

  await expect(page.getByTestId('voice-check-clean')).toBeVisible();
  await expect(page.getByTestId('voice-check-clean-detail')).toContainText('Your text was not changed.');
  await expect(page.getByTestId('voice-check-findings')).toHaveCount(0);

  // Save the checked text as a draft. It takes a copy of the voice of this moment.
  await page.getByTestId('voice-check-save').click();
  await expect(page.getByTestId('voice-check-saved')).toBeVisible();
  await expect(page.getByTestId('voice-check-saved')).toContainText('judged under that voice');

  // The draft is in the list beside the panel, and opening it shows the voice it was created under.
  // The wait is on the row itself - a list that rendered no rows looks the same as a list that has
  // not answered yet, so counting rows is the post-condition, not a container being present.
  await expect(page.getByTestId('voice-draft')).toHaveCount(1);
  await page.getByTestId('voice-draft').first().click();

  await expect(page.getByTestId('voice-draft-view')).toBeVisible();
  await expect(page.getByTestId('voice-draft-clean')).toBeVisible();
  await expect(page.getByTestId('voice-draft-snapshot')).toContainText('Written under technical-blog');

  // Nothing was flagged, so approval is offered - and the stored text is what the owner typed.
  const approve = page.getByTestId('voice-draft-approve');
  await expect(approve).toBeEnabled();
  await approve.click();

  // Wait for the approval to be reflected on screen before reading the record back: the status line
  // naming "approved" is the post-condition, and asking the server first would race the request.
  await expect(page.getByTestId('voice-draft-snapshot')).toContainText('Status approved');

  const projectId = await projectIdOf(page);
  const stored = await readDraftFromThePage(page, projectId);
  expect(stored.status).toBe(200);
  expect(stored.body.status).toBe('approved');
  expect(stored.body.title).toBe(COMPLIANT_TITLE);
  expect(stored.body.findings).toEqual([]);
});

test('changing the voice after a draft does not reach that draft', async ({ page }) => {
  await openVoicePanel(page);

  // Write a draft under the preset's own voice first.
  await page.getByTestId('voice-check-title').fill(COMPLIANT_TITLE);
  await page.getByTestId('voice-check-body').fill('把发布脚本换成了流水线，回滚也快了。');
  await page.getByTestId('voice-check-save').click();
  await expect(page.getByTestId('voice-check-saved')).toBeVisible();

  const projectId = await projectIdOf(page);
  const before = await readDraftFromThePage(page, projectId);
  expect(before.status).toBe(200);
  const fingerprintBefore = before.body.snapshot.fingerprint as string;
  const revisionBefore = before.body.snapshot.profileRevision as number;

  // Now change the voice in the editor: emoji off and a much narrower title budget.
  await page.getByTestId('voice-override-emoji').selectOption('false');
  await page.getByTestId('voice-override-titleMaxChars').fill('10');
  await page.getByTestId('voice-override-tone').selectOption('casual');

  const save = page.getByTestId('voice-profile-save');
  await expect(save).toBeEnabled();
  await save.click();
  await expect(page.getByTestId('voice-value-titleMaxChars')).toHaveText('10');
  await expect(page.getByTestId('voice-value-tone')).toHaveText('casual');

  // Wait for the save to land before reading it back: the revision in the state line is the
  // post-condition, and asking the server first would race the request in flight. This is the
  // project's first stored voice, so the save lands as revision 1.
  await expect(page.getByTestId('voice-profile-state')).toContainText('revision 1');

  // The profile really did move on: it is stored now, and its values are the overridden ones.
  const profile = await readProfile(page);
  expect(profile.body.stored).toBe(true);
  expect(profile.body.revision).toBe(1);
  expect(profile.body.effectiveValues.emoji).toBe(false);
  expect(profile.body.effectiveValues.titleMaxChars).toBe(10);
  expect(profile.body.effectiveValues.tone).toBe('casual');

  // And the draft written before the change is byte-for-byte what it was: same snapshot fingerprint,
  // same profile revision, same title and body. This is the requirement's boundary case, asserted
  // against the server's stored record rather than against anything this page still holds.
  const after = await readDraftFromThePage(page, projectId);
  expect(after.body.snapshot.fingerprint).toBe(fingerprintBefore);
  expect(after.body.snapshot.profileRevision).toBe(revisionBefore);
  expect(after.body.title).toBe(before.body.title);
  expect(after.body.body).toBe(before.body.body);
  expect(after.body.revision).toBe(before.body.revision);

  // The draft was taken while the project had no stored voice at all - revision 0 - and the voice
  // stored a moment later is revision 1. The gap is the point: a later edit produced a new revision
  // for future work and did not reach a snapshot that had already been taken.
  expect(revisionBefore).toBe(0);
  expect(after.body.snapshot.profileRevision).toBe(0);
  expect(profile.body.revision).toBe(1);

  // And the draft's own effective values still read as the voice it was created under, not the one
  // the profile moved on to.
  expect(after.body.snapshot.values.emoji).toBe(true);
  expect(after.body.snapshot.values.tone).toBe('restrained');
  expect(after.body.snapshot.values.titleMaxChars).toBe(60);
});

/**
 * Signs in, creates one project and opens the voice area on it.
 *
 * The waits are on post-conditions: the project header is what a loaded project renders, and the
 * profile panel plus its resolution row are what a loaded profile renders. A container merely
 * existing would also be true for a project that failed to load, which is exactly the state a
 * fixture must not accept.
 */
async function openVoicePanel(page: Page): Promise<void> {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();

  await page.goto('/projects/new');
  await page.getByTestId('project-name-input').fill('Ledger');
  await page.getByTestId('project-description-input').fill('Double-entry accounting.');
  await page.getByTestId('project-save').click();

  await expect(page.getByTestId('project-name')).toHaveText('Ledger');

  // PROJECT-AREA-IA-001 migration: the voice panels moved to the Content
  // area; the same selectors are asserted there with the same expectations.
  await page.getByTestId('project-area-content').click();
  await expect(page.getByTestId('voice-area')).toBeVisible();

  await page.getByTestId('voice-toggle').click();
  await expect(page.getByTestId('voice-panels')).toBeVisible();
  await expect(page.getByTestId('voice-profile-heading')).toBeVisible();

  // The revision-bearing read has landed: the state line names the project's current voice rather
  // than the "no voice saved yet" wording, and the save control is live.
  await expect(page.getByTestId('voice-profile-state')).toContainText('No voice saved yet');
  await expect(page.getByTestId('voice-profile-save')).toBeEnabled();
}

async function projectIdOf(page: Page): Promise<string> {
  const segments = new URL(page.url()).pathname.split('/').filter((segment) => segment.length > 0);

  return segments[1] ?? '';
}

async function readProfile(page: Page) {
  const projectId = await projectIdOf(page);

  return page.evaluate(async (id) => {
    const response = await fetch(`/api/projects/${id}/voice-profile`, { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  }, projectId);
}

async function checkFromThePage(page: Page, title: string, body: string) {
  const projectId = await projectIdOf(page);

  return page.evaluate(
    async ([id, typedTitle, typedBody]) => {
      const metadata = await (
        await fetch('/api/session/antiforgery', { credentials: 'same-origin' })
      ).json();

      const response = await fetch(`/api/projects/${id}/voice-check`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', [metadata.headerName]: metadata.token },
        body: JSON.stringify({ title: typedTitle, body: typedBody }),
      });

      return { status: response.status, body: await response.json() };
    },
    [projectId, title, body],
  );
}

async function readDraftFromThePage(page: Page, projectId: string) {
  return page.evaluate(async (id) => {
    const listed = await fetch(`/api/projects/${id}/voice-drafts`, { credentials: 'same-origin' });
    const page_ = await listed.json();
    const draftId = page_.items[0].id as string;

    const response = await fetch(`/api/voice-drafts/${draftId}`, { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  }, projectId);
}
