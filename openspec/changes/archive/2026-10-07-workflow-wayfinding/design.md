# Design: Workflow wayfinding over delivered routes (workflow-wayfinding)

## 1. Implementation boundary
Repository Chronicleaf only, on `main`. Web (React + TypeScript + Vite),
one additive backend wire piece (C# net10.0 / ASP.NET Core 10), .NET facts
in an existing stage, one new Playwright suite + e2e.sh block.

Files to change:
- `src/Chronicleaf.Web/src/features/project-areas/WorkflowNext.tsx` (new):
  `WorkflowNext({projectId, current, onNavigate})` rendering the
  workflow-order next step for one area (label + target area route via
  `areaPath`, `data-testid="workflow-next-{current}"` with the link
  `data-testid="workflow-next-link"`).
- `ProjectOverview.tsx`, `ProjectEvidence.tsx`, `ProjectContent.tsx`,
  `ProjectPublish.tsx`: mount `WorkflowNext` (Overview→Evidence,
  Evidence→Evolution, Evolution→Content, Content→Publish, Publish→Overview).
  `ProjectAutomations.tsx`: no footer (its Connection/Webhook sections
  already link to Evidence); the candidate panel's Automations mention
  becomes the link that makes Automations a next-step target.
- `MilestoneRows.tsx`: thread `onNavigate: (to: string) => void` through
  `MilestoneList` → `MilestoneRow`; Evidence rows gain "Request screenshots
  in Evolution" (`workflow-evidence-to-evolution`), Evolution rows gain
  "Select into evidence in Evidence" (`workflow-evolution-to-evidence`).
  `ProjectDetail.tsx` passes its existing `onNavigate`.
- `GitHubCandidatePanel.tsx`: per-row "Record as milestone"
  (`github-candidate-import-{row}` → shared `github-candidate-import`)
  calling `importCandidates(projectId, 'github', [{sourceKey,
  draft}])`; notice names created vs replayed and points at Overview for
  confirmation; hidden when `archived`. Doc comment updated: import
  proposes a candidate-status record; confirmation stays in Overview.
- `ScreenshotCapturePanel.tsx` + `useCapture.ts` + capture `api.ts` +
  `types.ts`: `CaptureDraft { urls: string[] }` (was `url: string`);
  three inputs (`capture-url`, `capture-url-2`, `capture-url-3`); submit
  enabled with ≥1 non-blank; sends `urls` (trimmed non-blank list).
- Backend, only the named wire piece: `CaptureRequestBody` gains optional
  `urls`; `CaptureEndpoints.RequestCaptureAsync` maps `urls` (non-empty) or
  legacy `url` into `ScreenshotCaptureRequest.Urls`; exactly one required;
  both present → 422 naming `urls`; malformed element → 422 naming `urls`.
  Count bound stays service-owned (413). No migration, no envelope change.
- `tests/workflow-wayfinding.spec.ts` + `scripts/e2e.sh` suite 28 (own
  disposable PostgreSQL, port 5108, `seed_workflow_wayfinding`).
- .NET facts in existing `ScreenshotCaptureTests` (existing verify.sh stage).

Must not change: Settings/Team/Tenancy surfaces; schedule form; variant
publish; candidate confirm semantics; any other change's files; the legacy
single-`url` wire path (byte-identical when `urls` is absent/empty).

## 2. Language and runtime
TypeScript/React/Vite for all UI; C# net10.0 for the wire piece (same
nullable-record + `JsonPropertyName` conventions as the file it extends).
PostgreSQL is the single store; no migration. .NET facts extend
`ScreenshotCaptureTests` so `scripts/verify.sh` needs no new stage. Browser
suite runs on its own disposable PostgreSQL + published app at port 5108
behind `scripts/e2e.sh`. Commands: `npm ci`, `tsc --noEmit`, `vite build`
(from `src/Chronicleaf.Web`), `bash scripts/e2e.sh`,
`openspec validate --all --strict --no-interactive`, `git diff --check`.

## 3. Ownership and shared code
**Adopt** `areaPath`/`areaOfSegment` (no invented addresses), the shared
`PublishForm` convention of adding no second publish control, the candidate
panel's hook patterns (explicit-click-only mutations, `role="alert"`
failures), and the capture hook's revision-at-submit + job-following shape.
**Keep local** the footer component (workflow glue, one consumer family) and
the import mapping (candidate-package-specific). No shared `/lib`, no
sibling edit.

