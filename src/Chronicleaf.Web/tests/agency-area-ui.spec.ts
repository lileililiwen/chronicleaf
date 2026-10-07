import { readFile } from 'node:fs/promises';

import { expect, test, type Download, type Page } from '@playwright/test';

/**
 * AGENCY-AREA-UI-001/002/003 in a real browser, against the real published
 * application and the real disposable PostgreSQL that scripts/e2e.sh starts.
 *
 * This installation starts with Client A (Agency Project 1, Agency Project 2,
 * each with one confirmed in-window milestone carrying manual evidence),
 * Client B (Agency Project 3) and one archived client — all created through
 * the delivered routes by `seed_agency_area`. Every report row below comes
 * only from the UI's own generate calls; the seed inserts none, because a
 * report is rendered by the delivered template and this host needs no
 * provider to reach one.
 *
 * What this suite claims, and what it does not. A pass is evidence that
 * client CRUD → assign → generate (DST week) → per-project preview → approve
 * → Markdown/HTML downloads serve bytes byte-equal to the delivered export
 * route fetched directly; that refusal codes are verbatim
 * (`client_archived`, `weekly_report_project_outside_client`,
 * `approval_required`, `approval_stale`, `revision_conflict`, `not_found`);
 * that the archived client stays readable with assignment disabled; that
 * regeneration appends a revision starting unapproved; that evidence drift
 * ends the export until re-approval; that empty states are honest; and that
 * nothing posts before its control and no request ever leaves loopback. It is
 * NOT evidence of any portal, invoicing or email behavior: no such route or
 * control exists, and the suite asserts no request ever aims at one.
 */
const ACCESS_TOKEN = process.env.CHRONICLEAF_E2E_ACCESS_TOKEN;

/** The DST week the seed's milestones fall in, with its IANA zone. */
const DST_WEEK = '2026-03-08';
const DST_ZONE = 'America/New_York';

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

