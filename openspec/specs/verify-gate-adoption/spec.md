# verify-gate-adoption Specification

## Purpose
TBD - created by archiving change verify-gate-adoption. Update Purpose after archive.
## Requirements
### Requirement: GATE-ADOPT-001 Gate manifest adopting the sibling runner
The repository SHALL declare its verification policy in
`.ai-gate/gate.yaml` with `runtime: driftwatchdog` under a
project-defined profile that schedules exactly six bound checks
(`platform-feed`, `web-scaffolding-lint`, `dotnet`, `frontend-build`,
`frontend-depth`, `e2e-browser`), adopting the sibling runner without
copying or forking its code.

#### Scenario: GATE-ADOPT-001 Success
- **WHEN** `driftwatchdog gate --dry-run` runs at the repository root
- **THEN** it exits 0 and the resolved plan lists exactly the six checks,
  each with a bound command, and no check reports `UNBOUND`.

#### Scenario: GATE-ADOPT-001 Failure
- **WHEN** the manifest names a check with no command, or `runtime` names
  another tool
- **THEN** the dry run fails (or reports the manifest skipped) before any
  check executes and before anything is persisted.

#### Scenario: GATE-ADOPT-001 Boundary
- **WHEN** both a native `gate.toml` and `.ai-gate/gate.yaml` exist
- **THEN** the native file wins per the sibling's resolution order — so
  this package ships exactly one manifest and no `gate.toml` may be added
  alongside it.

### Requirement: GATE-ADOPT-002 Single dotnet run with parsed attribution
Verification SHALL execute the solution tests as ONE `dotnet build`
followed by ONE unfiltered `dotnet test --no-build`, and per-package
failure attribution SHALL come from parsing the single run's output per
test-class; no `--filter` or `--list-tests` invocation remains anywhere in
the gate path.

#### Scenario: GATE-ADOPT-002 Success
- **WHEN** the `dotnet` check runs on a tree with a failing test in any
  package
- **THEN** the single run fails and the attribution report names the
  failing test-class with its exact failure count, without re-running any
  stage.

#### Scenario: GATE-ADOPT-002 Failure
- **WHEN** a `dotnet test --filter` or `dotnet test --list-tests`
  invocation is present anywhere in `scripts/verify.sh` or
  `scripts/gate/`
- **THEN** the requirement fails: the silent-empty-selection failure class
  is removed by structure (a single unfiltered run), not by guards.

#### Scenario: GATE-ADOPT-002 Boundary
- **WHEN** `CHRONICLEAF_SKIP_DB_TESTS=1` is set
- **THEN** the `dotnet` check errors loudly naming the retirement instead
  of reporting narrowed coverage as green — partial coverage can no longer
  pass.

### Requirement: GATE-ADOPT-003 Existing stages wired plus one deep frontend check
The gate SHALL wire the existing custom stages as checks (platform-feed,
web-scaffolding-lint, `npm ci` + `tsc --noEmit` + `vite build`, and the
e2e browser suites under `CHRONICLEAF_E2E=1`) and SHALL add exactly one new
`frontend-depth` check that executes the token-sweep, form-feedback and
perf-animation suites plus an axe-core full WCAG pass per top-level nav
page, and proves the a11y-core, a11y-roles and responsive-shell suites are
wired into `scripts/e2e.sh`.

#### Scenario: GATE-ADOPT-003 Success
- **WHEN** the `frontend-depth` check runs
- **THEN** the three standalone suites and the per-nav-page axe pass (zero
  critical/serious violations on every top-level nav entry) execute green,
  and each of the three app-backed suite files is proven named in a
  `scripts/e2e.sh` `run_suite` invocation.

#### Scenario: GATE-ADOPT-003 Failure
- **WHEN** an app-backed UI suite file stops being named in any
  `scripts/e2e.sh` `run_suite` invocation, or any nav page reports a
  critical/serious axe violation
- **THEN** the `frontend-depth` check fails loudly, naming the unwired
  suite or the violating page.

#### Scenario: GATE-ADOPT-003 Boundary
- **WHEN** `CHRONICLEAF_E2E` is unset
- **THEN** the `e2e-browser` check prints an explicit SKIP note and exits 0
  rather than running the browser suites — and full-gate evidence is only
  claimed from runs with `CHRONICLEAF_E2E=1`.

### Requirement: GATE-ADOPT-004 verify.sh stays a working thin wrapper
`scripts/verify.sh` SHALL remain the repository's verification entry point
and SHALL call the gate, propagating any failing check as a nonzero exit;
its header comment SHALL name the new manifest, check, and single-run
structure.

#### Scenario: GATE-ADOPT-004 Success
- **WHEN** any gate check fails
- **THEN** `bash scripts/verify.sh` exits nonzero without swallowing the
  failure, falling back to old stages, or adding new behavior of its own.

#### Scenario: GATE-ADOPT-004 Failure
- **WHEN** the wrapper exits 0 while a check failed, or the file is deleted
- **THEN** the requirement fails: HANDOFF evidence and `.project.json`
  reference this path, so deletion or silent success breaks the declared
  verification contract.

#### Scenario: GATE-ADOPT-004 Boundary
- **WHEN** the `driftwatchdog` binary is absent
- **THEN** the wrapper fails loudly (command-not-found propagates nonzero)
  rather than running a degraded local substitute — a fallback would be two
  gates.

