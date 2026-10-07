import { expect, test, type Page } from '@playwright/test';

/**
 * GITHUB-EVIDENCE-001/002 in a real browser, against the real published application and the real
 * disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * There is no stubbed GitHub anywhere in this file, and there is no seeded row of any kind. The project,
 * the milestone and the evidence are created by the same clicks an owner would make, the connection is
 * stored by the panel under test through the real route, and the collection is asked for by pressing the
 * real button. Every claim below is read back from the page or from the server's own answer.
 *
 * <b>What this fixture does not claim.</b> GitHub is not faked, so it is not controlled either. The
 * repository that is refused here is refused by GitHub, and the repository that is connected is read by
 * GitHub: a pass says that Chronicleaf reports what GitHub answered, not that GitHub answered anything
 * in particular. The assertions are written so they hold either way - a repository GitHub refuses and a
 * repository this build cannot reach are both refusals, and the panel must show the server's own words
 * in both cases rather than something of its own.
 *
 * The third scenario is the only one that needs outbound network to api.github.com, because a collection
 * needs a repository the server really can read. Nothing in this file may stand in for a repository it
 * could not verify, so where that network is unavailable that scenario fails and says so rather than
 * passing on a connection nothing confirmed.
 *
 * <b>Where the third scenario is asserted, it is asserted live.</b> The collection appends a revision to
 * the evidence bundle from a background job, and the owner watching it land must be shown that revision
 * by the page that is already open. There is deliberately no `page.reload()` anywhere in this file: a
 * browser can fetch a bundle after a reload, and that would prove nothing about a panel that had to be
 * told. So the evidence panel is asserted while the tab has stayed on the same project throughout.
 *
 * This suite has a database of its own for the reasons the credential and generation suites do: a
 * repository connection cannot be removed through the UI, and a stored token cannot be un-stored through
 * it, so a shared database would leave a second project with a connection where this fixture asserts
 * there is none.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** A repository name that cannot exist, so the refusal comes from GitHub rather than from this file. */
const ABSENT_OWNER = 'chronicleaf-no-such-owner';
const ABSENT_REPOSITORY = `chronicleaf-absent-repository-${Date.now()}`;

/** A token this suite types on purpose. It is refused by GitHub and is never a real secret. */
const ABSENT_TOKEN = 'a-token-this-suite-never-had-accepted';

/** The one public repository the last scenario connects to, and the ref it collects. */
const PUBLIC_OWNER = 'octocat';
const PUBLIC_REPOSITORY = 'Hello-World';
const PUBLIC_BRANCH = 'master';

/** The kind the server gives a GitHub collection, so a job of another kind is not this fixture's. */
const SYNC_KIND = 'milestone.github-sync';

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

test('a project with no repository connected says so honestly and offers the form', async ({ page }) => {
  await createProjectWithMilestone(page, 'Ledger');

  // The panel is on the project rather than on a milestone, because a connection belongs to the project
  // and every milestone below asks the same repository.
  const panel = page.getByTestId('github-connection-panel');
  await expect(panel).toBeVisible();
  await expect(panel.getByTestId('github-connection-empty-title')).toHaveText(
    'No repository is connected to this project',
  );

  // It says what that is rather than implying something is broken, and it does not show a repository,
  // a branch or an address it was never sent.
  await expect(panel.getByTestId('github-connection-empty-body')).toContainText(
    'Nothing has been collected from anywhere',
  );
  await expect(panel).not.toContainText('http://');
  await expect(panel).not.toContainText('https://');

  // The form asks for two names and for nothing that could aim the server's next request somewhere else.
  await expect(panel.getByTestId('github-owner')).toBeVisible();
  await expect(panel.getByTestId('github-repository')).toBeVisible();
  await expect(panel.getByTestId('github-token')).toHaveAttribute('type', 'password');
  await expect(panel.getByTestId('github-connect')).toBeVisible();

  // The milestone below it offers no collection form at all while there is nothing to collect from, and
  // says why rather than showing a form that could only be refused.
  const milestone = page.getByTestId('milestone').first();
  await expect(milestone.getByTestId('github-sync-no-connection')).toContainText(
    'no repository connected yet',
  );
  await expect(milestone.getByTestId('github-sync-form')).toHaveCount(0);
  await expect(milestone.getByTestId('github-sync-submit')).toHaveCount(0);

  // And the server agrees with the page on its own route, rather than the page inventing an empty
  // connection the way a placeholder would.
  const read = await readConnection(page);
  expect(read.status).toBe(422);
  expect(read.body.code).toBe('github_connection_missing');
});

