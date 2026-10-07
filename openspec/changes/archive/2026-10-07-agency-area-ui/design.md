# Design: Agency area Web UI over delivered client and weekly-report routes

## Context and implementation boundary
Repository Chronicleaf only. Primary boundary: new
`src/Chronicleaf.Web/src/features/agency-area/` (no other feature directory touched), plus a
one-entry addition to `NAV_ENTRIES` in `src/Chronicleaf.Web/src/App.tsx` and two routes
(`/clients`, `/clients/{id}`) in
`src/Chronicleaf.Web/src/features/projects-and-milestones/ProjectArea.tsx`. New test file
`src/Chronicleaf.Web/tests/agency-area-ui.spec.ts` and one appended e2e.sh group (port 5106)
with `seed_agency_area`. Inspect: `AgencyClientEndpoints.cs`, `AgencyClientWireContracts.cs`,
`WeeklyReportEndpoints.cs`, `WeeklyReportService.cs` (Generate/Approve/Export semantics),
`content-pack-panels/api.ts` (call pattern), `content-pack-panels.spec.ts`/`static-blog-export-ui.spec.ts`
(suite shape), `App.tsx`/`ProjectArea.tsx` (nav/route map). Dependencies
`agency-client-projects` and `agency-weekly-reports` are archived; consume their routes verbatim.
No backend file is changed; no migration; no envelope arm. Preserve unrelated files and
`current_spec`.

## Language, ownership and release
TypeScript + React + Vite in `src/Chronicleaf.Web` (Node 24, `npm ci`, `tsc --noEmit`,
`vite build`); Playwright for `tests/agency-area-ui.spec.ts` run through `bash scripts/e2e.sh`.
No C# compiles for this change (no backend edit), but the full `scripts/verify.sh` gate still
runs to prove no regression. **Keep local** (see proposal reconnaissance): the agency area is
product-specific UI; the only reuse is the delivered call/UX patterns named above. No shared
`/lib` added, no sibling edit authorized.

## Contract and compatibility decisions
Every call below names a delivered route and its delivered guard; field names are the wire
contracts' pinned names.

| UI action | Route | Guard / body |
|---|---|---|
| List clients | `GET /api/clients` | pagination passthrough; ordered by name then id |
| Create client | `POST /api/clients` `{name, notes}` | 201; name 1..160, notes ≤20k (server 422 otherwise) |
| Read client | `GET /api/clients/{id}` | `ETag` carries revision; foreign/archived-readable |
| Edit client | `PATCH /api/clients/{id}` `{name?, notes?}` | `If-Match` = read revision (428 missing, 409 stale) |
| Archive client | `DELETE /api/clients/{id}` | `If-Match` = read revision; archived stays readable |
| Member projects | `GET /api/clients/{id}/projects` | — |
| Report context | `GET /api/clients/{id}/context?projectIds=` | absent ids (unassigned/foreign) silently absent, never probed |
| Assign/unassign | `PATCH /api/projects/{id}/client` `{clientId\|null}` | key must be present (explicit `null` clears); `If-Match` = project revision (header wins, body `expectedRevision` fallback); archived target 409 `client_archived`; duplicate at stored revision = no-op |
| Generate report | `POST /api/clients/{id}/weekly-reports` `{projectIds, weekStart, timeZone, inProgressNotes?, nextWeekNotes?, idempotencyKey?}` | `weekStart` YYYY-MM-DD (2000..2100), `timeZone` IANA id (both 422 otherwise); outside-client project 422 `weekly_report_project_outside_client`; regroup mid-write 409 `revision_conflict`; clashing key 409 `weekly_report_idempotency_conflict`; regeneration = new row, revision = latest+1 for (client, week) |
| Read report | `GET /api/clients/{id}/weekly-reports/{reportId}` | carries `revision`, `attributionHash`, `sourceRefs`, `bodyMarkdown`, `bodyHash`, `approved`, `windowStartUtc/EndUtc` |
| Approve | `POST .../weekly-reports/{reportId}/approve` | `If-Match` = report revision; already-approved same state = idempotent read-back; prior stamp on moved state 409 `approval_stale`; moved attribution/evidence 409 `approval_stale` |
| Export | `GET .../weekly-reports/{reportId}/export?format=markdown\|html&revision=` | unapproved 409 `approval_required`; wanted ≠ approved/head, moved body/attribution/evidence 409 `approval_stale`; missing PNG bytes 409 `media_unavailable`; else attachment bytes |

