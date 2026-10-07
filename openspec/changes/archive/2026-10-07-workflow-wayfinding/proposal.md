# Proposal: Workflow wayfinding over delivered routes (workflow-wayfinding)

## Why
The last six UI packages proved every contract behind a picker, but no
surface guides the work itself. An owner standing on a project must already
know where everything lives: the MVP loop (connect repo → milestone →
evidence → screenshot → draft → approve → export, requirement.md §1/§9) is
traversable only by owners who memorized the six areas; `POST
/api/projects/{id}/milestones/import` has a typed client
(`features/projects-and-milestones/api.ts:importCandidates`) with zero
`.tsx` callers, so a GitHub candidate can never become a milestone without
`curl`; the capture form's own hint promises "up to {maximumPages} URLs"
while offering one input and the wire body accepts exactly one `url`; and
Team/Tenancy are reachable only through dashboard crumbs with no statement
of whether that is accidental or decided. Status: specified handoff (this
file); implementation not started. Stage: Web-only follow-up over delivered
routes plus one genuinely missing wire piece; depends only on archived
packages.

## What Changes
1. Explicit next-step links in workflow order over real area routes only
   (`areaPath`, no invented addresses): Overview → Evidence → Evolution →
   Content → Publish → Overview, Automations linked from the Evidence
   candidate panel that already names it, plus per-row Evidence ⇄ Evolution
   cross-links (request screenshots / select into evidence). The variant
   row's in-place publish (`publish-from-variant`) is coordinated with, not
   duplicated: Content gains an area-level "Continue in Publish" step, no
   second publish control.
2. A real UI trigger for the delivered import route: each GitHub candidate
   row gains "Record as milestone", calling `importCandidates` with source
   `github` and a documented kind→type/version/occurredAt/notes mapping.
   The import stores a candidate-status record; confirmation stays in
   Overview — the candidate panel's deliberate "no confirm verb" boundary
   holds, and its doc comment is updated to say so rather than left
   contradicting the new button.
3. Nothing for the sync-schedule form: `PROJECT-AREA-IA-002` plus
   `ScheduleSection` already ship create/edit over the delivered PUT. The
   new suite regression-proves it instead of rebuilding it.
4. Multi-URL capture in one submission: an additive `urls` (1..3) wire
   field beside legacy `url` (exactly one required), three URL inputs in
   `ScreenshotCapturePanel` keeping `capture-url` as the first, and backend
   facts proving the bound. The hint already promises three; the wire is
   what under-delivers.
5. Nothing for Team/Tenancy discoverability except the explicit record:
   Settings lists session internals plus the credentials link ONLY per the
   owner decision recorded in `SettingsPage.tsx` and `ProjectArea.tsx`
   (2026-10-07), and the dashboard crumbs link Team/Tenancy. The new suite
   pins both halves so a later edit cannot silently alter either.
6. One Playwright suite, `tests/workflow-wayfinding.spec.ts`, wired as a
   new database-isolated block in `scripts/e2e.sh`: every new link lands on
   a real area route, every new control answers the server, armed counters
   prove nothing fires before its explicit control.

## Package Boundary and Split Assessment
Single outcome, one actor (the owner), one lifecycle (follow the loop,
record the proposal, photograph the pages — never send, never synthesize,
never auto-confirm), one acceptance oracle (the new browser suite plus the
capture backend facts). Split signals considered: the five Web slices could
ship without the `urls` wire piece, but then the form would fire up to three
sequential single-URL jobs the panel cannot follow as one — an unusable
intermediate state, so the wire stays in this package with its own
requirement (WORKFLOW-WAYFINDING-004) and tests. The schedule form and the
Team/Tenancy decision are explicitly not built here (recorded, suite-pinned).
No other change's files are touched beyond the named backend wire piece and
its tests.

## Sibling and Shared Architecture Reconnaissance

| Candidate | Evidence path/symbol | Reusable code/config/architecture | Compatibility gap | Owner and release boundary | Decision |
|---|---|---|---|---|---|
| Area routing + sub-nav | `features/project-areas/areas.ts` (`areaPath`), `ProjectSubNav.tsx`, `ProjectDetail.tsx` | Real area routes, one-step sub-nav, `onNavigate` threading | Rows lack `onNavigate`; footers do not exist | Archived `project-area-ia`; same repo/release | **adopt** via import, thread `onNavigate` into rows |
| Publish surfaces | `features/content-asset-variants/VariantPublishSection.tsx`, shared `PublishForm` | In-row publish over delivered routes, verbatim codes | In-row publish already answers variant row → publish | Archived `publish-from-variant`; same repo/release | **adopt** conventions only, add no publish control |
| Candidate panel | `features/github-release-candidates/GitHubCandidatePanel.tsx`, `api.ts`, `types.ts` | Poll/listen controls, honest empty states, kind vocabulary | No import trigger; `importCandidates` uncalled | Archived `github-release-candidates` + `projects-and-milestones`; same repo/release | **adopt** via import, add per-row import button |
| Capture client | `features/screenshot-capture/api.ts`, `types.ts` (`CAPTURE_BOUNDS`), `useCapture.ts` | 202-accepted job following, revision-at-submit, verbatim refusals | Single `url` input + wire field vs bound of 3 | Archived `screenshot-capture`; same repo/release | **extend** form + additive wire field locally |
| Schedule section | `features/project-areas/ProjectAutomations.tsx` (`ScheduleSection`), `automationsApi.ts` | Create/edit over delivered PUT with CAS | Already complete | Archived `project-area-ia` + `scheduled-project-sync`; same repo/release | **adopt** as-is, regression-prove only |
| Platform libs | `dotnet-platform-libs/README.md` | Jobs/storage contracts only | No navigation/milestone/capture workflow | Sibling platform repo; versioned feed | **keep local**, reuse nothing new |

