## ADDED Requirements

### Requirement: PG-TIGHTEN-001 Polling sleep 10 ms with early-exit on terminal status

Every polling loop in the test assembly that waits for an
operation row to reach a target status SHALL sleep 10 ms between
polls (down from 100 ms) and SHALL exit on a terminal status
(`succeeded`/`failed`/`cancelled`/`superseded`) BEFORE the sleep,
not after.

#### Scenario: PG-TIGHTEN-001 Success

- **WHEN** any of the 15 polling loops across the 12 test classes
  (`BackgroundJobsApiHost.OperationRows.AwaitAsync`,
  `ScreenshotCaptureTests.RunJobAsync`,
  `ScreenshotCurationTests.RunJobAsync`,
  `ScreenshotEvolutionImagesTests.RunJobAsync`,
  `EvolutionGifExportTests.RunJobAsync`,
  `StaticBlogExportTests.AwaitErrorAsync` and the inline status loop,
  `HtmlContentExportTests` inline status loop,
  `AgencyWeeklyReportsTests.RunJobAsync`,
  `PublicProjectJourneyTests` inline status loop,
  `GroundedMarkdownDraftsTests.AwaitErrorAsync`,
  `GithubEvidenceTests.AwaitErrorAsync`,
  `LocalGitEvidenceTests.AwaitErrorAsync`,
  `GithubGraphqlEnrichmentTests.AwaitErrorAsync`)
  polls an operation row
- **THEN** the per-poll sleep is `await Task.Delay(10)` and the loop
  returns the moment the row reaches the target status (or a terminal
  status, where applicable); the loop's deadline budget is unchanged.

#### Scenario: PG-TIGHTEN-001 Failure

- **WHEN** a polling loop sleeps for more than 10 ms or returns
  AFTER a `Task.Delay` rather than before
- **THEN** the assembly's wait time after a status flip inflates
  and the change has not delivered the boundary it claims.

#### Scenario: PG-TIGHTEN-001 Boundary

- **WHEN** a loop polls something other than an operation row (e.g.
  `GithubGraphqlEnrichmentTests` transport-arrival at line 362 polls
  a transport's `Requests` list, which is only flushed by the polling
  thread)
- **THEN** the 100 ms sleep is unchanged; the boundary is "operation
  row to terminal status", not "any awaitable state to any other
  state".

### Requirement: PG-TIGHTEN-002 Per-call budgets via the `within` argument; 90 s default safety net

Every `OperationRows.AwaitAsync` call site SHALL pass a `within`
argument matching the test's actual expectation (5 s for in-process
operations, 15 s for jobs that hit a stubbed transport, 30 s when an
explicit backoff is being verified), and the 90 s `JobBudget`
constant SHALL remain the safety-net default for any call site that
omits `within`.

#### Scenario: PG-TIGHTEN-002 Success

- **WHEN** any of the 121 `OperationRows.AwaitAsync(..., JobBudget)`
  call sites across the 19 test classes is read
- **THEN** the `JobBudget` argument is replaced with a tighter
  `TimeSpan.FromSeconds(N)` literal (5, 15, or 30) at the call site;
  the `JobBudget` constant remains the safety net default that the
  helper applies when `within` is null.

#### Scenario: PG-TIGHTEN-002 Failure

- **WHEN** a call site omits `within` and the test's actual wait time
  is much shorter than 90 s
- **THEN** the change has not delivered per-call budgets and the
  failure inflates the assembly's wait time on a passing run.

#### Scenario: PG-TIGHTEN-002 Boundary

- **WHEN** a test legitimately needs more than 30 s (e.g. a future
  backoff scenario that the brief did not anticipate)
- **THEN** the call site keeps `JobBudget` (90 s) as the explicit
  argument; the 90 s floor is the safety net, not a constraint to
  lower.

### Requirement: PG-TIGHTEN-003 Full-suite proof of the shared-fixture claim

The full unfiltered `dotnet test Chronicleaf.sln --no-build --nologo
tests/Chronicleaf.Integration.Tests/` run SHALL be green end-to-end
with exactly ONE PostgreSQL container observed across the run and
exactly ONE invocation of the `EnsureTemplateSeededAsync` migrate
delegate; the previous change's filtered 16-test proof is replaced
by the actual 855-test claim.

#### Scenario: PG-TIGHTEN-003 Success

- **WHEN** the full unfiltered run completes
- **THEN** the pass/fail count and duration are recorded (the proof
  is the full run, not a filtered subset); the two-class
  `ContainerId` equality proof plus the stable-`ContainerId`
  rendezvous all observe the same `Server.ContainerId` value; the
  `MigrationDelegateInvocations` probe equals `1`.

#### Scenario: PG-TIGHTEN-003 Failure

- **WHEN** the full run reports any `53300 too many clients already`
  failures, any per-test migration replay, or any non-green test
- **THEN** the change has not delivered the boundary it claims; the
  run is re-checked with the same command and the failure is named.

#### Scenario: PG-TIGHTEN-003 Boundary

- **WHEN** the run is gated behind a Docker daemon that is
  unavailable in the implementation environment
- **THEN** the prerequisite unavailability is recorded in HANDOFF
  rather than fabricated as success; the proof oracles that do not
  require a live container (the pool-string oracle) still run.

### Requirement: PG-TIGHTEN-004 Connection-pool discipline with five self-trimming parameters

Every `new NpgsqlConnection` opened inside a test body SHALL be
wrapped in `await using` (audit confirms zero counter-examples in the
assembly); the fixture's `CreateDatabaseAsync` SHALL return a
connection string carrying `Maximum Pool Size=4; Minimum Pool
Size=0; Connection Pruning Interval=1; Connection Idle Lifetime=5;
Connection Lifetime=30`; the `Pooling` parameter SHALL remain at the
Npgsql default (`true`) for within-test reuse; the sibling
`Platform.Persistence.Postgres.Testing` 0.1.0 SHALL NOT be edited
and PostgreSQL's `max_connections` SHALL NOT be raised.

#### Scenario: PG-TIGHTEN-004 Success

- **WHEN** any of the 65 `new NpgsqlConnection` sites in the
  assembly is read
- **THEN** the connection is wrapped in `await using`; the
  `CreateDatabaseAsync` per-test connection string contains all five
  pool params; the new pool-string oracle test
  (`The_per_test_pool_string_carries_the_self_trimming_parameters`)
  is green.

#### Scenario: PG-TIGHTEN-004 Failure

- **WHEN** a connection is opened without `await using` or the
  per-test connection string drops one of the five pool params
- **THEN** the audit fails and the change has not delivered the
  pool discipline it claims; the pool-string oracle fails loudly.

#### Scenario: PG-TIGHTEN-004 Boundary

- **WHEN** a test relies on more than one concurrent open connection
  on its per-test database
- **THEN** the test is rewritten to use a single sequential
  `await using` chain (no extra parallel work against the per-test
  connection); the `Maximum Pool Size=4` boundary is enforced
  structurally, not by re-raising `max_connections`.