Week defaults: date input defaults to the current week's Monday (local), time-zone input defaults
to `UTC`; both are plain inputs the owner can change (DST week e.g. `2026-03-08` +
`America/New_York` renders the backend's exact seven-local-dates window). The UI never invents
progress: an empty week renders the backend's `EmptyWeekLabel` sentence verbatim from
`bodyMarkdown`. No portal/invoicing/email parameter exists on any call and none is added.

## Behavioral model and invariants
- Client list → detail is the only depth; `/clients/{id}` shows members, the assign picker (all
  owned projects with their current client named; archived-client detail disables assignment with
  the `client_archived` reason shown), and the report panel.
- Report panel state machine per generation: `idle → generating → preview(revision N, unapproved)
  → approving → approved → exported`; `regenerate` returns to `generating` and the new row
  starts unapproved (revision N+1, new id kept in panel state — there is no list route, so the
  panel keeps the latest generated id; named limitation, future `agency-report-history`).
- Approval binds (revision, bodyHash, attributionHash, sourceRefsJson); any drift refuses the
  approve/export with `approval_stale` and the panel names regeneration as the remedy.
- Archived clients stay readable with history; foreign ids answer `not_found` and the UI shows a
  "not found" view without naming anything foreign.
- Mutations carry a fresh antiforgery value per call and `credentials: same-origin`, exactly as
  the delivered features do; refusals render `{code, message}` verbatim, never paraphrased.

## Failure, boundary and rollback policy
Empty: no clients / no projects / no report yet / empty-week body — each names the owner's next
step (PRODUCT-SHELL-UX-003: no scaffolding text). Duplicate association at the stored revision is
a no-op success. Stale `If-Match` re-reads and lets the owner decide again. Missing `If-Match`
is 428 with the control re-enabled. Over-long/blank names are inline validation plus the
server's 422. Rollback: remove the nav entry, the two routes and the feature directory; stored
clients/reports/approvals are untouched backend rows. No partial-IO state exists client-side.

## Verification oracle
New suite `tests/agency-area-ui.spec.ts` against its own database (port 5106) seeded through
delivered routes only (projects, confirmed milestones with manual evidence, clients, assignment;
report rows come only from the UI's own generate calls — never inserted): client CRUD →
assign two projects → generate (DST week) → per-project preview → approve → Markdown + HTML
downloads byte-equal to the delivered export route fetched directly; refusal codes verbatim
(`client_archived` on assigning to the archived seed client, `weekly_report_project_outside_client`
on a cross-client project id, `approval_required` on export-before-approve,
`revision_conflict` on a stale edit, `not_found` on a foreign id); archived-client readable
with assignment disabled; honest empty states; an armed POST counter proving no mutation before
its control and no request ever leaving loopback. Commands: `npm ci`, `tsc --noEmit`,
`vite build`, the new suite, then full `bash scripts/e2e.sh` (26 suites) green,
`git diff --check`, name preflight, `openspec validate --all --strict --no-interactive`.

## Decision ledger and activation gates
Phase: DEMAND-GATED (agency). No backend change: every route needed exists and is correct for
this UI; any backend defect found during implementation gets its own spec requirement + tests or
a named deferral — none is assumed so far. Out-of-scope holes, named not silent:
`agency-client-recovery` (unarchive; no route), `agency-report-history` (per-client/week report
listing, report deletion, approval revocation; no routes). No portal/invoicing/email decision
exists to make: requirement §8 implies none and none is built.

## Requirement traceability
AGENCY-AREA-UI-001 → proposal Clients capability → design contract table rows 1–8 + behavioral
model → tasks 2.1 → suite flow client-CRUD/assign + refusal cases. AGENCY-AREA-UI-002 → proposal
weekly-report capability → design contract table rows 9–12 + panel state machine → tasks 2.2 →
suite flow generate/preview/approve/export + DST + drift cases. AGENCY-AREA-UI-003 → proposal
nav/entry → design boundary (App.tsx/ProjectArea.tsx) → tasks 2.3 → suite nav/reachability +
pre-existing shell suites unmodified.
