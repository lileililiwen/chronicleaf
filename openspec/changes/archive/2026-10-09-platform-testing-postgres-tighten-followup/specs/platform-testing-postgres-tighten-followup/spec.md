## ADDED Requirements

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
  all 30 suites green (including `curation` and `content_pack`). The
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
  `Error: download.path: canceled`, and the gate reports 29/30
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

### Requirement: PG-TIGHTEN-FU-004 Heading-order read waits for render

The a11y-roles heading-order helper SHALL wait for the SPA to
render the main landmark's first heading before reading the
heading sequence, so a data-heavy project view that is still
hydrating after `goto`'s load event is not read as an empty
heading list. The assertion itself (exactly one h1, no skipped
level) SHALL stay byte-identical.

#### Scenario: PG-TIGHTEN-FU-004 Success
- **WHEN** `tests/a11y-roles.spec.ts` runs against the parallel
  published application and asserts the heading order of a
  project content area
- **THEN** the helper waits for `main#main`'s first heading to be
  attached before evaluating, the subsequent h1 count is 1, and
  the suite reports 10/10.

#### Scenario: PG-TIGHTEN-FU-004 Failure
- **WHEN** the helper reads headings immediately after `goto`
  (predecessor behavior) while a data-heavy view is still
  hydrating
- **THEN** the heading list is empty, the h1 count is 0, the
  assertion fails with "exactly one h1 (found: [])", and the
  gate reports the `a11y_roles` suite missing.

#### Scenario: PG-TIGHTEN-FU-004 Boundary
- **WHEN** a view legitimately has no heading inside `main#main`
- **THEN** the wait times out and the test fails, which is the
  correct outcome — every product view has exactly one h1.

### Requirement: PG-TIGHTEN-FU-005 Single-suite diagnosis filter

`scripts/e2e.sh` SHALL accept an optional `CHRONICLEAF_E2E_ONLY`
value naming one suite (without the `suite_` prefix) and run only
that suite against its own TEMPLATE clone. Unset, the full
30-suite list SHALL run unchanged; the filter SHALL NOT alter the
gate's own path.

#### Scenario: PG-TIGHTEN-FU-005 Success
- **WHEN** `CHRONICLEAF_E2E_ONLY=a11y_roles CHRONICLEAF_E2E=1 bash
  scripts/gate/e2e-browser.sh` runs
- **THEN** only the `a11y_roles` suite runs, it reports 10/10, and
  the harness prints `all 1 e2e suites green`.

#### Scenario: PG-TIGHTEN-FU-005 Failure
- **WHEN** `CHRONICLEAF_E2E_ONLY` names a suite that does not exist
- **THEN** the harness exits non-zero naming the unmatched value
  before starting any suite or clone.

#### Scenario: PG-TIGHTEN-FU-005 Boundary
- **WHEN** `CHRONICLEAF_E2E_ONLY` is unset (the default and the
  gate path)
- **THEN** the 30-element `SUITES` array is unchanged and all 30
  run, proving the filter is a diagnosis aid and not a gate
  bypass.