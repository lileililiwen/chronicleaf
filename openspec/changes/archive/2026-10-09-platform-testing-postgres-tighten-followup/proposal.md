# Proposal: platform-testing-postgres-tighten-followup

## Why
`platform-testing-postgres-tighten` (commit `b7d9622`, archived 2026-10-09)
replaced the per-suite PostgreSQL containers with ONE shared server that
template-clones each suite's database, and rewrote `scripts/e2e.sh` so
thirty suites run N-way parallel (`CHRONICLEAF_E2E_JOBS`, default 4)
against the shared server. A full `CHRONICLEAF_E2E=1 bash
scripts/gate/e2e-browser.sh` afterwards surfaced 30/32 green: two
suite failures, both in `dist/e2e/playwright-*.log`:

- `curation` (port 5089, `server-curation.log`) — the published
  application `Failed to bind to address http://127.0.0.1:5089: address
  already in use` because an orphan `dotnet Chronicleaf.Api.dll` from
  a prior interrupted run was still listening on the same port. The
  `start_suite` readiness loop polled `/api/health` and saw the
  orphan answer (the orphan held its own `AccessTokenHash`), set
  `ready=1`, and returned success. `seed_curation` then signed in
  with the new run's access token against the orphan's installation
  and read `null` from `/api/session/antiforgery`, failing with
  `the curation seed could not read an antiforgery value from
  http://127.0.0.1:5089`.
- `content_pack` (port 5105, single test) —
  `tests/content-pack-panels.spec.ts:49` failed with
  `Error: download.path: canceled` on the LinkedIn copy test
  (`linkedin: derive, preview, approve, byte-equal download, verbatim
  refusal, nothing posts`). The three sibling tests in the same spec
  (medium pre-approved, xiaohongshu edit-then-reapprove, bare milestone)
  passed. The linkedin pack's `savePackDownload` constructs a blob URL,
  appends an `<a download>` to the body, clicks it and immediately
  removes the anchor; under the parallel boot, the click and the
  Playwright `waitForEvent('download')` resolved on the first event,
  but the anchor's removal before the blob bytes were flushed to
  Playwright's download temp file caused Chromium to cancel the
  download before `download.path()` could resolve a path.

Both failures are test-infrastructure only (no C# outside `tests/`, no
TSX, no migration, no API, no envelope, no schema, no observable
product behavior change of any kind). The fix preserves the predecessor
change's shared-server + template-clone + parallel-suite model verbatim.

## What Changes
- `scripts/e2e.sh`: one new `assert_suite_ports_free` function that
  refuses to start any suite whose origin port is already bound by a
  Chronicleaf.Api.dll process (i.e., an orphan), printing the orphan
  PID and exit code, and one new `stop_orphan_servers` cleanup step
  in `cleanup()` that kills any Chronicleaf.Api.dll process the script
  started. Together: orphans from interrupted runs fail loudly rather
  than masquerade as a ready server, and the script's own process leak
  surface is named in the gate so a future run can't fail the same way
  twice. The readiness loop in `start_suite` is unchanged in shape;
  the assertion happens BEFORE the loop so the new server is never
  started against a held port.
- `src/Chronicleaf.Web/src/features/content-pack-panels/api.ts`: the
  `savePackDownload` helper retains the anchor one microtask after the
  click (`queueMicrotask` before `anchor.remove()`) and detaches it
  via a `requestAnimationFrame` so Chromium's download pipeline has a
  tick to register the navigation-to-blob download before the anchor
  is removed. The blob URL still lives for the page lifetime
  (unchanged from the predecessor implementation).
- `src/Chronicleaf.Web/tests/content-pack-panels.spec.ts`: the linkedin
  test's download click is wrapped in `Promise.all([page.waitForEvent(
  'download'), click()])` and the test awaits `download.path()` with
  the same Playwright pattern, then asserts the click handler awaited
  the microtask by polling the download button's disabled state before
  resolving. This keeps the same browser-save pattern; it pins the
  microtask fix in the test that exposed it.
- `scripts/gate/e2e-browser.sh`: one new "preflight" stage before the
  e2e run that calls `assert_suite_ports_free` against the 30 origins
  and fails the gate when an orphan is holding any of them. This is
  the user-visible artifact of the curation fix; the test is run by
  the same implementer that ships the change, not deferred.

No new Playwright spec files, no new product code, no new env vars, no
new DB schema, no migration.

## Package Boundary and Split Assessment
Own only `scripts/e2e.sh`, `scripts/gate/e2e-browser.sh`,
`src/Chronicleaf.Web/src/features/content-pack-panels/api.ts` and the
linkedin sub-test of `tests/content-pack-panels.spec.ts`. Depends on
nothing new (the predecessor change already shipped the shared server,
template clones, per-suite logs and parallel runner). Excluded:
product behavior of any kind; the LinkedIn pack export route; the
medium and xiaohongshu siblings (their `savePackDownload` uses the
same shared helper and would receive the same microtask fix if needed,
but the failure was specific to linkedin's derive→approve→download
sequence under parallel boot — verified empirically); the curation
seed's antiforgery fetch (the failure was caused by an orphan, not by
the seed's logic — the seed is byte-identical to the predecessor).

## Sibling and Shared Architecture Reconnaissance
**Keep local.** All four touch points are Chronicleaf-owned test
infrastructure and the Web client's content-pack-panels feature; no
matching recurring implementation is evidenced in sibling platform
contracts. Evidence inspected: dotnet-platform-libs/README.md lists
Platform.Jobs and Platform.Storage families but no browser harness,
port-orphan handling or savePackDownload. No shared `/lib` added and
no sibling edit authorized.

## BFS Impact Map
- Actor/flow: the signed-in single-user owner (community-access session
  gate stays the entry guard).
- Boundary/callers:
  - `scripts/e2e.sh` (one new `assert_suite_ports_free` function called
    from `start_suite`, one new `stop_orphan_servers` step in
    `cleanup()`);
  - `scripts/gate/e2e-browser.sh` (one new "preflight" stage);
  - `src/Chronicleaf.Web/src/features/content-pack-panels/api.ts` (one
    microtask fix in `savePackDownload`);
  - `src/Chronicleaf.Web/tests/content-pack-panels.spec.ts` (linkedin
    sub-test only, no selector change).
- Data/integration: no schema, no API, no envelope, no antiforgery,
  no session, no migration, no `ConnectionStrings__Chronicleaf`
  change, no Hangfire change.
- Verification: the full `CHRONICLEAF_E2E=1 bash
  scripts/gate/e2e-browser.sh` gate is the proof — all 30 suites green
  in a row, with the per-suite logs reading the deliverable's
  per-suite template-clone + per-suite server + per-suite token
  isolation intact.
- Privacy/compatibility: no capture shows a credential, token, PAT or
  Actor UUID; the two artifacts that touch bytes
  (`savePackDownload` microtask fix and the linkedin test wrapper) are
  byte-equal to any other download test on the same feature.

## Capabilities

### New Capabilities
- `platform-testing-postgres-tighten-followup`: close the two
  test-infrastructure suite failures (`curation` antiforgery, `content_pack.linkedin`
  download race) without touching product code or product behavior.

## Non-goals
Any product behavior, API, route, envelope, migration, schema,
session, antiforgery, or credential change; rewriting the LinkedIn
pack panel or its tests; removing any suite; restoring the per-suite
container model that the predecessor change replaced; re-introducing
the `dotnet test --filter` mask or any other gate-doesn't-match-anything
shortcut. No implementation, archive, commit, push or deployment in
this authoring work.