test('clients are one nav step away; CRUD, assign, report, approve, byte-equal exports', async ({
  page,
}) => {
  await signIn(page);

  // AGENCY-AREA-UI-003: the Clients entry is in the top nav beside the
  // delivered entries, and it marks itself current.
  await page.getByTestId('nav-clients').click();
  await expect(page.getByTestId('nav-clients')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByRole('heading', { name: 'Clients', exact: true })).toBeVisible();

  const fired = armPostCounter(page);

  // The seeded hierarchy is listed: Client A with its two projects named.
  await expect(page.getByTestId('clients-list')).toBeVisible();
  await expect(
    page.getByTestId('client-card').filter({ hasText: 'Client A' }),
  ).toBeVisible();
  await expect(
    page.getByTestId('client-card').filter({ hasText: 'Client B' }),
  ).toBeVisible();
  expect(postsTo(fired, /\/api\/clients/)).toEqual([]);

  // Create → detail: the new client opens on save.
  await page.getByTestId('clients-new').click();
  await page.getByTestId('client-form-name').fill('Client C');
  await page.getByTestId('client-form-notes').fill('Carried by hand, never sent.');
  await page.getByTestId('client-form-save').click();
  await expect(page.getByTestId('client-name')).toHaveText('Client C');
  const clientC = clientIdFromPath(page);

  // Edit at the read revision: the rename lands and the notes persist.
  await page.getByTestId('nav-clients').click();
  const rowC = clientRow(page, 'Client C');
  await rowC.getByTestId('client-edit').click();
  await page.getByTestId('client-form-name').fill('Client C Renamed');
  await page.getByTestId('client-form-save').click();
  await expect(clientRow(page, 'Client C Renamed')).toBeVisible();

  // Open Client A and assign nothing new: both seeded projects are members.
  await clientRow(page, 'Client A').getByTestId('client-card').click();
  await expect(page.getByTestId('client-name')).toHaveText('Client A');
  const clientA = clientIdFromPath(page);
  await expect(page.getByTestId('client-projects-list')).toContainText('Agency Project 1');
  await expect(page.getByTestId('client-projects-list')).toContainText('Agency Project 2');

  // The report context names the membership generation both projects share.
  await page.getByTestId('client-context-preview').click();
  await expect(page.getByTestId('client-context-note')).toContainText('Agency Project 1');
  await expect(page.getByTestId('client-context-note')).toContainText('Agency Project 2');

  // Generate over the DST week: the window still covers exactly seven local
  // calendar dates, as the backend defines them.
  const panel = page.getByTestId('weekly-report-panel');
  await expect(panel.getByTestId('weekly-report-empty')).toBeVisible();
  await panel.getByTestId('weekly-report-week').fill(DST_WEEK);
  await panel.getByTestId('weekly-report-timezone').fill(DST_ZONE);

  const [generateResponse] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        new URL(response.url()).pathname.endsWith('/weekly-reports'),
    ),
    panel.getByTestId('weekly-report-generate').click(),
  ]);

  expect(generateResponse.status()).toBe(201);
  const generated = (await generateResponse.json()) as {
    id: string;
    revision: number;
    windowStartUtc: string;
    windowEndUtc: string;
  };
  expect(generated.id).toMatch(/^[0-9a-f-]{36}$/i);
  expect(generated.revision).toBe(1);

  // The DST transition moves the instants without moving the dates: Mar 8
  // 00:00 EST is 05:00Z and Mar 15 00:00 EDT is 04:00Z.
  expect(generated.windowStartUtc).toContain('2026-03-08T05:00');
  expect(generated.windowEndUtc).toContain('2026-03-15T04:00');
  await expect(panel.getByTestId('weekly-report-meta')).toContainText(
    `Revision ${generated.revision}`,
  );
  await expect(panel.getByTestId('weekly-report-meta')).toContainText('2026-03-08T05:00');
  await expect(panel.getByTestId('weekly-report-meta')).toContainText('2026-03-15T04:00');

  // Per-project source preview: both member projects are attributed.
  await expect(panel.getByTestId('weekly-report-sources')).toContainText('Agency Project 1');
  await expect(panel.getByTestId('weekly-report-sources')).toContainText('Agency Project 2');
  await expect(panel.getByTestId('weekly-report-body')).not.toBeEmpty();
  await expect(panel.getByTestId('weekly-report-status')).toHaveText('draft');
  await expect(panel.getByTestId('weekly-report-download-unavailable')).toBeVisible();

  // Approval binds the shown revision; both downloads are the server's bytes.
  await panel.getByTestId('weekly-report-approve').click();
  await expect(panel.getByTestId('weekly-report-status')).toHaveText('approved');

  for (const [control, format] of [
    ['weekly-report-download-markdown', 'markdown'],
    ['weekly-report-download-html', 'html'],
  ] as const) {
    const [download] = await Promise.all([
      page.waitForEvent('download'),
      panel.getByTestId(control).click(),
    ]);

    const offered = await downloadBytes(download);
    const direct = await exportBytesFromThePage(page, clientA, generated.id, format);
    expect(offered.equals(direct)).toBe(true);
    expect(offered.length).toBeGreaterThan(0);
  }

  // Regeneration appends a revision that starts unapproved: the old approval
  // does not carry over, and export waits behind the new approval.
  await panel.getByTestId('weekly-report-generate').click();
  await expect(panel.getByTestId('weekly-report-meta')).toContainText(
    `Revision ${generated.revision + 1}`,
  );
  await expect(panel.getByTestId('weekly-report-status')).toHaveText('draft');
  await expect(panel.getByTestId('weekly-report-download-unavailable')).toBeVisible();

  expect(clientC.length).toBe(36);
  assertNothingLeaves(fired);
});

