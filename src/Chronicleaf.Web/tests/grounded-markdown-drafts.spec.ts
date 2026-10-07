import { expect, test, type Page } from '@playwright/test';

/**
 * GROUNDED-MARKDOWN-DRAFTS-001 and GROUNDED-MARKDOWN-DRAFTS-002 in a real browser, against the real
 * published application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * **What is real.** The project, the milestone, its evidence and the stored draft below are all real
 * rows in a real database. The project and the milestone the suite creates are made by the same clicks
 * an owner would make, and the one milestone that has to be *unconfirmed* is handed to the delivered
 * intake route the way a collector hands one over, because a milestone the owner writes is confirmed by
 * saving it and there is no control anywhere that makes one unconfirmed. The one seeded draft is written
 * by `scripts/e2e.sh` through the same `INSERT` a completed provider run produces, with a body hash
 * computed from its own bytes. The panel is the shipped one and the server is the published one.
 *
 * **What is a fixture, and stated rather than implied.** The stored draft's *text* is a fixture. No
 * provider was called: this installation has an OpenAI adapter with an empty model allowlist, so every
 * provider is refused before a credential is leased, and that refusal is asserted here as the honest
 * state of a deployment nobody has pointed at anything. So a pass is evidence that the panel renders a
 * stored draft faithfully - its provenance, its citations, its findings and its refusal to render the
 * body as HTML - and is **not** evidence that any provider can be called, that any model exists, or that
 * a draft was generated on this host. The .NET suite is where a real provider answer flows through both
 * generation phases; this one is where the stored result is read by a person.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** The job kind this feature creates, as the server names it. */
const DRAFT_JOB_KIND = 'milestone.grounded-draft';

/** The milestone `scripts/e2e.sh` seeded a stored draft against, by the title it created it with. */
const SEEDED_DRAFT_TITLE = 'First reproducible release';

/** A milestone the owner wrote, with evidence on it and no draft. */
const NO_DRAFT_TITLE = 'Nothing drafted yet';

/** A milestone that arrived from outside, which is a candidate until the owner looks at it. */
const UNCONFIRMED_TITLE = 'Arrived from a collector';

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

test('a milestone with no draft says so, and no provider is offered that can be called', async ({ page }) => {
  await signIn(page);
  await createProjectWithDraftableMilestone(page, 'Ledger', NO_DRAFT_TITLE);

  const panel = row(page, NO_DRAFT_TITLE).getByTestId('draft-panel');
  await expect(panel).toBeVisible();

  // The empty state is the stored state, not a failed read, and the panel says so in those terms.
  await expect(panel.getByTestId('draft-empty')).toBeVisible({ timeout: 15000 });
  await expect(panel.getByTestId('draft-empty-title')).toContainText('No draft has been written');

  // No stored draft means no view, no findings and no claims. A panel that rendered an empty body and an
  // empty finding list here would be indistinguishable from a draft with nothing in it.
  await expect(panel.getByTestId('draft-view')).toBeHidden();

  // The server agrees, through the owner's own session: an absent draft is a 200 with a null asset, not a
  // 404 and not an empty object that looks like a draft.
  const read = await readDraftRoute(page, NO_DRAFT_TITLE);
  expect(read.status).toBe(200);
  expect(read.body.assetId).toBeNull();
  expect(read.body.currentRevision).toBeNull();
  expect(read.body.groundingFindings).toEqual([]);
  expect(read.body.voiceFindings).toEqual([]);

  // The provider catalogue this installation really has, read from the server. An adapter with an empty
  // model allowlist can be present and still call nothing, which is a fact about the configuration and
  // not about the build - so what the panel offers is compared against `usable`, the server's own answer
  // to "what can I actually call right now".
  const catalogue = await readProviders(page);
  expect(catalogue.body.usable).toHaveLength(0);

  await expect(panel.getByTestId('draft-no-provider')).toBeVisible();
  await expect(panel.getByTestId('draft-no-provider')).toContainText(
    'No provider in this installation can be called',
  );

  // No approval control exists anywhere on the panel, and that is a claim about the shipped client: the
  // review package owns approval as a table bound to a revision and a body hash, and a button here would
  // be a control this package cannot honour.
  await expect(panel.locator('button', { hasText: /approve|publish/i })).toHaveCount(0);
});