## 4. User experience and interface
- Footers: one sentence naming the next step + one button navigating to the
  target area route. Publish footer returns to Overview ("see published work
  as history"). Copy never promises what the target does not do.
- Row cross-links: Evidence rows → "Request screenshots in Evolution —
  captures are candidates until curation selects them"; Evolution rows →
  "Select into evidence in Evidence — curation publishes the choice".
  Buttons, keyboard-native, in the row's action flow.
- Import button: per candidate row, disabled while in flight; success notice
  `Recorded “{title}” as a candidate milestone{, already recorded} — confirm
  it in Overview.` with a link to `/projects/{projectId}`; failure renders
  `code: message` verbatim (incl. `validation_failed` field errors and
  `revision_conflict`); nothing fires on render.
- Capture form: three URL inputs with labels "The pages to photograph (up to
  3)"; hint keeps the bound sentence (now true); field errors render
  `messageFor('urls')` beside the inputs; existing `capture-url` testid is
  the first input so prior suites hold.
- Archived projects: footers render (navigation is not a mutation); row
  cross-links render; import button and capture inputs stay hidden (existing
  gating).
- Candidate-panel Automations mention becomes "…instead of waiting to be
  asked for — see Automations" linking `/projects/{id}/automations`.

## 5. Behavioral model
| Input | Rule | Outcome |
|---|---|---|
| Footer/row/crumb link press | `onNavigate(areaPath(projectId, target))` | Real area route renders; no request fires |
| Import press | `importCandidates(projectId, 'github', [{sourceKey, draft}])` | 201 (created) / 200 (replay names stored row); record is `candidate` status |
| Import mapping | kind release/tag→`release`, pull-request→`feature`, issue→`bugfix`, star→`stars`; version=`tagName ?? ''`; occurredAt=`receivedAt`; notes=`Proposed from GitHub {kindLabel} "{title}" (source key {sourceKey}, received {receivedAt}{, commit {revision}}). Imported as {type}; edit before confirming.` | Deterministic, documented, owner-editable |
| Capture submit | 1..3 non-blank URLs → `urls`; 0 → submit disabled | 202 + followed job; identical replay follows same job |
| Wire `urls` | absent/empty → legacy `url` path untouched | Byte-identical legacy behavior |
| Wire `urls` + `url` both naming | — | 422 `validation_failed` naming `urls` |
| Wire `urls` element malformed | — | 422 naming `urls` with the index |
| Wire `urls` length > 3 | service-owned | 413, no job (delivered `PayloadTooLargeException`) |
| Archived project | — | No import control, no capture form (existing); links still navigate |

## 6. Contract and compatibility
- `CaptureRequestBody` gains `[JsonPropertyName("urls")]
  IReadOnlyList<string>? Urls`. Absent/empty/null-element handling: exactly
  one of `url`/`urls` must name ≥1 URL; `url`+`urls` conflict is 422;
  per-element parse reuses `ParseUrl` semantics with the field named
  `urls`. No new error code; no serializer change; old clients sending only
  `url` see byte-identical behavior (proven by untouched existing facts).
- Import uses the delivered `importCandidates` signature unchanged
  (`source: 'github'`, well-formed non-`manual` per `MilestonePolicy`).
- No migration; no route added or removed; no envelope change.

## 7. Failure and boundary policy
- Import refusal: verbatim `code: message`; archived → control absent (the
  server would refuse; the UI does not offer what it knows is refused).
- Capture refusal: verbatim; `urls` field errors beside the inputs;
  revision conflict names the moved evidence (existing copy).
- Link targets: only `areaPath` outputs; unknown segments keep the delivered
  "Nothing here" view (no new 404 surface).
- Idempotency: import replay returns the stored row (`created: false`) with
  no duplicate (delivered partial unique index); capture replay follows the
  same job (delivered key).

## 8. Verification oracle
- .NET (existing stage, asserted to select tests): 4+ new facts in
  `ScreenshotCaptureTests` — `urls` with 2 URLs accepted 202 and runs one
  job photographing both (provider answers twice; two candidates with
  distinct `sourceUrl`); 4 URLs → 413 with no job; `url`+`urls` → 422 naming
  `urls`; malformed element → 422 naming `urls`; legacy single-`url` fact
  suite untouched and green.