test('refusal codes are verbatim; archived stays readable; drift ends approval', async ({
  page,
}) => {
  await signIn(page);
  await page.getByTestId('nav-clients').click();

  // The archived seed client is listed with its badge and stays readable.
  const archivedRow = clientRow(page, 'Archived Client');
  await expect(archivedRow.getByTestId('client-card-archived')).toBeVisible();
  await archivedRow.getByTestId('client-card').click();
  await expect(page.getByTestId('client-name')).toHaveText('Archived Client');
  await expect(page.getByTestId('client-archived-note')).toContainText('Archived');
  await expect(page.getByTestId('client-assign-disabled')).toContainText('client_archived');

  // Assigning to the archived client is refused in the server's own words.
  const archivedId = clientIdFromPath(page);
  const outsider = await projectOutside(page, archivedId);
  const archivedRefusal = await postAssignment(page, outsider.id, outsider.revision, archivedId);
  expect(archivedRefusal.status).toBe(409);
  expect(archivedRefusal.code).toBe('client_archived');

  // A foreign client id is not_found, never named.
  const foreignRefusal = await readClient(page, '00000000-0000-0000-0000-000000000000');
  expect(foreignRefusal.status).toBe(404);
  expect(foreignRefusal.code).toBe('not_found');

  // A stale client edit loses loudly.
  await page.getByTestId('nav-clients').click();
  await clientRow(page, 'Client A').getByTestId('client-card').click();
  const clientA = clientIdFromPath(page);
  const staleEdit = await patchClient(page, clientA, 1_000_000, 'Client A', 'Stale notes.');
  expect(staleEdit.status).toBe(409);
  expect(staleEdit.code).toBe('revision_conflict');

  // A project outside the client is rejected before anything is rendered.
  const foreignProject = await projectOfClient(page, 'Client B');
  const outsideRefusal = await generateFor(
    page,
    clientA,
    [foreignProject.id],
    '2026-03-08',
    'America/New_York',
  );
  expect(outsideRefusal.status).toBe(422);
  expect(outsideRefusal.code).toBe('weekly_report_project_outside_client');

  // Generate an empty week first: no recorded completion is reported
  // honestly, never invented — and export before approval is refused.
  const panel = page.getByTestId('weekly-report-panel');
  await panel.getByTestId('weekly-report-week').fill('2026-01-05');
  await panel.getByTestId('weekly-report-timezone').fill('UTC');

  const [emptyGenerate] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        new URL(response.url()).pathname.endsWith('/weekly-reports'),
    ),
    panel.getByTestId('weekly-report-generate').click(),
  ]);

  expect(emptyGenerate.status()).toBe(201);
  const emptyReportId = ((await emptyGenerate.json()) as { id: string }).id;
  await expect(panel.getByTestId('weekly-report-body')).toContainText(
    'No recorded completed milestones',
  );

  const earlyExport = await exportDirect(page, clientA, emptyReportId, 'markdown', null);
  expect(earlyExport.status).toBe(409);
  expect(earlyExport.code).toBe('approval_required');

  // Approve, then drift the evidence inside the reported week: the export
  // ends until re-approval, and the approved stamp cannot simply be
  // re-struck onto moved sources.
  await panel.getByTestId('weekly-report-week').fill('2026-03-08');
  await panel.getByTestId('weekly-report-timezone').fill('America/New_York');

  const [driftGenerate] = await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        new URL(response.url()).pathname.endsWith('/weekly-reports'),
    ),
    panel.getByTestId('weekly-report-generate').click(),
  ]);

  expect(driftGenerate.status()).toBe(201);
  const driftReport = (await driftGenerate.json()) as { id: string; revision: number };
  const driftReportId = driftReport.id;
  await expect(panel.getByTestId('weekly-report-status')).toHaveText('draft');

  await panel.getByTestId('weekly-report-approve').click();
  await expect(panel.getByTestId('weekly-report-status')).toHaveText('approved');
  await appendManualEvidence(page, 'Agency Project 1', 'Drift note after approval.');
  const driftedExport = await exportDirect(page, clientA, driftReportId, 'markdown', null);
  expect(driftedExport.status).toBe(409);
  expect(driftedExport.code).toBe('approval_stale');

  const reread = await getReportDirect(page, clientA, driftReportId);
  const staleApprove = await approveDirect(page, clientA, driftReportId, reread.revision);
  expect(staleApprove.status).toBe(409);
  expect(staleApprove.code).toBe('approval_stale');

  // The panel names the state honestly after drift: re-reading shows the
  // approved stamp no longer authorizes an export, and regeneration is the
  // remedy — a new revision starts unapproved and the flow completes again.
  await panel.getByTestId('weekly-report-refresh').click();
  await expect(panel.getByTestId('weekly-report-status')).toHaveText('approved');
  await panel.getByTestId('weekly-report-generate').click();
  await expect(panel.getByTestId('weekly-report-meta')).toContainText(
    `Revision ${driftReport.revision + 1}`,
  );
  await expect(panel.getByTestId('weekly-report-status')).toHaveText('draft');
});

