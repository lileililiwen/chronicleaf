## ADDED Requirements

### Requirement: GATE-FIX-001 platform-feed check runs to completion

The `platform-feed` gate check SHALL be declared with a timeout large enough
for its cold-restore proof to finish, so the check reaches a terminal status
instead of being killed by the runner's 60000 ms default.

#### Scenario: GATE-FIX-001 Success
- **WHEN** `bash scripts/gate/platform-feed.sh` runs under
  `driftwatchdog gate`
- **THEN** the check is allowed at least 1800000 ms, its cold restore of the
  whole solution into an empty package folder completes, and the check exits 0
  rather than `REVIEW_REQUIRED`.

#### Scenario: GATE-FIX-001 Failure
- **WHEN** the check still times out because the restore genuinely cannot
  finish in the declared budget
- **THEN** the timeout is a real signal and is not simply raised again; the
  restore itself is investigated.

#### Scenario: GATE-FIX-001 Boundary
- **WHEN** any other check's timeout is read
- **THEN** it is unchanged: `dotnet`, `frontend-build`, `frontend-depth` and
  `e2e-browser` keep their existing values.

### Requirement: GATE-FIX-002 obj is left consistent after the proof

`scripts/verify-platform-feed.sh` SHALL leave every project's
`obj/project.assets.json` naming a live package folder after its cold-restore
proof, so a later `dotnet build` / `dotnet test --no-build` does not resolve
assemblies against the deleted scratch folder.

#### Scenario: GATE-FIX-002 Success
- **WHEN** `scripts/verify-platform-feed.sh` finishes
- **THEN** the scratch folder has been removed by the EXIT trap AND the
  solution has been re-restored normally so `packageFolders` in a project's
  `obj/project.assets.json` names an existing directory.

#### Scenario: GATE-FIX-002 Failure
- **WHEN** the trailing re-restore itself fails
- **THEN** the script exits nonzero rather than leaving a half-restored
  repository.

#### Scenario: GATE-FIX-002 Boundary
- **WHEN** the cold-restore proof aborts early because the feed is insufficient
- **THEN** the script has already exited via `fail()` and the trailing
  re-restore does not run.

### Requirement: GATE-FIX-003 failure is visible within the diagnostic cap

The `dotnet` gate check SHALL run with output small enough that a test failure
appears inside the runner's `MAX_DIAGNOSTIC_BYTES` (2048) head, while the full
output remains available in `CHRONICLEAF_DOTNET_LOG`.

#### Scenario: GATE-FIX-003 Success
- **WHEN** `scripts/gate/dotnet-tests.sh` runs and a test fails
- **THEN** the first 2048 bytes of the check's combined output contain the
  failed-test detail, not only the build's per-project lines.

#### Scenario: GATE-FIX-003 Failure
- **WHEN** the build and test are run at default verbosity
- **THEN** the build output fills the 2048-byte head and the test failure is
  invisible, which is the defect this requirement removes.

#### Scenario: GATE-FIX-003 Boundary
- **WHEN** the check passes
- **THEN** the output is one build line plus the per-project test summaries;
  the exit-code propagation is unchanged.
