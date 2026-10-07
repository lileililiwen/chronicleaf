# Proposal: Project area information architecture (project-area-ia)

## Why
The project page is one endless scroll of 20 panels and the automatic
subsystems are invisible. `ProjectDetail` renders every panel for every
visit — connection forms, candidates, narrative, timeline, evolution,
publications, journey, plus nine per-milestone panels inside every
milestone row — with no map and no way to link to one part of it. The
trigger audit behind `feature-trigger-panels` proved the backends exist;
this proves the owner can find them. Worse, the automation the product
already runs (candidate sync, scheduled sync, webhook deliveries,
background jobs, publish jobs) has no surface that names it: an owner who
never opens Operations cannot tell anything automatic ever happened.
Status: Specified handoff; implementation not started. Stage: POST-MVP
(new package; DEMAND satisfied by explicit owner order after the audit —
owner is furious about process overhead, so this authors and implements
in a single pass with no extra rounds).

## What Changes
Split `ProjectDetail` into six routed sub-views behind one project
sub-navigation — Overview (milestones + timeline excerpt), Evidence
(manual + syncs + candidates + curation + connections), Content
(narrative + voice + generation + drafts + variants + review), Publish
(journey + publications), Evolution (images + GIFs + capture) — plus a
NEW Automations view per project (connection status, candidate sync last
run + trigger, scheduled-sync cadence view/edit, webhook status, recent
jobs, publish history). All 20 existing panels move exactly once; none
is deleted, none is duplicated. The top nav stays five entries; Publish
and Automations become reachable within two steps from anywhere through
per-project Publish/Automations links on the dashboard and project list.
Empty states name the automation payoff (candidate sync + scheduled
sync), not just the form. Frontend only: no migration, no route, no
envelope, no binding, no evidence rule. Product-shell-ux tokens and
conventions kept; no rebrand. Screenshot acceptance: DELTA captures per
new view against the 2026-10-07 set, empty/generic states only, no
secrets. See design.md.

## Package Boundary and Split Assessment
Own only the project-area split, sub-navigation, dashboard/list
deep-links, the Automations view, empty-state payoff copy, and the new
browser suite plus in-package migrations of suites asserting moved
selectors. Repository Chronicleaf, React + TypeScript + Vite, no C#
surface. Depends on (both archived): product-shell-ux (shell, nav,
tokens, DELTA discipline) and feature-trigger-panels (the six trigger
panels this IA moves without re-implementing). Different persistence,
approval, publishing, scheduling and Cloud lifecycles are separate
packages in ../../../ROADMAP.md, not hidden sub-features. This unit is
accepted by its scoped browser evidence and outcome, not by completion
of the full product. Excluded surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Project-area IA, sub-navigation and the Automations
composition are Chronicleaf-product presentation over already delivered
routes; no matching recurring implementation is evidenced in sibling
platform contracts. Evidence inspected: dotnet-platform-libs/README.md
lists Platform.Jobs and Platform.Storage families but no Web project
area, sub-navigation or automation-summary surface. Reuse
dependency-owned API contracts exactly as served; never duplicate those
concerns. No shared `/lib` added and no sibling edit authorized.

## BFS Impact Map
- Actor/flow: the signed-in single-user owner (community-access session
  gate stays the entry guard); anonymous sees sign-in only, as today.
- Boundary/callers: `ProjectDetail.tsx` (split into a header + sub-nav +
  six area components under a new `features/project-areas/` folder),
  `ProjectArea.tsx` route map (five new client-side sub-paths under
  `/projects/{id}`; bare `/projects/{id}` renders Overview at the same
  URL, so URL assertions survive), `Dashboard.tsx` + projects list
  (per-project Publish/Automations deep-links), `GitHubCandidatePanel`
  no-connection copy (payoff wording), `scripts/e2e.sh` (new
  `project-area-ia.spec.ts` appended to suite 1), every suite asserting
  a moved selector (migrated in-package, named in the spec scenarios —
  inventoried in design §BFS; no suite deleted, none silently weakened).
- Data/integration: no schema, no API, no envelope change; the
  Automations view calls only delivered routes (connection read,
  candidates sync POST, sync-schedule GET/PUT, webhook subscription
  read, operations list) through the existing per-feature `api.ts`
  convention plus one new `automationsApi.ts` client for the delivered
  sync-schedule routes the Web never called before. The one read with
  no delivered route — per-project publish history — is reported in
  design and shown honestly (scope's recent publish jobs, captioned as
  such), never invented.
- Verification: full pre-existing Playwright set green (migrated
  in-package where named); new project-area-ia suite green; DELTA
  captures per new view vs the 2026-10-07 set; `npm ci`, `tsc` + `vite
  build`; full `bash scripts/verify.sh` with `GATE_EXIT` explicit.
- Privacy/compatibility: captures and fixtures show empty/generic
  states only — no credential, token, PAT, or signed-in session value
  in any committed file, fixture, or capture.

## Capabilities
### New Capabilities
- `project-area-ia`: Routed project sub-views with sub-navigation and a
  visible per-project Automations surface over delivered routes.

## Non-goals
Backend changes of any kind (migrations, routes, envelope, approval
bindings, evidence rules); auto-posting or auto-publishing anywhere; a
taste rebrand (product-shell-ux tokens and conventions are kept as-is);
new top-level nav entries (reachability is via deep-links, exact IA in
design); secret-bearing captures; touching HANDOFF.md, ROADMAP.md, or
.gitignore (coordinator owns them). No implementation, archive, commit,
push or deployment in this authoring work.
