## 1. BFS — Baseline and impact coverage
- [x] 1.1 Map every requirement and Success/Failure/Boundary scenario to src/Chronicleaf.Application/Evolution/, owned state/contracts, API/UI callers and ScreenshotEvolutionImagesTests fixtures; confirm proposal/design/spec agree and record baseline.
- [x] 1.2 Confirm completed dependencies (screenshot-curation, markdown-review-export), C# .NET10 / React TypeScript boundary, shared-package adoption and every activation/blocker decision; do not start a blocked handoff or duplicate sibling code.

## 2. DFS — Requirement-by-requirement implementation
- [x] 2.1 Implement SCREENSHOT-EVOLUTION-IMAGES-001 Evidence-preserving evolution images through its declared contracts, state/IO and applicable API/UI; add tests asserting its specified success, failure and boundary results before marking complete.
- [x] 2.2 Implement SCREENSHOT-EVOLUTION-IMAGES-002 Reviewed visual artifacts through its declared contracts, state/IO and applicable API/UI; add tests asserting its specified success, failure and boundary results before marking complete.

## 3. BFS — Cross-surface regression and completeness
- [x] 3.1 Run ScreenshotEvolutionImagesTests with the exact fixtures in design.md; verify scope, privacy, revision/concurrency, retry/cancel and output invariants across every changed caller, and audit the original impact map for omissions.
- [x] 3.2 Exercise dependency compatibility and retained preexisting history; test documented rollback/partial failure and verify no current-change placeholders, unauthorized side effects or unrelated sibling changes.

## 4. Verification
- [x] 4.1 Run native build and scoped real scenario tests from design.md; execute applicable UI/browser, PostgreSQL or provider-contract checks and local Gate. Record exact commands/results and unavailable prerequisites; no mock-only runtime claims.
- [x] 4.2 Run `node scripts/check-openspec-change-names.mjs`, `openspec validate --all --strict --no-interactive`, `git diff --check`, and review untracked/staged content and the mapped impact surface before archive.
- [x] 4.3 Only after implementation authorization and all scoped acceptance: archive without --skip-specs, create implementation/tests/archive commit, then HANDOFF.md-only pointer/evidence commit; exactly two commits, no conversational confirmation, stop and do not push.

Evidence (all run by the implementer, 2026-10-06):
- 1.1/1.2: proposal/design/spec agree on two requirements; dependencies screenshot-curation
  (9c1ce4a) and markdown-review-export (64d6868) both archived; UI optional per design and
  omitted (backend-only; no Web surface touched, no e2e delta).
- 2.1: Core EvolutionImage + EvolutionLayouts/Status/Limits; Application EvolutionPng
  (BCL-only decode grey/RGB/RGBA8 + uniform-cell compose + encode), EvolutionImageService
  (selected-only reads via IScreenshotCurationStore, media+caption+evidence revisions cited
  in manifest, 40MP/20MiB refusal before any write, media-Put-first/row-second with quiet
  retire); Api EvolutionEndpoints (render/list/read/approve/preview/download); Infra store +
  20261012090000 migration + DbContext/Designer/Snapshot + ChronicleafSchema id; middleware
  arms + ApiErrorStatus arms. Candidates never decoded/placed/cited (pin of a candidate is
  422 selectedScreenshotIds).
- 2.2: draft-until-approved row state bound to selectionHash + artifactSha256; preview serves
  draft bytes with X-Evolution-Approval-Live; download requires live approval
  (approval_required / approval_stale reuse review codes); caption edit supersedes render
  (409 revision_conflict) and stales approval.
- 3.1: ScreenshotEvolutionImagesTests 12/12 against real disposable PostgreSQL + real API
  host (pixel-equality assertions on decoded canvas regions, gap-band 0x40 assertions,
  idempotency replay/conflict, viewport mismatch vs gallery labels, 413 with empty list,
  renderer unit cases for pixel ceiling and gap composition).
- 3.2: full gate `CHRONICLEAF_E2E=1 bash scripts/verify.sh` exit 0 (GATE_EXIT=0): all prior
  filtered stages green (incl. 18/18 curation, 52/52 capture, 11/11 review-export, 10/10
  variants), npm ci + tsc + vite build green, 14 Playwright suites green; no placeholders;
  shared files touched only additively (ApiException arms, middleware arms, Program
  registration, DbContext entity, Schema id, snapshot block, verify.sh stage).
- 4.1: `dotnet build --nologo --no-incremental` 0 errors, same 2 pre-existing warnings
  (CS0649, CS0618 at DbContext:1348); unavailable: real browser capture (no CAP_NET_ADMIN;
  production answers capture_sandbox_unavailable; PNGs are labelled synthesised fixtures).
- 4.2: check-openspec-change-names exit 0; openspec validate 54/54 before archive;
  git diff --check clean; status shows only this change's surface.
