# Design: Project area information architecture (project-area-ia)

## Context and implementation boundary
Repository Chronicleaf only. Primary boundary
`src/Chronicleaf.Web/src/features/projects-and-milestones/ProjectDetail.tsx`
(split, not restyled), `ProjectArea.tsx` (five new client-side sub-paths),
a new `src/Chronicleaf.Web/src/features/project-areas/` folder (sub-nav,
six area components, one sync-schedule client), `Dashboard.tsx` plus the
projects list in `ProjectArea.tsx` (deep-links only), the
`GitHubCandidatePanel` no-connection copy (payoff wording only), the
Playwright suites asserting moved selectors (migrated in-package, named
below), and `scripts/e2e.sh` (new suite appended to suite 1). Inspect the
explicit symbols below; create only modules needed for this outcome.
Depends on product-shell-ux + feature-trigger-panels (both archived) and
changes no backend: every API contract rendered here is delivered and
stays byte-identical. Preserve forge.yaml, .standard receipts, unrelated
files, HANDOFF.md, ROADMAP.md, .gitignore and current_spec
(`project-area-ia`). The exact top-nav/deep-link IA is decided here, not
deferred: five-entry top nav unchanged; Publish and Automations reached
within two steps from anywhere via per-project deep-links (see below).

## Language, ownership and release
React + TypeScript + Vite for `src/Chronicleaf.Web`; no C#, no
migration, no API edit. Area components consume product-shell-ux tokens
and conventions; no new styling primitives, no ad-hoc colors or spacing.
Native dependencies pinned in lockfiles during implementation; no
package install in this authoring task.

**Keep local.** Project-area IA and the Automations composition are
Chronicleaf-product presentation; no matching recurring implementation
is evidenced in sibling platform contracts. Evidence inspected:
dotnet-platform-libs/README.md lists Platform.Jobs and Platform.Storage
families but no Web project area or automation-summary surface. No
shared `/lib` added and no sibling edit authorized.

## Contract and compatibility decisions
Consume the delivered API contracts exactly as served (owner session,
projects/milestones, connections, candidates, sync-schedule, webhook
subscription, operations, publications): same paths, payloads, envelope
`{code,message,traceId,fieldErrors?}`, antiforgery/session handling,
and revision/If-Match discipline. No new API route, no envelope change,
no approval-binding change, no evidence-rule change. New client-side
sub-paths only (all under an existing project id; the project id and
everything after it is routing, not API):
- `/projects/{id}` — Overview (renders at the bare URL, so existing
  URL assertions survive the split).
- `/projects/{id}/evidence`, `/content`, `/publish`, `/evolution`,
  `/automations` — the other five areas.
- Edit/milestone sub-paths (`/edit`, `/milestones/new`,
  `/milestones/{mid}`) keep matching first; an unknown area segment
  renders the delivered "Nothing here" view, now with the project
  sub-nav intact so the owner is one click from a real area.
The project header (name, description, URL, edit/record/archive
actions, archive notice, action notice/error) renders identically above
the sub-nav on every area, so header-level suites keep passing
wherever they navigate. The sub-nav (`project-subnav`, six buttons
`project-area-{overview,evidence,content,publish,evolution,automations}`
with `aria-current="page"` on the active one) renders on every area
including the unknown-area view, so every area is one step from every
other area once the owner is on a project.

Top-nav reachability (the recorded IA decision): the five-entry top
nav does not change — a per-project area cannot be a global entry. Two
steps from anywhere means: (1) the Projects nav entry from any page,
(2) the per-project `Publish` / `Automations` deep-link
(`project-publish-link`, `project-automations-link`) beside every
project on the dashboard and the projects list, landing directly on
`/projects/{id}/publish` and `/projects/{id}/automations`. The
project sub-nav is the second half of the same decision: once on any
project area, every other area (including Publish and Automations) is
one further step.