test('a stored draft renders its provenance, its citations and both finding lists, and never as HTML', async ({ page }) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_DRAFT_TITLE);

  // The seeded draft belongs to a real milestone with a real evidence bundle; its asset id is the one the
  // server holds, read through the owner's own session rather than asserted from a constant.
  const read = await readDraftRoute(page, SEEDED_DRAFT_TITLE);
  expect(read.status).toBe(200);
  expect(read.body.assetId).not.toBeNull();
  expect(read.body.currentRevision).toBe(1);

  const panel = row(page, SEEDED_DRAFT_TITLE).getByTestId('draft-panel');
  await expect(panel.getByTestId('draft-view')).toBeVisible({ timeout: 15000 });
  await expect(panel.getByTestId('draft-empty')).toBeHidden();

  // Draft-only, read from the stored row rather than from anything this client set.
  await expect(panel.getByTestId('draft-status')).toHaveText('draft');

  // The provenance line, in full: a reader deciding whether to trust a paragraph is entitled to know
  // which evidence snapshot it was checked against, which voice, which provider, and which instruction
  // and schema versions were in force.
  await expect(panel.getByTestId('draft-provenance')).toContainText('instruction grounded-markdown/1');
  await expect(panel.getByTestId('draft-provenance')).toContainText('schema grounded-summary/1');
  await expect(panel.getByTestId('draft-provenance')).toContainText('written under voice');
  await expect(panel.getByTestId('draft-hash')).toContainText('body ');

  // The body is shown as source. A <pre> is asserted by tag, because the requirement is about the element
  // rather than a styling convention: a draft can contain a link, a prompt or a tag, and the page asking an
  // owner whether to believe it must not be the page that renders it.
  const body = panel.getByTestId('draft-body');
  await expect(body).toBeVisible();
  await expect(body).toContainText('Reproducible release pipeline');
  expect(await body.evaluate((node) => node.tagName.toLowerCase())).toBe('pre');

  // The body is not interpreted: the fixture stores a raw tag in its text, and a client that rendered
  // Markdown as HTML would have produced an element rather than the characters.
  expect(await body.evaluate((node) => node.querySelectorAll('*').length)).toBe(0);
  await expect(body).toContainText('<b>not bold</b>');

  // The citations are visible as written, and the claim list says what each one rests on. There is one
  // claim per summary item - two changes, one problem solved, one limitation, one next step - and the body
  // cites exactly those five, so a claim list of a different length would mean the stored summary and the
  // stored prose disagree.
  await expect(body).toContainText('[^c1]');
  await expect(panel.getByTestId('draft-claims')).toContainText('What this draft claims');
  const claims = panel.getByTestId('draft-claim');
  await expect(claims).toHaveCount(5);
  await expect(claims.first()).toContainText('rests on 1 evidence item(s)');

  // Both finding lists are present with their own empty states. The grounding one is clean for this
  // fixture because every claim cites collected evidence. The voice one is not, and it must not be: the
  // delivered checker measures the stored body against the voice it was written with, and the fixture's
  // English body does not match the Chinese default profile. A panel that showed an empty voice list
  // there would be reporting that nothing was checked.
  await expect(panel.getByTestId('draft-grounding-clean')).toBeVisible();
  await expect(panel.getByTestId('draft-grounding-clean')).toContainText('not proof the draft is true');
  await expect(panel.getByTestId('draft-voice-findings-item')).toHaveCount(2);
  await expect(panel.getByTestId('draft-voice-findings-item').first()).toContainText('language_mismatch');

  // And the panel says plainly that neither list is a verdict.
  await expect(panel.getByTestId('draft-grounding-findings')).toContainText('not a verdict');

  // A reload changes nothing about the stored state: the panel reads the server rather than keeping a
  // local copy, so what a reload shows is what the database holds.
  await page.reload();
  await expect(row(page, SEEDED_DRAFT_TITLE).getByTestId('draft-body')).toContainText(
    'Reproducible release pipeline',
  );
});

