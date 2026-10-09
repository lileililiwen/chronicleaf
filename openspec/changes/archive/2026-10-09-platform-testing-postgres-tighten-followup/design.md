# Design: platform-testing-postgres-tighten-followup

## Context and implementation boundary

Repository Chronicleaf only. Primary boundary:

- `scripts/e2e.sh`: one new `assert_suite_ports_free` function called
  from `start_suite` BEFORE the readiness loop, plus one new
  `stop_orphan_servers` step in `cleanup()` that walks
  `pgrep -f "dotnet Chronicleaf.Api.dll"` and `kill`s every match the
  current script-PID-scoped orphan may have left. The function is
  pure bash, no subprocess tools beyond `ss`/`lsof` and `pgrep`,
  and the assertion happens BEFORE the server is launched so a held
  port fails the gate loudly with the orphan PID printed.
- `scripts/gate/e2e-browser.sh`: one new "preflight" stage that
  imports `assert_suite_ports_free` via the same sh-compatible export
  pattern `e2e.sh` already uses for `start_suite`, and fails the gate
  with a per-port listing when any origin is held.
- `src/Chronicleaf.Web/src/features/content-pack-panels/api.ts`:
  `savePackDownload` keeps the anchor in the DOM for one microtask
  before removing it. The pattern is
  `anchor.click(); queueMicrotask(() => anchor.remove());`. The blob
  URL still lives for the page lifetime (unchanged from the
  predecessor implementation; the predecessor comment already states
  revoking races the click on some browsers).
- `src/Chronicleaf.Web/tests/content-pack-panels.spec.ts`: the linkedin
  sub-test's download click is rewritten to the same
  `Promise.all([page.waitForEvent('download'), click()])` pattern the
  sibling medium/xiaohongshu tests use (it already does), and the
  post-click assertions wait for `linkedin-pack-download` to be
  disabled (downloading=true) before the `download.path()` call so the
  microtask fix has visibly engaged. No selector change.

Sibling `dotnet-platform-libs` is consumed as a published artifact
only and never edited. Zero product-code change: no C# outside
`tests/`, no TSX beyond the one microtask tweak, no migration, no
API, no envelope.

## Language, ownership and release

Bash for `scripts/e2e.sh` / `scripts/gate/e2e-browser.sh`; TypeScript
for `src/Chronicleaf.Web/src/features/content-pack-panels/api.ts` and
the linkedin sub-test. The port-orphan check is bash (the script
already speaks bash, no new tool dependency), the microtask fix is
one line in the same `savePackDownload` helper, and the linkedin test
assertion is one extra `expect.toBeDisabled()` poll that the
sibling tests already use. No package install; no feed re-pack; no
`nuget.config` change; no nupkg re-pack.

**ADOPT, never edit.** All four touch points are Chronicleaf-owned
test infrastructure and the Web client's content-pack-panels feature.
Shared-server postgres testing is platform-owned and was already
adopted in the predecessor change. No shared `/lib` added and no
sibling edit authorized.

## Contract and compatibility decisions

1. **Port-orphan detection shape.** `assert_suite_ports_free` is one
   bash function with one argument: a space-separated list of
   `origin` URLs. For each, it parses the port out of the URL and
   walks `ss -tnlp "sport = :$port"` (the same `ss` flavor the
   predecessor change uses for nothing, but already installed on
   the verification host). When a listener exists whose `users:`
   line names `dotnet`, the function prints the port, the PID, the
   full command line of the PID (so an operator can read the held
   `Chronicleaf__AccessTokenHash` from `/proc/$pid/environ` if
   needed), and exits non-zero. The check runs BEFORE
   `clone_suite_database`, so a held port fails the gate without
   cloning a disposable database or starting a server.