test('empty client and unassigned project states stay honest', async ({ page }) => {
  await signIn(page);
  await page.getByTestId('nav-clients').click();

  await page.getByTestId('clients-new').click();
  await page.getByTestId('client-form-name').fill('Empty Client');
  await page.getByTestId('client-form-notes').fill('');
  await page.getByTestId('client-form-save').click();
  await expect(page.getByTestId('client-name')).toHaveText('Empty Client');

  await expect(page.getByTestId('client-projects-empty')).toContainText(
    'No projects grouped under this client yet',
  );
  await expect(page.getByTestId('weekly-report-no-projects')).toContainText(
    'nothing to report on yet',
  );
  await expect(page.getByTestId('weekly-report-empty')).toBeVisible();
  await expect(page.getByTestId('weekly-report-generate')).toHaveCount(1);
});

async function signIn(page: Page) {
  await page.getByTestId('session-token-input').focus();
  await page.keyboard.type(ACCESS_TOKEN as string);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Owner session' })).toBeVisible();
}

/** The client list row holding the card with this exact name. */
function clientRow(page: Page, name: string) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  return page
    .locator('li')
    .filter({
      has: page.getByTestId('client-card-name').filter({ hasText: new RegExp(`^${escaped}$`) }),
    })
    .first();
}

function clientIdFromPath(page: Page): string {
  const match = /\/clients\/([0-9a-f-]{36})$/i.exec(new URL(page.url()).pathname);

  if (!match) {
    throw new Error(`the client detail path holds no client id: ${page.url()}`);
  }

  return match[1];
}

async function projectOutside(page: Page, archivedId: string) {
  return page.evaluate(async (archived) => {
    const projects = await fetch('/api/projects', { credentials: 'same-origin' });
    const catalogue = (await projects.json()) as {
      readonly items: readonly { readonly id: string; readonly revision: number; readonly clientId: string | null }[];
    };
    const candidate = catalogue.items.find((item) => item.clientId !== archived);

    if (!candidate) {
      throw new Error('no project outside the archived client exists');
    }

    return { id: candidate.id, revision: candidate.revision };
  }, archivedId);
}

async function projectOfClient(page: Page, name: string) {
  return page.evaluate(async (wanted) => {
    const clients = await fetch('/api/clients', { credentials: 'same-origin' });
    const catalogue = (await clients.json()) as { readonly items: readonly { readonly id: string; readonly name: string }[] };
    const client = catalogue.items.find((item) => item.name === wanted);

    if (!client) {
      throw new Error(`no client named "${wanted}" exists`);
    }

    const response = await fetch(`/api/clients/${client.id}/projects`, {
      credentials: 'same-origin',
    });
    const members = (await response.json()) as { readonly items: readonly { readonly id: string }[] };

    if (members.items.length === 0) {
      throw new Error(`client "${wanted}" holds no projects`);
    }

    return { id: members.items[0].id };
  }, name);
}

async function postAssignment(page: Page, projectId: string, revision: number, clientId: string | null) {
  return page.evaluate(
    async ({ project, expectedRevision, client }) => {
      const antiforgery = await fetch('/api/session/antiforgery', { credentials: 'same-origin' });
      const metadata = (await antiforgery.json()) as { headerName: string; token: string };
      const response = await fetch(`/api/projects/${project}/client`, {
        method: 'PATCH',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'If-Match': String(expectedRevision),
          [metadata.headerName]: metadata.token,
        },
        body: JSON.stringify({ clientId: client }),
      });
      const body = (await response.json()) as { code: string };

      return { status: response.status, code: body.code };
    },
    { project: projectId, expectedRevision: revision, client: clientId },
  );
}