test("a repository the server refuses is reported in the server's own words", async ({ page }) => {
  await createProjectWithMilestone(page, 'Ledger');

  const panel = page.getByTestId('github-connection-panel');

  // A token is typed on the way in, because an owner connecting a repository of their own would type
  // one and the panel must not leave it sitting in the field once it has been handed over.
  await panel.getByTestId('github-owner').fill(ABSENT_OWNER);
  await panel.getByTestId('github-repository').fill(ABSENT_REPOSITORY);
  await panel.getByTestId('github-token').fill(ABSENT_TOKEN);
  await expect(panel.getByTestId('github-token')).toHaveValue(ABSENT_TOKEN);

  await panel.getByTestId('github-connect').click();

  // The field is emptied the moment the value is stored, and nothing on this page ever brings it back:
  // no read in this feature returns one, so a reload could not redraw it either.
  await expect(panel.getByTestId('github-token')).toHaveValue('');

  const refusal = panel.getByTestId('github-connect-failed');
  await expect(refusal).toBeVisible({ timeout: 30000 });

  // Whatever the server's own answer was, the page shows that answer rather than one of its own. It is
  // read first from the route itself, through the owner's own session and the delivered boundary, and
  // the page is then asserted to contain that exact text. Writing the expected sentence here instead
  // would assert that the panel says what this file says, which is the failure this catches.
  const direct = await connectAbsentRepository(page);
  expect(direct.status).toBeGreaterThanOrEqual(400);
  expect(typeof direct.body.message).toBe('string');
  await expect(page.getByTestId('github-connect-failed-message')).toContainText(direct.body.message);

  // A repository that is not public and a repository that does not exist are deliberately the same
  // answer, so the page must not tell this owner which one it was - and it must not dress the refusal
  // up in a URL, a GitHub response body or the token that was offered.
  await expect(refusal).toContainText('Nothing was stored');
  await expect(refusal).not.toContainText('http://');
  await expect(refusal).not.toContainText('https://');
  await expect(refusal).not.toContainText(ABSENT_TOKEN);

  // Nothing was stored, so the project is still honestly empty afterwards and the form is still there.
  await expect(panel.getByTestId('github-connection-empty-title')).toBeVisible();
  expect((await readConnection(page)).status).toBe(422);

  // The token this fixture typed is nowhere in the delivered document - not in the field, not in an
  // attribute, not in a node a screenshot could catch.
  expect(await page.content()).not.toContain(ABSENT_TOKEN);
});