test('a milestone the owner has not confirmed is offered no form, and the server refuses the request', async ({ page }) => {
  await signIn(page);

  // A milestone the owner wrote is confirmed by the act of saving it, so the one state this needs cannot
  // be made by clicking. It is made the way a collector makes it: the intake route, which stores anything
  // that arrives from outside as a candidate. That is the real route a later collection package will call,
  // with this browser's own session cookie and a fresh antiforgery value.
  const projectId = await createProject(page, 'Ledger', 'A project with an unconfirmed milestone.');
  const imported = await importCandidate(page, projectId, UNCONFIRMED_TITLE);
  expect(imported.status).toBe(201);
  expect(imported.body.items).toHaveLength(1);
  expect(imported.body.items[0].created).toBe(true);

  await openProjectHolding(page, UNCONFIRMED_TITLE);

  const target = row(page, UNCONFIRMED_TITLE);
  const panel = target.getByTestId('draft-panel');
  await expect(target.getByTestId('milestone-status')).toHaveText('candidate');
  await expect(panel.getByTestId('draft-unavailable')).toBeVisible({ timeout: 15000 });
  await expect(panel.getByTestId('draft-unavailable')).toContainText('not confirmed');
  await expect(panel.getByTestId('draft-form')).toBeHidden();

  // The browser-visible notice is only half the claim; the server's own refusal is the other half, and it
  // is a 422 naming the milestone rule rather than a validation error about a field. The body deliberately
  // carries a consent this session never received, so the refusal is also proof the milestone rule is
  // decided before a credential could be leased or a job could exist.
  const refused = await requestDraft(page, UNCONFIRMED_TITLE);
  expect(refused.status).toBe(422);
  expect(refused.body.code).toBe('draft_milestone_not_confirmed');

  // Nothing was queued: a draft job is the only thing this feature creates, and a milestone nobody
  // confirmed cannot have one.
  expect(await countDraftOperations(page)).toBe(0);
});

test('a draft cannot be requested without a consent, and the stored draft is left alone', async ({ page }) => {
  await signIn(page);
  await openProjectHolding(page, SEEDED_DRAFT_TITLE);

  // The request route is asked for a preview this session never received. There is no route that lists
  // previews and there should not be - a preview is an authorization, not a resource with a browse surface
  // - so a 404 is how "nothing was authorized" is observed from a browser, and it is the same 404 a
  // foreign preview produces.
  const refused = await requestDraft(page, SEEDED_DRAFT_TITLE, {
    previewId: crypto.randomUUID(),
    consentHash: '0'.repeat(64),
  });
  expect(refused.status).toBe(404);

  // The stored draft the server really holds is untouched by a request that was refused, and no job
  // exists for it.
  const read = await readDraftRoute(page, SEEDED_DRAFT_TITLE);
  expect(read.body.currentRevision).toBe(1);
  expect(await countDraftOperations(page)).toBe(0);
});

/** Signs in through the delivered form, with this run's real access token. */
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
 * fixture that read the wrong project would assert about a record it never looked at. The waits are on the
 * loaded project and on the milestone row, which is what a project that really loaded renders.
 */
async function openProjectHolding(page: Page, title: string): Promise<void> {
  const found = await locateMilestone(page, title);

  if (found === null) {
    throw new Error(`no milestone titled "${title}" exists in this installation`);
  }

  await page.goto(`/projects/${found.projectId}`);
  await expect(page.getByTestId('project-name')).toBeVisible();
  await expect(row(page, title)).toBeVisible();

  // PROJECT-AREA-IA-001 migration: the draft panel moved to the Content area;
  // the same selectors are asserted there with the same expectations.
  await page.getByTestId('project-area-content').click();
  await expect(row(page, title)).toBeVisible();
}