No shared `/lib` added; no sibling edit authorized. Navigation and import
mapping are product-specific workflow glue.

## User Experience and Interface Impact
Actor: the single-user owner, anywhere on a project. Entry: any project
area. Primary flow: follow the "Next" steps Overview → Evidence →
Evolution → Content → Publish → Overview, each landing on the named area's
real route; per-row cross-links move between Evidence and Evolution for the
same project; a GitHub candidate row records a candidate-status milestone
with one press and names where to confirm it (Overview). Alternate flows:
honest empty states unchanged; verbatim refusals (`code: message`);
archived projects show read-only rows with no acting control. No POST fires
on render or navigation: import, capture and schedule-save each leave only
from their own explicit control, and the suite arms request counters to
prove it. Responsive/keyboard: native buttons/inputs, focus order follows
the flow, errors use `role="alert"`. No destructive action exists (imports
are idempotent creates, captures are queued jobs, links navigate).

## BFS Impact Map
- Capabilities: new `WORKFLOW-WAYFINDING-001..006`; consumes
  `project-area-ia` (areas, sub-nav, schedule form), `projects-and-milestones`
  (import route), `github-release-candidates` (candidate rows), `screenshot-capture`
  (capture route + bound), `scheduled-project-sync` (schedule routes),
  `publish-from-variant` (in-row publish, not duplicated). No capability redefined.
- Users/flows: every project area gains next-step links; candidate rows gain
  one button; capture form gains two inputs; nothing else moves.
- Contracts/data/persistence: one additive optional `urls` (1..3) on
  `CaptureRequestBody`; legacy `url` byte-identical; no migration, no
  envelope change, no new error code (422 validation, 413 over bound — both
  delivered).
- Integrations/config: none; no provider, no credential, no socket.
- Callers: `MilestoneRows.tsx` (onNavigate threading + row links),
  `ProjectOverview/Content/Publish/Evidence` (footers), `GitHubCandidatePanel.tsx`
  (import button), `ScreenshotCapturePanel.tsx` + `useCapture.ts` + capture
  `api.ts`/`types.ts` (urls), `CaptureEndpoints.cs` + `CaptureWireContracts.cs`
  (wire), all other callers untouched.
- Failure/boundary: verbatim import refusals; capture `urls` 422/413 naming
  the field; archived gating; idempotent import replay names the stored row.
- Tests: new Playwright suite + e2e.sh block (suite 28, port 5108); .NET
  facts extend existing `ScreenshotCaptureTests` (no new stage, no
  verify.sh edit); full pre-existing browser set must stay green (28 suites).
- Compatibility/privacy/security: same-origin session cookie, per-call
  antiforgery, owner-scoped reads; candidate import carries only the
  server-published row (title, kind, tag, SHA, instants) — no secret, no
  invented prose beyond the documented provenance note.
- Markedly unaffected: Settings/Team/Tenancy surfaces (decision recorded,
  suite-pinned, zero edits); schedule form (delivered, regression only);
  variant publish (delivered, no second control); candidate confirm
  semantics (import proposes, Overview confirms).

## Capabilities
- WORKFLOW-WAYFINDING-001: workflow-order next-step links + Evidence ⇄ Evolution row cross-links over real routes.
- WORKFLOW-WAYFINDING-002: per-candidate "Record as milestone" over the delivered import route (candidate status; confirm stays in Overview).
- WORKFLOW-WAYFINDING-003: delivered schedule create/edit form regression-proven (no rebuild).
- WORKFLOW-WAYFINDING-004: additive multi-URL capture wire (`urls` 1..3) + three-input form in one submission.
- WORKFLOW-WAYFINDING-005: Team/Tenancy owner decision recorded and suite-pinned (no surface change).
- WORKFLOW-WAYFINDING-006: regression suite proving each link lands and each new control answers the server.

## Non-goals
- No new publish, export, reconcile or send control anywhere.
- No candidate confirm verb (import proposes; Overview confirms); no
  candidate edit or removal route (→ named future package `github-candidate-retirement`).
- No milestone delete/unarchive route (dismiss/archive are the by-design
  terminals; history is not erasable).
- No per-project publish history or export history route (→ named future
  packages `publication-history`, `export-history`, already named upstream).
- No Settings listing change for Team/Tenancy (owner decision stands).
- No backend change beyond the additive `urls` wire piece.