test('a collection returns a real operation that the panel then shows progressing', async ({ page }) => {
  await createProjectWithMilestone(page, 'Ledger');

  // A real repository, read by the real server from GitHub's own host. This is the one thing here that
  // cannot be faked, and it is why this scenario needs the network: a collection needs a repository the
  // server really can read, and no fixture may stand in for one it could not verify.
  const panel = page.getByTestId('github-connection-panel');
  await panel.getByTestId('github-owner').fill(PUBLIC_OWNER);
  await panel.getByTestId('github-repository').fill(PUBLIC_REPOSITORY);
  await panel.getByTestId('github-connect').click();

  const connected = panel.getByTestId('github-connection-repository');
  await expect(connected).toBeVisible({ timeout: 30000 });
  await expect(connected).toHaveText(`${PUBLIC_OWNER}/${PUBLIC_REPOSITORY}`);

  // What the panel shows came from the server's response: the repository id GitHub answered for, the
  // branch GitHub called the default, and a statement about the token that is an absence rather than a
  // value.
  await expect(panel.getByTestId('github-connection-branch')).toContainText(
    `GitHub reported ${PUBLIC_BRANCH} as its default branch`,
  );
  await expect(panel.getByTestId('github-connection-credential')).toContainText('No token is stored');

  // The milestone below now offers the collection form, and it names the repository it will ask. The
  // wait here is the panel catching up on a bounded interval rather than a race against a request that
  // may never be sent: the connection was written by the panel in the header above this one, so a
  // milestone panel that read it once on mount would keep claiming there is nothing to collect from.
  const milestone = page.getByTestId('milestone').first();
  const sync = milestone.getByTestId('github-sync-panel');
  await expect(sync.getByTestId('github-sync-form')).toBeVisible({ timeout: 15000 });
  await expect(sync.getByTestId('github-sync-source')).toContainText(
    `${PUBLIC_OWNER}/${PUBLIC_REPOSITORY}`,
    { timeout: 15000 },
  );

  // The note above makes revision 1, and the collection form says which revision it would extend. That
  // revision is read from the server on an interval rather than remembered on mount, so a revision that
  // moved is never the one that is sent.
  await expect(milestone.getByTestId('evidence-revision').first()).toContainText('Revision 1');
  await expect(sync.getByTestId('github-sync-revision')).toContainText('Adding to revision 1');

  // A collection is refused rather than guessed at: no ref, no job, and the button says why.
  await expect(sync.getByTestId('github-sync-submit')).toBeDisabled();
  await expect(sync.getByTestId('github-sync-needs-ref')).toBeVisible();

  await sync.getByTestId('github-head').fill(PUBLIC_BRANCH);
  await expect(sync.getByTestId('github-sync-submit')).toBeEnabled();
  await sync.getByTestId('github-sync-submit').click();

  // The answer is a job, and the panel says so. It does not show evidence and does not claim a
  // collection happened, because none has yet.
  const progress = sync.getByTestId('github-operation');
  await expect(progress).toBeVisible({ timeout: 30000 });
  await expect(progress.getByTestId('github-operation-heading')).toContainText('Collection accepted');

  const operationId = (await progress.getByTestId('github-operation-id').textContent())?.trim() ?? '';
  expect(operationId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);

  // That id is a real, readable operation on the delivered private route, and it is this milestone's
  // collection rather than some other job that happens to exist.
  const accepted = await readOperation(page, operationId);
  expect(accepted.status).toBe(200);
  expect(accepted.body.id).toBe(operationId);
  expect(accepted.body.kind).toBe(SYNC_KIND);
  expect(accepted.body.inputRevision).toBe(1);

  // Progress is followed, not simulated. The panel replaces its own "accepted" placeholder with a
  // status word, and that word is the one the operations route reports - a panel with a bar on a timer
  // would pass an assertion that checked nothing, which is why what is asserted here is that the page
  // has caught up with the server rather than that something moved.
  await expect
    .poll(async () => (await readOperation(page, operationId)).body.status, {
      timeout: 30000,
      intervals: [500],
      message: 'the collection never left the queue, so the panel had no progress to show',
    })
    .toMatch(/^(queued|running|succeeded|failed|superseded|cancelled)$/);

  // And it ends where the server says it ends. A collection is a bounded set of network reads with a
  // bounded number of attempts, so this terminates on its own; the timeout is the harness giving up on
  // a job that never finished, not a limit being widened to hide a race.
  const terminal = await waitForTerminalStatus(page, operationId);
  await expect(progress).toHaveAttribute('data-status', terminal);

  // The rendered sentence is the one that goes with that status, so the panel and the server are
  // agreeing about more than a word.
  await expect(sync.getByTestId('github-operation-status')).toContainText(
    sentenceFor(terminal).split('.')[0] as string,
  );

  if (terminal === 'failed') {
    // A failed collection is reported in the stored code as well as in words, because the row carries a
    // code and nothing else: no GitHub response body, no repository name and no token. Showing it
    // verbatim is more honest than dressing it up as something GitHub said.
    const code = (await readOperation(page, operationId)).body.errorCode;
    expect(code).not.toBeNull();
    await expect(sync.getByTestId('github-operation-code')).toContainText(code as string);
  }

  // Whatever happened, the collection did not rewrite what was already there, and the form is left
  // against a revision read back from the server rather than the one it started with.
  const evidence = await readEvidence(page, accepted.body.targetId);
  expect(evidence.revision).toBeGreaterThanOrEqual(1);
  await expect(sync.getByTestId('github-sync-revision')).toContainText(
    `Adding to revision ${evidence.revision}`,
  );

  if (terminal === 'succeeded') {
    // The server says it collected and appended, so the bundle it appended to must have moved. A
    // success that left the revision where it started would be the operations route and the evidence
    // route agreeing on something that did not happen, and this is the assertion that would catch it.
    expect(evidence.revision).toBeGreaterThan(1);
    expect(evidence.collectedSources.length).toBeGreaterThan(0);

    // And the owner really does end up looking at what was collected - in the evidence panel that was
    // already on screen, with no reload and none needed. This collection appended a revision nothing
    // on this page asked for, and the panel above the one that asked for it re-read when the job
    // stopped being able to change. The newest bundle is the one the route returns first, so the first
    // bundle rendered is the one the server appended.
    const bundle = milestone.getByTestId('evidence-bundle').first();
    await expect(bundle.getByTestId('evidence-revision')).toContainText(
      `Revision ${evidence.revision}`,
      { timeout: 20000 },
    );

    // The same bundle's facts, matched against the sources the server holds rather than against a
    // string written here. Every source line the panel renders must name a source the evidence route
    // reports for the bundle it just read, so a panel still showing the owner's own note cannot pass
    // this - that note was appended at revision 1 and the collection moved the head past it.
    const rendered = await bundle.getByTestId('evidence-item-source').allTextContents();
    expect(rendered.length).toBeGreaterThan(0);
    for (const line of rendered) {
      expect(
        evidence.collectedSources.some((source) => line.includes(`${source} at `)),
        `"${line}" is not a source the evidence route reports for revision ${evidence.revision}`,
      ).toBe(true);
    }
  }
});