/** Creates a project by the same clicks an owner would make, and returns its id from the URL. */
async function createProject(page: Page, name: string, description: string): Promise<string> {
  await page.goto('/projects/new');
  await page.getByTestId('project-name-input').fill(name);
  await page.getByTestId('project-description-input').fill(description);
  await page.getByTestId('project-save').click();
  await expect(page.getByTestId('project-name')).toHaveText(name);

  return projectIdFromUrl(page);
}

/**
 * Creates one project, one milestone in it and one manual note on that milestone, and leaves the page on
 * the project.
 *
 * The waits are on post-conditions rather than on containers: a milestone header is what a loaded
 * milestone renders, and an evidence row is what a committed bundle renders. A container merely existing
 * would also be true of a record that failed to load, which is exactly the state a fixture must not
 * accept. A milestone the owner writes is confirmed by saving it, so the status is asserted rather than
 * set - there is no control that makes one of these unconfirmed.
 */
async function createProjectWithDraftableMilestone(
  page: Page,
  project: string,
  title: string,
): Promise<void> {
  await createProject(page, project, 'A project with a grounded draft.');

  await page.getByTestId('milestone-new').click();
  await page.getByTestId('milestone-title-input').fill(title);
  await page.getByTestId('milestone-save').click();

  const target = row(page, title);
  await expect(target.getByTestId('milestone-title')).toHaveText(title);
  await expect(target.getByTestId('milestone-status')).toHaveText('confirmed');

  // PROJECT-AREA-IA-001 migration: the evidence form moved to the Evidence
  // area and the draft panel to Content; the bundle below is recorded in
  // Evidence with the same expectations, then the suite moves to Content.
  await page.getByTestId('project-area-evidence').click();
  await expect(row(page, title)).toBeVisible();

  // The evidence a draft claims to rest on, recorded through the delivered evidence form. A draft whose
  // citations pointed at evidence that did not exist would make every provenance assertion meaningless.
  await target.getByTestId('evidence-notes').fill('The pipeline now reproduces from a clean checkout.');
  await target.getByTestId('evidence-submit').click();
  await expect(target.getByTestId('evidence-bundles')).toBeVisible();

  await page.getByTestId('project-area-content').click();
  await expect(row(page, title)).toBeVisible();
}

/** The project id in the address, which is the only place the application ever exposes it. */
function projectIdFromUrl(page: Page): string {
  const segments = new URL(page.url()).pathname.split('/').filter((segment) => segment.length > 0);

  return segments[1] ?? '';
}

/**
 * Hands one upstream record to the intake route, which stores it as a candidate rather than as history.
 * The call carries this browser's own session cookie and a fresh antiforgery value, and it is the same
 * call a collection package will make - there is no UI that pretends to be a collector.
 */
async function importCandidate(page: Page, projectId: string, title: string) {
  return page.evaluate(
    async ([id, recordTitle]) => {
      const metadata = await (
        await fetch('/api/session/antiforgery', { credentials: 'same-origin' })
      ).json();

      const response = await fetch(`/api/projects/${id}/milestones/import`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', [metadata.headerName]: metadata.token },
        body: JSON.stringify({
          source: 'e2e',
          candidates: [
            {
              sourceKey: `e2e-${crypto.randomUUID()}`,
              title: recordTitle,
              occurredAt: '2026-10-01T09:00:00.000Z',
              type: 'release',
              notes: 'Imported by the fixture, so it is a candidate.',
            },
          ],
        }),
      });

      return { status: response.status, body: await response.json() };
    },
    [projectId, title],
  );
}

/** The milestone row with this exact title, so two milestones in two projects cannot be confused. */
function row(page: Page, title: string) {
  return page
    .getByTestId('milestone')
    .filter({ has: page.getByTestId('milestone-title').filter({ hasText: title }) })
    .first();
}

