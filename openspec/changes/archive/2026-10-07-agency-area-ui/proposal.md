# Proposal: Agency area Web UI over delivered client and weekly-report routes

## Why
requirement §8 Agency requires Client A/Project 1, Project 2; Client B/Project 3, with the weekly
report covering completed/screenshots/resolved/in-progress/next-week, and explicitly implies no
client portal, invoicing or email sending. Both agency backends (`agency-client-projects`,
`agency-weekly-reports`) were archived as "No Web UI… API-observable", so the agency capability
has no usable interface: this is the first agency UI in the product, and it closes that gap
without touching either backend.

## What Changes
A new React `agency-area` feature behind a top-nav `Clients` entry: client list/create/edit/
archive over `POST/GET/PATCH/DELETE /api/clients*` with revision guards; project assign/unassign
over `PATCH /api/projects/{id}/client`; and the per-client weekly-report flow (week-range picker,
in-client project multi-select, deterministic generate over delivered routes, per-project source
preview, revision/body/attribution-bound approval, Markdown/HTML export download) over
`POST/GET /api/clients/{id}/weekly-reports*`. Plus one new Playwright suite and the 26th
database-isolated e2e group. No backend route, contract, migration or envelope change.

## Package Boundary and Split Assessment
Single outcome: the delivered agency backends become operable through the Web UI. Included:
client CRUD UI, assignment UI, weekly-report generate/preview/approve/export UI, nav entry, one
Playwright suite plus its e2e.sh group and seed. Excluded: any backend change (each backend owns
its contracts; none is missing or defective for this UI), client unarchive, report listing/
deletion, approval revocation (no routes exist — named future packages, never silent), and any
portal, invoicing or email surface (requirement §8 implies none). Split signals considered: the
client half and the report half share one area, one nav entry, one suite and one acceptance
journey (CRUD → assign → generate → approve → export), so separating them would create an
unusable intermediate state. Dependencies: `agency-client-projects`, `agency-weekly-reports`
(both archived); `product-shell-ux` conventions consumed, not redefined.

## Sibling and Shared Architecture Reconnaissance
Keep local, reusing delivered UI patterns. No sibling `common`/`manager` project exists in this
repository (single-product layout; `packages/platform-feed/` carries only Platform.Jobs/Storage
nupkgs, no agency workflow).

| Candidate | Evidence path/symbol | Reusable code/config/architecture | Compatibility gap | Owner and release boundary | Decision |
|---|---|---|---|---|---|
| content-pack panels | `src/Chronicleaf.Web/src/features/content-pack-panels/api.ts` (antiforgery-per-mutation, `ApiFailure` envelope read, byte-download via `Response.blob()`) | Adopt the call pattern verbatim | Pack routes differ; agency routes substituted | This change, same Web client | Adopt pattern |
| static-blog-export-ui | `tests/static-blog-export-ui.spec.ts` (byte-equal offered-vs-direct download, job-watch wording) | Adopt byte-equality assertion shape | No job here; generate is synchronous 201 | This change, same suite style | Adopt pattern |
| markdown-review-export panel | `readReview` approval binding display | Adopt revision-bound approval UX | Weekly approval binds revision+body+attribution+sources, not voice | This change | Adapt |
| Platform feed | `packages/platform-feed/` | None for agency UI | No recurring capability | Not applicable | N/A with evidence |

## User Experience and Interface Impact
Actor: the signed-in owner organizing agency work. Entry: new `Clients` top-nav entry (one nav
step from every area, per PRODUCT-SHELL-UX-001) plus the delivered dashboard order preserved.
Flows: `/clients` lists clients (name, notes, archived badge, empty state naming the next step:
create the first client); create/edit forms validate name 1..160 inline and surface `not_found`
404 / `revision_conflict` 409 / `client_archived` 409 in the server's words; `/clients/{id}`
shows member projects with assign (project picker over the delivered project list) and
clear-to-personal, and the weekly-report panel (week-start date input defaulting to the current
week's Monday, IANA time-zone input defaulting to `UTC`, in-client multi-select, in-progress and
next-week note fields, generate → body preview with per-project source refs → approve at the
shown revision → Markdown/HTML downloads). States: loading, empty (honest: no clients, no
projects, no report yet, empty-week body shown verbatim), validation, refusal (verbatim codes),
archived-client (readable, new assignments refused with the control disabled and the reason
shown), and drift (approval refused `approval_stale`: panel names regeneration as the remedy).
Responsive/keyboard/focus: token primitives only, native form controls, focus restoration after
dialogs, `aria-current` on the new nav entry. No portal, invoicing or email control exists
anywhere in the area. Browser evidence: the new Playwright suite.

## BFS Impact Map
- Capabilities/users/flows: new `agency-area` feature; owner CRUD/assign/report journeys; no other
  area's flow changes (Projects list untouched; project `clientId` read is display-only here).
- Contracts/data/persistence: none — every call uses a delivered route with its delivered
  revision guard; no migration, no envelope addition.
- Integrations/config: none — no provider, no credential, no job, no outbound call; e2e.sh gains
  one group (port 5106) following the existing `start_suite`/`run_suite` shape.
- Callers: `App.tsx` NAV_ENTRIES plus one entry; `ProjectArea.tsx` plus two routes; nothing else
  imports the new feature.
- Failure/boundary: verbatim `not_found`, `validation_failed`, `if_match_required`,
  `revision_conflict`, `client_archived`, `weekly_report_project_outside_client`,
  `weekly_report_idempotency_conflict`, `approval_required`, `approval_stale`; archived and
  foreign handling per the backends (foreign = `not_found` 404, never named).
- Tests: new `tests/agency-area-ui.spec.ts`; full `bash scripts/e2e.sh` (26 suites) stays green.
- Privacy/security: session cookie only, antiforgery per mutation, no new secret surface.
- Unaffected (explicit): all backends, all other panels, shell copy, tokens, publish/export
  pipelines, Hosted/team/tenancy behavior.

## Capabilities
### New Capabilities
- `agency-area-ui`: Agency Clients area and weekly-report Web UI over delivered routes.

## Non-goals
Backend changes of any kind; client unarchive; report listing/deletion/history; approval
revocation; client portal, invoicing, email sending or recurring publication; rebrand, token or
nav-order changes; touching any other change's files.