2. **Orphan-process cleanup shape.** `cleanup()` gains one new step
   after the `docker rm` step: `pkill -f "dotnet
   Chronicleaf.Api.dll"` (gated by `pkill -0` first so a missing
   `pkill` doesn't fail the run). This catches the same orphan
   class the predecessor change could leave: a `dotnet
   Chronicleaf.Api.dll` process the script started via `start_suite`
   but whose subshell exited abnormally. The `cleanup()` step is
   idempotent (killing a non-existent PID is a no-op for `pkill`),
   so it stays safe to run on every clean exit.

3. **Readiness loop unchanged.** The 60-second `curl --fail --silent
   --show-error --max-time 2 "$origin/api/health"` loop in
   `start_suite` stays byte-identical to the predecessor change.
   With `assert_suite_ports_free` gating the loop's entry, an
   orphan can no longer answer `/api/health` masquerading as a
   ready server. The loop's `kill -0 $server_pid` early-exit
   stays in place for the case where the new server itself
   crashes during the readiness wait.

4. **Pre-flight stage in the gate.** `scripts/gate/e2e-browser.sh`
   is a thin wrapper today (exec the gate runner). It gains one new
   `preflight` stage before the e2e invocation that sources
   `scripts/e2e.sh`'s function definitions and runs
   `assert_suite_ports_free` against the 30 origins. The stage
   exits non-zero when an orphan is holding any of them, with a
   message naming the held port and PID. This is the user-visible
   artifact of the curation fix.

5. **`savePackDownload` microtask fix shape.** The helper becomes:
   ```ts
   const anchor = document.createElement('a');
   anchor.href = url;
   anchor.download = fileName;
   document.body.appendChild(anchor);
   anchor.click();
   queueMicrotask(() => anchor.remove());
   ```
   The blob URL is NOT revoked (predecessor comment is unchanged).
   The microtask runs after the current task, giving Chromium's
   download pipeline one tick to register the navigation-to-blob
   download before the anchor is detached. The pattern is the
   minimum fix the linkedin download race needs; it does not
   change any visible behavior (the anchor is detached in the same
   event-loop turn, just after the click handler returns) and it
   does not require the medium / xiaohongshu / video-story paths
   to change (their tests pass without it under the same parallel
   boot, so the fix is scoped to the test that exposed it and the
   helper that drives it).

6. **Linkedin test pin.** The linkedin sub-test gains one extra
   assertion between the approve-click and the download-click:
   `expect(panel.getByTestId('linkedin-pack-download')).toBeEnabled()`
   (the button is enabled when `approved` is true, which the
   predecessor assertion `toHaveText('approved')` already pins).
   The test then awaits the same `Promise.all([waitForEvent(
   'download'), click()])` the sibling tests use, and `download.path()`
   is wrapped in a `try/catch` that rethrows with the same error
   message format. No selector change.

7. **Verification oracle.** The full `CHRONICLEAF_E2E=1 bash
   scripts/gate/e2e-browser.sh` gate is the proof. Two consecutive
   runs (back-to-back, no clean-up between them — the second run
   exercises the orphan-cleanup step from the first run) must
   both report 30/30 green. The `dist/e2e/playwright-curation.log`
   file must end in a Playwright "passed" line for
   `tests/screenshot-curation.spec.ts`; the `dist/e2e/
   playwright-content_pack.log` file must end in four "passed"
   lines (linkedin, medium, xiaohongshu, bare) without the
   `Error: download.path: canceled` line.

## Behavioral model and invariants

- One container serves the whole run (predecessor invariant, unchanged).
- One template seed migration runs at most once per run (predecessor
  invariant, unchanged).
- Per-suite isolation: own database, own server, own port, own token
  per suite (predecessor invariant, unchanged).
- Per-suite ports are free at `start_suite` entry; an orphan holding
  any port fails the gate loudly with the PID printed (new invariant,
  PG-TIGHTEN-FU-001).
- `cleanup()` removes every `dotnet Chronicleaf.Api.dll` process the
  script started (new invariant, PG-TIGHTEN-FU-001).
- `savePackDownload` detaches its anchor in a microtask, so Chromium
  has one tick to register the blob-URL download before the anchor is
  removed (new invariant, PG-TIGHTEN-FU-002).
- The LinkedIn copy download path serves byte-equal content to the
  direct fetch (predecessor invariant, unchanged).

## Failure, boundary and rollback policy

- A held port at `start_suite` entry fails the gate with the PID
  printed; the operator reads the orphan's command line via
  `/proc/$pid/cmdline` and `kill`s it. The error is loud at
  preflight time, not silent at runtime.
- A `pkill` of a missing PID is a no-op (no signal sent, exit 1 from
  `pkill`); the cleanup step's exit code is checked but ignored when
  it returns 1 from "no process matched" (we only fail if a process
  WAS killed and refused to die).
- A `savePackDownload` microtask that runs too late for Chromium to
  register the download still cancels the download the predecessor
  way (anchor removed before the download was queued). This is the
  boundary: the microtask is the minimum delay Chromium needs to
  register the download; larger delays would change the visible
  behavior (anchor visibly flashes). Verified empirically that the
  sibling medium/xiaohongshu/bare tests pass with the fix in place.
- A held port that is NOT a `dotnet Chronicleaf.Api.dll` process
  (e.g., some other dev-time server) still fails the preflight
  loudly; the operator decides whether to free it or rename the
  test port. This is documented in the preflight stage's stderr
  message.
- Rollback is test-infrastructure only: revert the four touch points
  and the new spec; no product, data, migration, API or envelope
  surface is touched, so stored evidence, approvals, credentials
  and operations are unaffected.

## Verification oracle

- `node scripts/check-openspec-change-names.mjs` exit 0.
- `openspec validate --all --strict --no-interactive` green.
- `dotnet build Chronicleaf.sln --nologo` 0 errors.
- Full `CHRONICLEAF_E2E=1 bash scripts/gate/e2e-browser.sh` exit 0
  with `set -o pipefail` and explicit `GATE_EXIT=0`. The first run
  starts from a clean state (no orphans). The second run starts
  within 5 seconds of the first run's cleanup — it exercises the
  orphan-cleanup step from `cleanup()`. Both runs must report
  30/30 green, with the `curation` and `content_pack` suites both
  green in both runs.
- `dist/e2e/playwright-curation.log` ends in a Playwright "passed"
  line for `tests/screenshot-curation.spec.ts` (no antiforgery
  failure message).
- `dist/e2e/playwright-content_pack.log` ends in four Playwright
  "passed" lines (linkedin, medium, xiaohongshu, bare) without the
  `Error: download.path: canceled` line.
- `bash scripts/gate/dotnet-tests.sh` exit 0 (the same single
  unfiltered run used by the full gate's dotnet stage) — the
  current batch's tightened suite stays green; this change does
  not touch any C# test file.
- `git diff --check` clean.
- The two owner-BLOCKED changes stay deferred to ROADMAP.

## Decision ledger and activation gates

Phase: POST-MVP test-infrastructure followup. No product-policy
decision is open; every numbered decision above is implementer-binding.
Missing Docker daemon or browser at implementation time is recorded
prerequisite unavailability, never fabricated success.

1. Sibling reconnaissance decision = ADOPT (no sibling edit, no
   fork).
2. Port-orphan detection is a bash preflight that prints the PID
   and fails the gate; the readiness loop in `start_suite` is
   unchanged.
3. Orphan-process cleanup is one `pkill -f "dotnet
   Chronicleaf.Api.dll"` step in `cleanup()`, idempotent.
4. Gate preflight stage runs `assert_suite_ports_free` against the
   30 origins before invoking the e2e run.
5. `savePackDownload` microtask fix is one line; the blob URL is
   not revoked (predecessor invariant, unchanged).
6. Linkedin sub-test pin is one extra `toBeEnabled()` assertion;
   no selector change.
7. Verification is two consecutive full `CHRONICLEAF_E2E=1 bash
   scripts/gate/e2e-browser.sh` runs, both 30/30 green, with the
   two previously failing suites green in both runs.

## Requirement traceability

| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| PG-TIGHTEN-FU-001 | Orphan port-orphan handling so curation seed never signs in against an orphan | Decisions 1, 2, 3, 4 | `scripts/e2e.sh` (assert_suite_ports_free + stop_orphan_servers) + `scripts/gate/e2e-browser.sh` (preflight stage) | PG-TIGHTEN-FU-001 Success/Failure/Boundary | 1.1, 2.1, 3.1, 4.1 | Full gate 30/30 twice; `dist/e2e/playwright-curation.log` ends in passed line; orphan PID printed on failure |
| PG-TIGHTEN-FU-002 | Linkedin copy download race fixed in `savePackDownload` microtask + linkedin test pin | Decisions 5, 6 | `src/Chronicleaf.Web/src/features/content-pack-panels/api.ts` + `tests/content-pack-panels.spec.ts` (linkedin sub-test only) | PG-TIGHTEN-FU-002 Success/Failure/Boundary | 1.2, 2.2, 3.1, 4.1 | Full gate 30/30 twice; `dist/e2e/playwright-content_pack.log` ends in 4 passed lines without `download.path: canceled` |
| PG-TIGHTEN-FU-003 | Zero product-code change, zero new API, zero new schema | Decision 7 | The four touch points named above (all test infra or one helper) | PG-TIGHTEN-FU-003 Success/Failure/Boundary | 1.3, 2.3, 3.1, 4.2 | `git diff --stat` names only the four touch points + the spec; no C# outside `tests/`, no migration, no API, no envelope |