async function readClient(page: Page, clientId: string) {
  return page.evaluate(async (id) => {
    const response = await fetch(`/api/clients/${id}`, { credentials: 'same-origin' });
    const body = (await response.json()) as { code: string };

    return { status: response.status, code: body.code };
  }, clientId);
}

async function patchClient(page: Page, clientId: string, revision: number, name: string, notes: string) {
  return page.evaluate(
    async ({ id, expectedRevision, clientName, clientNotes }) => {
      const antiforgery = await fetch('/api/session/antiforgery', { credentials: 'same-origin' });
      const metadata = (await antiforgery.json()) as { headerName: string; token: string };
      const response = await fetch(`/api/clients/${id}`, {
        method: 'PATCH',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'If-Match': String(expectedRevision),
          [metadata.headerName]: metadata.token,
        },
        body: JSON.stringify({ name: clientName, notes: clientNotes }),
      });
      const body = (await response.json()) as { code: string };

      return { status: response.status, code: body.code };
    },
    { id: clientId, expectedRevision: revision, clientName: name, clientNotes: notes },
  );
}

async function generateFor(
  page: Page,
  clientId: string,
  projectIds: readonly string[],
  weekStart: string,
  timeZone: string,
) {
  return page.evaluate(
    async ({ client, projects, week, zone }) => {
      const antiforgery = await fetch('/api/session/antiforgery', { credentials: 'same-origin' });
      const metadata = (await antiforgery.json()) as { headerName: string; token: string };
      const response = await fetch(`/api/clients/${client}/weekly-reports`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          [metadata.headerName]: metadata.token,
        },
        body: JSON.stringify({ projectIds: [...projects], weekStart: week, timeZone: zone }),
      });
      const body = (await response.json()) as { code: string };

      return { status: response.status, code: body.code };
    },
    { client: clientId, projects: projectIds, week: weekStart, zone: timeZone },
  );
}

async function getReportDirect(page: Page, clientId: string, reportId: string) {
  return page.evaluate(
    async ({ client, report }) => {
      const response = await fetch(`/api/clients/${client}/weekly-reports/${report}`, {
        credentials: 'same-origin',
      });

      return (await response.json()) as { revision: number };
    },
    { client: clientId, report: reportId },
  );
}

async function approveDirect(page: Page, clientId: string, reportId: string, revision: number) {
  return page.evaluate(
    async ({ client, report, expectedRevision }) => {
      const antiforgery = await fetch('/api/session/antiforgery', { credentials: 'same-origin' });
      const metadata = (await antiforgery.json()) as { headerName: string; token: string };
      const response = await fetch(`/api/clients/${client}/weekly-reports/${report}/approve`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'If-Match': String(expectedRevision),
          [metadata.headerName]: metadata.token,
        },
        body: JSON.stringify({}),
      });
      const body = (await response.json()) as { code: string };

      return { status: response.status, code: body.code };
    },
    { client: clientId, report: reportId, expectedRevision: revision },
  );
}

async function exportDirect(
  page: Page,
  clientId: string,
  reportId: string,
  format: 'markdown' | 'html',
  revision: number | null,
) {
  return page.evaluate(
    async ({ client, report, kind, wanted }) => {
      const query = wanted === null ? '' : `&revision=${wanted}`;
      const response = await fetch(
        `/api/clients/${client}/weekly-reports/${report}/export?format=${kind}${query}`,
        { credentials: 'same-origin' },
      );

      if (response.ok) {
        return { status: response.status, code: 'ok' };
      }

      const body = (await response.json()) as { code: string };

      return { status: response.status, code: body.code };
    },
    { client: clientId, report: reportId, kind: format, wanted: revision },
  );
}

