# Tasks: workflow-wayfinding

## 1. BFS — Baseline and impact coverage
- [x] Map requirements to layers/callers/tests (design §1/§5/§9; footers + row links + import button + `urls` wire + suite-pinned decisions + e2e block).
- [x] Confirm the implementation-handoff gate: exact files, commands, oracles (design §1/§2/§8).
- [x] Verify all consumed routes exist with exact paths/shapes (import POST, candidate list GET, capture POST/GET, schedule GET/PUT, area routes via `areaPath`).
- [x] Record CRUD matrix (design §9); name `github-candidate-retirement` as the explicit out-of-scope hole plus upstream named packages.
- [x] Add test skeleton: `tests/workflow-wayfinding.spec.ts` outline + e2e.sh suite-28 block plan (port 5108, `seed_workflow_wayfinding`).

## 2. DFS — Requirement-by-requirement implementation
- [x] WORKFLOW-WAYFINDING-001: `WorkflowNext.tsx` + mounts in Overview/Evidence/Content/Publish (+Publish→Overview); candidate-panel Automations link; `onNavigate` threading through `MilestoneList`/`MilestoneRow`/`ProjectDetail`; Evidence ⇄ Evolution row cross-links.
- [x] WORKFLOW-WAYFINDING-002: per-row "Record as milestone" in `GitHubCandidatePanel` over delivered `importCandidates` (source `github`, documented mapping, created/replay notice + Overview link, archived gating, doc-comment update).
- [x] WORKFLOW-WAYFINDING-004: additive `urls` on `CaptureRequestBody` + endpoint mapping (exactly-one-required, 422 naming `urls`, bound service-owned 413); client `CaptureDraft.urls` + three inputs keeping `capture-url` first; facts in `ScreenshotCaptureTests`.
- [x] WORKFLOW-WAYFINDING-003/005: no code (delivered form; owner decision stands) — suite pins both.
- [x] WORKFLOW-WAYFINDING-006: browser suite `tests/workflow-wayfinding.spec.ts` + `scripts/e2e.sh` suite 28 (own port 5108, `seed_workflow_wayfinding`: project + milestone + evidence + direct connection/candidate rows; links land; import answers; schedule answers; multi-URL accepted; armed counters).

## 3. BFS — Cross-surface regression and completeness
- [x] All six areas render prior panels unchanged; existing testids hold (`capture-url` first input; no publish control added).
- [x] Full `bash scripts/e2e.sh` green (new suite + all pre-existing, 28 suites).
- [x] No current-change placeholders; `git diff --check` clean; no other change's files touched (beyond the named wire piece + its tests).
- [x] CRUD matrix re-verified against shipped UI triggers; holes named, none silent.

## 4. Verification
- [x] `npm ci`, `tsc --noEmit`, `vite build` green.
- [x] Extended `ScreenshotCaptureTests` green (existing verify.sh stage) + new Playwright suite green + full `bash scripts/e2e.sh` green.
- [x] `git diff --check` clean.
- [x] `openspec validate --all --strict --no-interactive` green; archive with `openspec archive workflow-wayfinding --yes` (no `--skip-specs`); confirm canonical spec promoted.