The 20-panel move map (exactly once each; the count is the oracle):
- Overview — the milestone list with its status filter and
  confirm/dismiss actions (1) + `ProjectTimeline` excerpt (1).
- Evidence — `GitHubConnectionPanel`, `LocalRepositoryPanel`,
  `GitHubCandidatePanel` (3) + per-milestone `MilestoneEvidencePanel`,
  `GitHubSyncPanel`, `LocalGitSyncPanel`, `ScreenshotPicker` (4).
- Content — `NarrativeMemoryPanel`, `VoiceArea` (2) + per-milestone
  `LlmProviderArea`, `GroundedMarkdownDraftPanel`, `MarkdownReviewPanel`,
  `ContentVariantPanel` (4).
- Publish — `PublicationPanel`, `JourneyPanel` (2, no milestone rows:
  publishing and journey projection are project-level decisions).
- Evolution — `EvolutionImagePanel`, `EvolutionGifPanel` (2) +
  per-milestone `ScreenshotCapturePanel` (1).
- Automations — NEW components only; zero moved panels.
Total: 2 + 7 + 6 + 2 + 3 = 20. Milestone rows render in Overview
(chrome + filter + actions, no detail panels), Evidence, Content and
Evolution (chrome + that area's detail panels only). Row chrome
repeating across areas is navigation context, not a panel: panels are
the 20 named components above, and each renders in exactly one area —
asserted by the new suite (each moved `data-testid` present on its
area, absent on the other five).

## BFS impact map (every suite asserting a moved selector)
`ProjectDetail`'s panels are asserted by: projects-and-milestones
(header, filters, list, milestone chrome — stays on the bare Overview
URL, no migration expected beyond sub-nav awareness),
milestone-timeline (timeline excerpt — Overview, no migration),
milestone-evidence + github-evidence + github-release-candidates +
screenshot-curation-picker (→ Evidence), grounded-markdown-drafts +
markdown-review-export + content-asset-variants + voice-profiles +
llm-provider-contract (→ Content), screenshot-capture +
screenshot-curation-capture (→ Evolution), evolution-images +
evolution-gifs (→ Evolution), publications + journey-publish (→
Publish). Migration is mechanical and named per scenario: after the
suite lands on the project page it navigates to the area (sub-nav
click or direct sub-path goto) before asserting the same selectors
with the same expectations — no assertion weakened, no suite deleted.
Suites asserting only the header, the milestone chrome, the dashboard,
operations, credentials, settings, team, tenancy, shell, or
product-shell-ux surfaces are untouched. `scripts/e2e.sh` suite 1
gains `tests/project-area-ia.spec.ts` (appended last; creates its own
project by name and never asserts global emptiness).

## Behavioral model and invariants
- One project, six addresses. The sub-nav and the area agree because
  both read the same route the shell owns; no area fetches what
  another area owns, and no panel imports another area's panel.
- Panels never invent state: the Automations view reads only delivered
  routes and renders the server's answers — connection (or the honest
  `github_connection_missing` empty state), last candidate-sync
  operation for this project (or "never ran"), schedule (or the
  schedule 404 empty state with the create form), subscription (or the
  not-listening empty state), this project's recent operations by
  `targetId`, and the scope's recent `publication.publish` operations
  captioned as scope-wide. A needed read with no delivered route is
  not invented: there is NO per-project publish-history route, so the
  section says so and shows what the server does serve. Triggers are
  explicit owner acts only (Sync-now button, schedule create/edit
  form); nothing auto-fires on render or navigation, and failures
  surface the server code verbatim.
- Empty states advertise the payoff: the Automations no-connection
  state and the candidate panel no-connection state name candidate
  sync + scheduled sync as what connecting unlocks (copy change only;
  the candidate suite's exact-text assertions are migrated, not
  deleted).
- Scaffolding purge holds: no new production string names a change,
  spec, or "not in this change" state (the lint stage keeps guarding).