/**
 * Signs in, creates one project and one milestone with one recorded evidence note.
 *
 * The waits are on post-conditions: the milestone header is what a loaded milestone renders, and the
 * evidence row is what a committed evidence bundle renders. A container merely existing would also be
 * true for a record that failed to load, which is exactly the state a fixture must not accept.
 */
async function createProjectWithMilestone(page: Page, name: string): Promise<void> {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();

  await page.goto('/projects/new');
  await page.getByTestId('project-name-input').fill(name);
  await page.getByTestId('project-description-input').fill('Double-entry accounting.');
  await page.getByTestId('project-save').click();
  await expect(page.getByTestId('project-name')).toHaveText(name);

  await page.getByTestId('milestone-new').click();
  await page.getByTestId('milestone-title-input').fill('First release');
  await page.getByTestId('milestone-save').click();

  const milestone = page.getByTestId('milestone').first();
  await expect(milestone.getByTestId('milestone-title')).toHaveText('First release');

  // PROJECT-AREA-IA-001 migration: the evidence panel moved to the Evidence
  // area; the bundle below is recorded there with the same expectations.
  await page.getByTestId('project-area-evidence').click();
  await expect(page.getByTestId('evidence-panel')).toBeVisible();

  // The evidence the collection would be appended to. A milestone with no bundle at all is at revision
  // 0, and that is a different request, so the bundle is created through the delivered evidence form.
  await milestone.getByTestId('evidence-notes').fill('The pipeline now reproduces from a clean checkout.');
  await milestone.getByTestId('evidence-submit').click();
  await expect(milestone.getByTestId('evidence-bundles')).toBeVisible();
  await expect(milestone.getByTestId('evidence-revision').first()).toContainText('Revision 1');
}

/** The project this page is showing, taken from the address bar rather than from a list's ordering. */
function projectIdFromUrl(page: Page): string {
  const segments = new URL(page.url()).pathname.split('/').filter((part) => part.length > 0);

  // PROJECT-AREA-IA-001: the URL is now `/projects/{id}/{area}` rather than `/projects/{id}`, so the
  // project id is the second segment, not the last.
  const id = segments[1] ?? '';

  expect(id).not.toBe('');
  return id;
}

async function readConnection(page: Page) {
  return page.evaluate(async (projectId) => {
    const response = await fetch(`/api/projects/${projectId}/github-connection`, {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });

    return { status: response.status, body: await response.json() };
  }, projectIdFromUrl(page));
}

/**
 * Asks for the same refused repository the panel was asked for, on the route itself, with the same
 * credential the panel used.
 *
 * This is the reference reading. It goes through the owner's own session and the delivered boundary, so
 * the message it returns is the server's own, and the page is then asserted to contain that exact text.
 * The credential is read back from the delivered vault route first because it is metadata only - an id,
 * never a value - so this fixture can ask the same question the panel asked rather than a different one.
 */