- Browser suite 28 (`tests/workflow-wayfinding.spec.ts`, port 5108,
  `seed_workflow_wayfinding`: project + confirmed milestone + evidence +
  two seeded candidate rows + review-story approved? No — keep the seed
  minimal: project, confirmed milestone with manual evidence, two seeded
  GitHub candidate rows via `seed_candidate_rows`-style insert, connection
  row optional? Import needs no connection (import route needs only the
  project). Candidate rows need a connection? The candidate list route
  refuses `github_candidate_connection_missing` without one — check: list
  refuses when no connection. So seed must connect a repository → needs
  api.github.com (like suite 12). Alternative: seed candidates + connection
  rows directly in DB. The candidates seed connects via real route (needs
  network). Hmm — suite 12 already requires outbound network. For suite 28,
  seed connection + candidate rows directly in PostgreSQL (same technique as
  candidate rows), avoiding the network prerequisite; the panel and routes
  are what's under test, and suite 12 covers the real-transport connect.
  Cases: (a) footer/row links land on real areas (assert area testids after
  each click, incl. Evidence→Evolution, Evolution→Evidence,
  Overview→…→Publish→Overview, candidate-panel→Automations,
  dashboard→Team/Tenancy, Settings absence of team/tenancy links); (b)
  candidate import → 201 + row in Overview milestone list as candidate +
  replay → 200 names stored row; (c) schedule save → server's cadence shown
  (`automations-schedule-interval` reads back the saved minutes); refusal
  path (e.g. 14 minutes → 422 verbatim); (d) multi-URL submit → 202 accepted
  notice (job then fails `capture_sandbox_unavailable` on this host —
  assert the panel names the code, proving the request reached the server);
  armed counters prove no import/capture/schedule POST before its control.
- Full `bash scripts/e2e.sh` green (28 suites); `npm ci`, `tsc --noEmit`,
  `vite build` green; `git diff --check` clean; name preflight exit 0;
  `openspec validate --all --strict --no-interactive` green before and
  after archive (no `--skip-specs`).

## 9. Decision ledger and CRUD matrix
- Mapping kind→type is this package's choice (release/tag→release,
  pull-request→feature, issue→bugfix, star→stars; notes carry the kind so
  nothing is lost; owner edits before confirming). Alternative rejected:
  importing everything as `custom` — less useful, and the notes already
  preserve the true kind.
- Wire `urls` additive (not replacing `url`) so old callers and the
  existing fact suite hold byte-identical.
- Team/Tenancy: owner decision (Settings internals + credentials ONLY,
  `SettingsPage.tsx` + `ProjectArea.tsx` 2026-10-07) stands; dashboard
  crumbs are the discoverability path; both suite-pinned.
- Schedule form: delivered (`PROJECT-AREA-IA-002`, `ScheduleSection`);
  not rebuilt.

Per-entity CRUD circulation (API route → Web UI trigger):
| Entity | Create | Read | Update | Delete |
|---|---|---|---|---|
| Milestone (incl. import-created) | POST manual (Overview `milestone-new` editor) / POST import 201, idempotent replay 200 (candidate-row button) | GET list + GET one (all areas' rows) | PATCH edit, POST confirm/dismiss (row actions) | No erase route — dismiss/archive are the by-design terminals (history not erasable); no future package |
| GitHub candidate row | Poll POST / webhook delivery (candidate panel poll/listen; server-side) | GET candidate list (panel) | Rows immutable — none by design | No removal route → named future package `github-candidate-retirement` |
| Sync schedule | PUT If-Match 0 (Automations form `automations-schedule-save`) | GET schedule (form state) | PUT CAS (same form) | No erase route — disable via `enabled:false`; archive/disconnect disables server-side; by design, no future package |
| Screenshot capture request | POST captures 202 (capture form; 1..3 `urls`) | GET screenshots + operation read (panel + job follow) | N/A — re-request is a new Create (jobs immutable) | N/A by design (candidates immutable; selection owned by curation); no future package |
| Nav/wayfinding state | N/A — links are stateless navigation | N/A | N/A | N/A — no persistence, no API; evidence: `areas.ts` holds routes only |

Out-of-scope holes, named not silent: `github-candidate-retirement`
(candidate rows immutable, no removal route); upstream
`publication-history`, `export-history`, `content-asset-retirement`,
`agency-client-recovery`, `agency-report-history`,
`publisher-options-binding` (all already named by prior packages; untouched
here).