/** The draft route for the milestone with this title, read through the owner's own session. */
async function readDraftRoute(page: Page, title: string) {
  const found = await locateMilestone(page, title);

  if (found === null) {
    return {
      status: 0,
      body: { assetId: null, currentRevision: null, groundingFindings: [], voiceFindings: [] },
    };
  }

  return page.evaluate(async (id) => {
    const response = await fetch(`/api/milestones/${id}/drafts`, { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  }, found.milestoneId);
}

/** The provider catalogue, so what the panel offers can be compared against what can be called. */
async function readProviders(page: Page) {
  return page.evaluate(async () => {
    const response = await fetch('/api/ai/providers', { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  });
}

/** The rows of this feature's own job kind, counted through the real operations route. */
async function countDraftOperations(page: Page): Promise<number> {
  return page.evaluate(async (kind) => {
    const response = await fetch('/api/operations?limit=100', { credentials: 'same-origin' });

    if (!response.ok) {
      return -1;
    }

    const body = (await response.json()) as { readonly items: readonly { readonly kind: string }[] };

    return body.items.filter((item) => item.kind === kind).length;
  }, DRAFT_JOB_KIND);
}

/**
 * Asks for a draft for the milestone with this title, through the real route and the delivered
 * antiforgery value.
 *
 * The body deliberately carries a consent this session never received, which is the state a client is in
 * when it has not authorized anything. The server refuses before a job exists, and the panel shows the
 * server's own words.
 */
async function requestDraft(
  page: Page,
  title: string,
  draft: { readonly previewId?: string; readonly consentHash?: string } = {},
) {
  const found = await locateMilestone(page, title);

  if (found === null) {
    return { status: 0, body: {} };
  }

  return page.evaluate(
    async ({ id, body }) => {
      const metadata = await (
        await fetch('/api/session/antiforgery', { credentials: 'same-origin' })
      ).json();

      const response = await fetch(`/api/milestones/${id}/drafts`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', [metadata.headerName]: metadata.token },
        body: JSON.stringify(body),
      });

      return { status: response.status, body: await response.json() };
    },
    {
      id: found.milestoneId,
      body: {
        previewId: draft.previewId ?? crypto.randomUUID(),
        consentHash: draft.consentHash ?? '0'.repeat(64),
        providerId: 'anthropic',
        model: 'a-model-nobody-has',
        evidenceRevision: 1,
        voiceRevision: 0,
        idempotencyKey: `browser-${Date.now()}-${Math.random()}`,
      },
    },
  );
}

/**
 * The project and milestone with this exact title, found through the owner's own read routes.
 *
 * Written once, on this side of the boundary, because four scenarios need it and four copies would be four
 * places for the routes to drift. It reads the project list and then each project's milestones - the same
 * two calls the panel itself makes - so it cannot answer with a record the application would not show. It
 * runs here rather than inside `page.evaluate` because a helper defined outside a page function is not in
 * the page: a closure cannot cross the boundary, and asking the page to call one is a ReferenceError
 * dressed up as a missing milestone.
 */
async function locateMilestone(page: Page, title: string): Promise<{ projectId: string; milestoneId: string } | null> {
  return page.evaluate(async (wanted) => {
    const projects = await fetch('/api/projects', { credentials: 'same-origin' });

    if (!projects.ok) {
      return null;
    }

    const catalogue = (await projects.json()) as { readonly items: readonly { readonly id: string }[] };

    for (const project of catalogue.items) {
      const response = await fetch(`/api/projects/${project.id}/milestones`, { credentials: 'same-origin' });

      if (!response.ok) {
        continue;
      }

      const list = (await response.json()) as {
        readonly items: readonly { readonly id: string; readonly title: string }[];
      };

      const found = list.items.find((item) => item.title === wanted);

      if (found !== undefined) {
        return { projectId: project.id, milestoneId: found.id };
      }
    }

    return null;
  }, title);
}