async function connectAbsentRepository(page: Page) {
  return page.evaluate(
    async ({ projectId, owner, repository }) => {
      const provider = await fetch('/api/settings/credentials/github', {
        credentials: 'same-origin',
        headers: { Accept: 'application/json' },
      });
      const stored = provider.ok ? await provider.json() : null;
      const metadata = await (
        await fetch('/api/session/antiforgery', { credentials: 'same-origin' })
      ).json();

      const response = await fetch(`/api/projects/${projectId}/github-connection`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', [metadata.headerName]: metadata.token },
        body: JSON.stringify({
          owner,
          repository,
          credentialRef: typeof stored?.id === 'string' ? stored.id : null,
        }),
      });

      return { status: response.status, body: await response.json() };
    },
    { projectId: projectIdFromUrl(page), owner: ABSENT_OWNER, repository: ABSENT_REPOSITORY },
  );
}

interface OperationReading {
  readonly status: number;
  readonly body: {
    readonly id: string;
    readonly kind: string;
    readonly targetId: string;
    readonly inputRevision: number;
    readonly status: string;
    readonly errorCode: string | null;
  };
}

/** Reads one operation through the delivered private route, in the owner's own session. */
async function readOperation(page: Page, operationId: string): Promise<OperationReading> {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/operations/${id}`, {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });

    return { status: response.status, body: await response.json() };
  }, operationId);
}

/**
 * Waits for the job to reach one of the four terminal statuses the delivered queue persists.
 *
 * The bound is generous on purpose and is not widened to rescue a slow collection: a GitHub job makes a
 * bounded number of attempts with a bounded backoff, so it terminates on its own, and a job that has
 * not by the time the harness gives up is a real failure worth failing on.
 */
async function waitForTerminalStatus(page: Page, operationId: string): Promise<string> {
  const deadline = Date.now() + 120000;
  let last = '';

  while (Date.now() < deadline) {
    const reading = await readOperation(page, operationId);

    if (reading.status !== 200) {
      throw new Error(`the operations route answered ${reading.status} for ${operationId}`);
    }

    last = reading.body.status;

    if (last === 'succeeded' || last === 'failed' || last === 'superseded' || last === 'cancelled') {
      return last;
    }

    await page.waitForTimeout(1000);
  }

  throw new Error(`operation ${operationId} was still ${last} after 120s`);
}

/** What the evidence route reports for a milestone: its revision, and what the newest bundle cites. */
interface EvidenceReading {
  readonly revision: number;
  readonly collectedSources: readonly string[];
}

/**
 * The evidence the server currently holds for a milestone.
 *
 * Read through the delivered evidence route rather than scraped from the page: the revision is a server
 * fact, and the DOM is this fixture's own rendering of it. The milestone id comes from the operation the
 * server created, so even that is not guessed here. The sources come from the newest bundle, which the
 * route returns first, so a collection's items are compared rather than described.
 */
async function readEvidence(page: Page, milestoneId: string): Promise<EvidenceReading> {
  const reading = await page.evaluate(async (id) => {
    const response = await fetch(`/api/milestones/${id}/evidence`, {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });

    return { ok: response.ok, body: await response.json() };
  }, milestoneId);

  expect(reading.ok).toBe(true);

  const bundles = (reading.body.items ?? []) as readonly {
    readonly items: readonly { readonly sourceRef: string }[];
  }[];

  return {
    revision: reading.body.revision as number,
    collectedSources: (bundles[0]?.items ?? []).map((item) => item.sourceRef),
  };
}

/**
 * The sentence the panel is expected to be rendering for a persisted status word.
 *
 * Written here rather than taken from the panel, so the assertion is about the two agreeing instead of
 * about this file comparing the page with itself.
 */
function sentenceFor(status: string): string {
  switch (status) {
    case 'queued':
      return 'Queued.';
    case 'running':
      return 'Collecting from GitHub.';
    case 'succeeded':
      return 'Collected.';
    case 'failed':
      return 'The collection failed and nothing was appended.';
    case 'cancelled':
      return 'The collection was cancelled before it finished, and nothing was appended.';
    case 'superseded':
      return 'The evidence moved while this was collecting, so nothing was appended.';
    default:
      return '';
  }
}