## Failure, boundary and rollback policy
A renamed testid breaks its suite loudly (suites migrated in-package,
never silenced); an area that cannot render its panels fails the
package rather than shipping a thinner area. The Automations view
shows the server's own refusal for any route the installation answers
differently (Community vs Hosted, unconfigured schedule, unlistening
webhook) and invents no fallback. Unknown area keeps the delivered
"Nothing here" view with sub-nav intact. Captures contain
empty/generic states only: any capture showing a credential, token,
PAT, Actor UUID, or signed-in session value is rejected and retaken.
Rollback is frontend-only: revert the Web tree, the route map and the
e2e.sh line; no data, migration, or API surface is touched. If
implementation proves a backend change unavoidable, it is named as a
separate future package and this one stays frontend-only.

## Verification oracle
New `tests/project-area-ia.spec.ts` (suite 1, real browser, real
published app, real PostgreSQL): sub-nav reaches all six areas with
`aria-current`; each moved testid present on exactly its area;
Automations sections read (connection status, last sync, schedule
404-then-create, webhook null, project jobs, scope publish note
naming the missing per-project route); candidate-sync trigger answers
202 with a watched operation; schedule PUT validation surfaces the
server code; two-step reach (Projects nav → card Automations link);
payoff empty states; DELTA captures per new view
(`area-overview/evidence/content/publish/evolution/automations`) in
empty states vs the 2026-10-07 set with diffs justified. Full
pre-existing Playwright set green (migrated in-package where named
above). `npm ci`, `tsc` + `vite build`, full `bash scripts/verify.sh`
with `set -o pipefail` + explicit GATE_EXIT in FOREGROUND, every
filtered stage ≥1 test, tsbuildinfo removed. Name preflight exit 0;
`openspec validate --all --strict --no-interactive` all-green before
and after the archive, which uses no `--skip-specs`.

## Decision ledger and activation gates
Phase: POST-MVP. DEMAND satisfied by explicit owner order after the
audit — no further activation needed. IA decisions recorded above
(five-entry nav kept; deep-links, not new entries; bare URL renders
Overview; publish history shown scope-wide with the gap named). No
unresolved product-policy decision remains; taste questions are out of
scope (tokens kept as-is). Missing browsers/DB at implementation time
is recorded prerequisite unavailability, never fabricated success.

## Requirement traceability
| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| PROJECT-AREA-IA-001 | Routed sub-views + sub-nav, 20 panels moved once | Move map + sub-path decisions above | ProjectDetail split + ProjectArea map + sub-nav | 001 Success/Failure/Boundary | 1.1, 2.1, 3.1, 4.1 | New suite: six areas addressable, each testid on exactly one area |
| PROJECT-AREA-IA-002 | NEW Automations view, read+trigger over delivered routes | Automations section decisions above | project-areas/ automation components + clients | 002 Success/Failure/Boundary | 1.1, 2.2, 3.1, 4.1 | New suite: six sections read, sync trigger 202, schedule create/edit, missing publish-history route named |
| PROJECT-AREA-IA-003 | Publish/Automations ≤2 steps from anywhere | Deep-link IA decision above | Dashboard + projects list links | 003 Success/Failure/Boundary | 1.2, 2.3, 3.1, 4.1 | New suite: nav→card-link→area; sub-nav area-to-area in one step |
| PROJECT-AREA-IA-004 | Payoff empty states | Payoff copy decisions above | Automations + candidate panel copy | 004 Success/Failure/Boundary | 1.2, 2.4, 3.1, 4.1 | New + candidate suites: no-connection names candidate sync + scheduled sync |
| PROJECT-AREA-IA-005 | No backend; tokens kept; DELTA acceptance | Boundary + oracle decisions above | Web tree only + DELTA captures | 005 Success/Failure/Boundary | 1.2, 2.5, 3.1, 4.1 | verify.sh green, tsc+build, per-view DELTAs justified, no secret capture |
