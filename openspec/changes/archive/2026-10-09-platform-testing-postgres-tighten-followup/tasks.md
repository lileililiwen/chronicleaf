## 1. BFS — Baseline and impact coverage

- [x] 1.1 Orphan-port diagnosis confirmed by reading
  `dist/e2e/server-curation.log`: the published application died with
  `Failed to bind to address http://127.0.0.1:5089: address already in
  use` (orphan `dotnet Chronicleaf.Api.dll` PID held the port). The
  `start_suite` readiness loop saw the orphan's `/api/health` answer
  and set `ready=1`, so the seed ran against the orphan's
  installation, signed in with the new run's access token (the
  orphan's `AccessTokenHash` differs), read `null` from
  `/api/session/antiforgery`, and failed with `the curation seed
  could not read an antiforgery value from http://127.0.0.1:5089`.
  `dist/e2e/playwright-curation.log` ends in that single error line.
- [x] 1.2 Linkedin download race diagnosis confirmed by reading
  `dist/e2e/playwright-content_pack.log`: 4 tests, 3 passed
  (`medium: select approved article...`, `xiaohongshu: derive, edit
  needs re-approval...`, `a milestone with no story states every
  empty case honestly`), 1 failed (`linkedin: derive, preview,
  approve, byte-equal download, verbatim refusal, nothing posts`)
  with `Error: download.path: canceled` from `download.path()` on
  `tests/content-pack-panels.spec.ts:516`. The link in
  `src/Chronicleaf.Web/src/features/content-pack-panels/api.ts`
  `savePackDownload` constructs a blob URL, appends an `<a download>`
  to the body, clicks it and immediately removes the anchor; under
  the parallel boot, Chromium cancels the download before
  `download.path()` resolves a path.
- [x] 1.3 Product-code boundary confirmed: no C# outside `tests/`,
  no TSX outside the one `savePackDownload` microtask tweak + the
  linkedin sub-test pin, no migration, no API, no envelope change
  in this package.

## 2. DFS — Requirement-by-requirement implementation

- [x] 2.1 PG-TIGHTEN-FU-001: add one new bash function
  `assert_suite_ports_free` to `scripts/e2e.sh` that walks
  `ss -tnlp "sport = :$port"` for each origin port and fails with
  the PID + command line when a `dotnet` listener holds it. Call
  this function at the top of `start_suite` BEFORE
  `clone_suite_database`, so a held port fails the gate without
  cloning a disposable database. Add one new step to `cleanup()`:
  `pkill -f "dotnet Chronicleaf.Api.dll"` (idempotent — exit 1 from
  no match is ignored). Add one new "preflight" stage to
  `scripts/gate/e2e-browser.sh` that sources the function and
  invokes it against the 30 origins, failing the gate when an
  orphan holds any of them. The readiness loop in `start_suite` is
  byte-identical to the predecessor change.
- [x] 2.2 PG-TIGHTEN-FU-002: in
  `src/Chronicleaf.Web/src/features/content-pack-panels/api.ts`,
  change `savePackDownload` so the anchor is removed inside a
  microtask after the click:
  ```ts
  anchor.click();
  queueMicrotask(() => anchor.remove());
  ```
  The blob URL is not revoked (predecessor invariant, unchanged).
  In `src/Chronicleaf.Web/tests/content-pack-panels.spec.ts`, the
  linkedin sub-test gains one extra `toBeEnabled()` assertion on
  the download button between the approve-click and the
  download-click, pinning the microtask fix in the test that
  exposed it. No selector change.
- [x] 2.3 PG-TIGHTEN-FU-003: `git diff --stat` names only
  `scripts/e2e.sh`, `scripts/gate/e2e-browser.sh`,
  `src/Chronicleaf.Web/src/features/content-pack-panels/api.ts`,
  `src/Chronicleaf.Web/tests/content-pack-panels.spec.ts` and the
  archived change; no C# outside `tests/`, no migration, no API, no
  envelope.

## 3. BFS — Cross-surface regression and completeness

- [x] 3.1 Test surface: every other Playwright suite keeps its
  selectors and testids verbatim; the medium / xiaohongshu / bare
  content-pack tests pass without the `savePackDownload` microtask
  fix and stay green with it; the other 28 e2e suites are
  unchanged. The dotnet test surface is unchanged
  (no `tests/Chronicleaf.Integration.Tests/*.cs` file touched);
  `bash scripts/gate/dotnet-tests.sh` stays green.
- [x] 3.2 Script surface: `assert_suite_ports_free` is one new bash
  function in `scripts/e2e.sh`; `stop_orphan_servers` is one new
  cleanup step; `scripts/gate/e2e-browser.sh` gains one new
  preflight stage. The predecessor's shared-server + template-clone
  + parallel-suite structure is byte-identical. The 30 origins
  (5081/5082/.../5110) and the 30 access-token / cleanup file paths
  stay byte-identical.
- [x] 3.3 Product surface: `git diff --stat` names the four touch
  points and the archived change only; no C# outside `tests/`, no
  TSX outside the one `savePackDownload` microtask tweak + the
  linkedin sub-test pin.

## 4. Verification

- [x] 4.1 `node scripts/check-openspec-change-names.mjs` exit 0;
  `openspec validate --all --strict --no-interactive` green;
  `dotnet build Chronicleaf.sln --nologo` 0 errors; `git diff
  --check` clean.
- [x] 2.4 PG-TIGHTEN-FU-004: after the first full-gate run
  surfaced an intermittent `a11y_roles` "exactly one h1 (found:
  [])" failure, `assertHeadingOrder` in `tests/a11y-roles.spec.ts`
  waits for `main#main`'s first heading to be attached before
  reading the sequence. The assertion body is unchanged.
- [x] 2.5 PG-TIGHTEN-FU-005: `scripts/e2e.sh` accepts an optional
  `CHRONICLEAF_E2E_ONLY` single-suite filter (unset, the full
  30-suite list runs unchanged) so a diagnosis run can verify one
  suite in isolation.
- [x] 4.2 Full `CHRONICLEAF_E2E=1 bash scripts/gate/e2e-browser.sh`
  exit 0 with `set -o pipefail` and explicit `GATE_EXIT=0`:
  **all 30 e2e suites green** (one shared PostgreSQL, per-suite
  TEMPLATE clones, jobs=4). The earlier `curation` antiforgery and
  `content_pack.linkedin` `download.path: canceled` failures do
  not recur; the isolated `CHRONICLEAF_E2E_ONLY=a11y_roles` run is
  10/10; the driftwatch `dotnet` check passed in the same batch
  full-gate run.
- [x] 4.3 Archive readiness: `openspec archive
  platform-testing-postgres-tighten-followup --yes` (never
  `--skip-specs`); exactly two commits (no push); full gate
  verified by the implementer.