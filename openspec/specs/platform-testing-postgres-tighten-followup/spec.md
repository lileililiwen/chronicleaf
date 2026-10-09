# platform-testing-postgres-tighten-followup Specification

## Purpose
TBD - created by archiving change platform-testing-postgres-tighten-followup. Update Purpose after archive.
## Requirements
### Requirement: PG-TIGHTEN-FU-001 Orphan-port preflight and cleanup

The shared-server + template-clone + parallel-suite harness in
`scripts/e2e.sh` SHALL refuse to start any suite whose origin port
is already bound by a `dotnet` process, and SHALL kill every
`dotnet Chronicleaf.Api.dll` process the script started when its
run ends. The 30 origins (5081..5110) SHALL stay byte-identical to
the predecessor change, the per-suite database/server/token/log
isolation SHALL stay byte-identical, and the readiness loop in
`start_suite` SHALL stay byte-identical.

#### Scenario: PG-TIGHTEN-FU-001 Success
- **WHEN** `CHRONICLEAF_E2E=1 bash scripts/gate/e2e-browser.sh`
  starts a fresh run with no orphans on any origin port
- **THEN** the new preflight stage in `scripts/gate/e2e-browser.sh`
  exits 0, every suite's `start_suite` enters its readiness loop
  against a free port, the 30 suites run N-way parallel against
  the shared PostgreSQL + template clones, and the gate reports
  32/32 green (including `curation` and `content_pack`). The
  suite's per-suite logs end in Playwright "passed" lines without
  any antiforgery failure.

#### Scenario: PG-TIGHTEN-FU-001 Failure
- **WHEN** an orphan `dotnet Chronicleaf.Api.dll` from a prior
  interrupted run holds one of the 30 origins (e.g., port 5089
  from a previous `curation` run)
- **THEN** `assert_suite_ports_free` (called at the top of
  `start_suite` and from the new preflight stage in
  `scripts/gate/e2e-browser.sh`) prints the held port, the orphan
  PID and the orphan's command line, and the gate exits non-zero
  BEFORE any suite is started against the held port. A read of
  the orphan's `AccessTokenHash` from `/proc/$pid/environ` shows it
  differs from the new run's hash — the preflight refuses to
  silently let a suite sign in against the orphan.

#### Scenario: PG-TIGHTEN-FU-001 Boundary
- **WHEN** `cleanup()` runs after a successful run (or an
  interrupted one), it walks `pkill -f "dotnet
  Chronicleaf.Api.dll"` and kills every match. The step is
  idempotent — `pkill` exiting 1 from "no process matched" is
  ignored — so a clean run never fails its own cleanup. A
  follow-up run started within seconds of the first run's
  cleanup finds the 30 origins free and its preflight exits 0,
  proving the cleanup step did its job.

### Requirement: PG-TIGHTEN-FU-002 Linkedin copy download microtask

`savePackDownload` in
`src/Chronicleaf.Web/src/features/content-pack-panels/api.ts`
SHALL detach its `<a download>` anchor in a microtask after the
click so Chromium's download pipeline has one tick to register
the navigation-to-blob download before the anchor is removed. The
blob URL SHALL still live for the page lifetime (predecessor
invariant, unchanged), the medium / xiaohongshu / bare sibling
tests SHALL pass without this microtask and SHALL continue to
pass with it, and the byte-equality oracle for the LinkedIn copy
download SHALL stay byte-identical to the predecessor change.

#### Scenario: PG-TIGHTEN-FU-002 Success
- **WHEN** the LinkedIn copy test in
  `tests/content-pack-panels.spec.ts:49` runs against the real
  published application on its own port, under the parallel
  boot, and clicks the `linkedin-pack-download` button after
  approving the derived pack
- **THEN** the click resolves into a real download event,
  `download.path()` returns a non-null path, the bytes read from
  the offered download equal the bytes read directly from
  `/api/assets/{derivedAssetId}/export?format=linkedin`, and the
  test passes. The sibling medium / xiaohongshu / bare tests stay
  green.

#### Scenario: PG-TIGHTEN-FU-002 Failure
- **WHEN** the LinkedIn copy download click removes its anchor
  synchronously (predecessor behavior) under the parallel boot
- **THEN** Chromium cancels the download before `download.path()`
  can resolve a path, the test fails with
  `Error: download.path: canceled`, and the gate reports 31/32
  green with the LinkedIn copy suite failing.

#### Scenario: PG-TIGHTEN-FU-002 Boundary
- **WHEN** the linkedin test's pin (the extra
  `toBeEnabled()` assertion between approve-click and
  download-click) is removed but the `savePackDownload` microtask
  fix stays
- **THEN** the test still passes — the microtask fix is the
  boundary, the test pin is a regression-locator for future
  readers. The pin does not introduce a selector change.

### Requirement: PG-TIGHTEN-FU-003 Zero product-code change

The change SHALL alter no product behavior: no C# outside
`tests/`, no TSX outside the one `savePackDownload` microtask
tweak + the linkedin sub-test pin, no migration, no API route,
no envelope, no schema, no observable behavior change of any
kind. The dotnet test assembly SHALL stay unchanged.

#### Scenario: PG-TIGHTEN-FU-003 Success
- **WHEN** `git diff --stat` is read after the implementation
  commit
- **THEN** it names only `scripts/e2e.sh`,
  `scripts/gate/e2e-browser.sh`,
  `src/Chronicleaf.Web/src/features/content-pack-panels/api.ts`,
  `src/Chronicleaf.Web/tests/content-pack-panels.spec.ts` and the
  archived change. No C# file in `tests/Chronicleaf.Integration.Tests/`
  is touched.

#### Scenario: PG-TIGHTEN-FU-003 Failure
- **WHEN** a C# file outside `tests/`, a migration, a new API
  route, an envelope change, or a schema change is part of the
  diff
- **THEN** the change is out of scope and the product hunks are
  reverted, not justified.

#### Scenario: PG-TIGHTEN-FU-003 Boundary
- **WHEN** the same shared-server harness later needs another
  preflight (e.g., a port range check for a new suite, or a
  Docker-image pinning check)
- **THEN** it joins `scripts/e2e.sh` in the same commit family;
  the preflight surface stays one function per concern.