async function exportBytesFromThePage(
  page: Page,
  clientId: string,
  reportId: string,
  format: 'markdown' | 'html',
): Promise<Buffer> {
  const base64 = await page.evaluate(
    async ({ client, report, kind }) => {
      const response = await fetch(
        `/api/clients/${client}/weekly-reports/${report}/export?format=${kind}`,
        { credentials: 'same-origin' },
      );

      if (!response.ok) {
        throw new Error(`the export route answered ${response.status} for format ${kind}`);
      }

      const buffer = await response.arrayBuffer();
      const bytes = new Uint8Array(buffer);
      let binary = '';

      for (const byte of bytes) {
        binary += String.fromCharCode(byte);
      }

      return btoa(binary);
    },
    { client: clientId, report: reportId, kind: format },
  );

  return Buffer.from(base64, 'base64');
}

async function appendManualEvidence(page: Page, projectName: string, notes: string) {
  return page.evaluate(async ({ wanted, note }) => {
    const projects = await fetch('/api/projects', { credentials: 'same-origin' });
    const catalogue = (await projects.json()) as { readonly items: readonly { readonly id: string; readonly name: string }[] };
    const project = catalogue.items.find((item) => item.name === wanted);

    if (!project) {
      throw new Error(`no project named "${wanted}" exists`);
    }

    const milestones = await fetch(`/api/projects/${project.id}/milestones`, {
      credentials: 'same-origin',
    });
    const listing = (await milestones.json()) as { readonly items: readonly { readonly id: string }[] };

    if (listing.items.length === 0) {
      throw new Error(`project "${wanted}" holds no milestones`);
    }

    // The append is conditional on the head the seed read: presenting the
    // stored revision is what makes this an append rather than a blind write.
    const head = await fetch(`/api/milestones/${listing.items[0].id}/evidence`, {
      credentials: 'same-origin',
    });
    const bundle = (await head.json()) as { revision: number };

    if (typeof bundle.revision !== 'number') {
      throw new Error(`no evidence head is readable for project "${wanted}"`);
    }

    const antiforgery = await fetch('/api/session/antiforgery', { credentials: 'same-origin' });
    const metadata = (await antiforgery.json()) as { headerName: string; token: string };
    const response = await fetch(`/api/milestones/${listing.items[0].id}/evidence/manual`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        'Content-Type': 'application/json',
        'If-Match': String(bundle.revision),
        [metadata.headerName]: metadata.token,
      },
      body: JSON.stringify({ notes: note, expectedRevision: bundle.revision }),
    });

    if (!response.ok) {
      throw new Error(`the manual evidence route answered ${response.status}`);
    }
  }, { wanted: projectName, note: notes });
}

/**
 * Counts the POSTs the page issues from here on. A panel that merely renders
 * must not post: the paths are read after the clicks, never awaited.
 */
function armPostCounter(page: Page): { posts: string[]; remote: string[] } {
  const fired = { posts: [] as string[], remote: [] as string[] };

  page.on('request', (request) => {
    const url = new URL(request.url());

    if (url.hostname !== '127.0.0.1' && url.hostname !== 'localhost') {
      fired.remote.push(request.url());
    }

    if (request.method() === 'POST') {
      fired.posts.push(url.pathname);
    }
  });

  return fired;
}

/** The POST paths fired so far whose pathname matches this pattern. */
function postsTo(fired: { posts: string[] }, pattern: RegExp): string[] {
  return fired.posts.filter((path) => pattern.test(path));
}

/**
 * Nothing posts anywhere unexpected: every request stayed on the loopback
 * application, and every POST went to a delivered client, project or report
 * route — never to a portal, an invoice or a mail host, because no such
 * route exists.
 */
function assertNothingLeaves(fired: { posts: string[]; remote: string[] }) {
  expect(fired.remote, 'a request left the loopback application').toEqual([]);

  for (const path of fired.posts) {
    expect(path, `unexpected POST to ${path}`).toMatch(
      /^\/api\/clients(\/[0-9a-f-]+(\/weekly-reports(\/[0-9a-f-]+\/approve)?)?)?$/i,
    );
  }
}

async function downloadBytes(download: Download): Promise<Buffer> {
  const path = await download.path();

  if (!path) {
    throw new Error('the browser offered no file for the approved report');
  }

  return readFile(path);
}
