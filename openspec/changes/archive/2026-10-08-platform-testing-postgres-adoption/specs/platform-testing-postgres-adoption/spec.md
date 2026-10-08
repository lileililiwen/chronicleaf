## ADDED Requirements

### Requirement: PG-ADOPT-001 Platform feed carries the shared postgres testing package

The repository SHALL resolve `Platform.Persistence.Postgres.Testing`
`0.1.0` from the committed repo-relative feed, referenced pinned at that
exact version by the test project only, with the same cold-restore
discipline that guards the existing platform packages.

#### Scenario: PG-ADOPT-001 Success

- **WHEN** `scripts/pack-platform.sh` runs and the feed is committed
- **THEN** `packages/platform-feed/` contains
  `Platform.Persistence.Postgres.Testing.0.1.0.nupkg`, the test project
  pins exactly `0.1.0`, and `scripts/verify-platform-feed.sh` passes
  including a cold restore into an empty package folder.

#### Scenario: PG-ADOPT-001 Failure

- **WHEN** the feed lacks the testing artifact or the test project floats
  off `0.1.0`
- **THEN** `scripts/verify-platform-feed.sh` fails loudly before any test
  runs, naming the missing artifact or the version drift.

#### Scenario: PG-ADOPT-001 Boundary

- **WHEN** any production project (Core, Application, Api,
  Infrastructure) references the testing package
- **THEN** the change is non-conformant: the testing package is
  test-support by its own contract and only the test project may name it.

### Requirement: PG-ADOPT-002 One assembly-shared postgres server for all dotnet test classes

All persistence tests SHALL run against ONE `PostgresTestServer`
container held in an xUnit `ICollectionFixture`, with the template
seeded exactly once via the existing EF migration delegate.

#### Scenario: PG-ADOPT-002 Success

- **WHEN** two test classes in the collection create their databases
- **THEN** both databases live on the same server (equal `ContainerId`),
  the container count for the assembly is `1`, and migrations replayed
  exactly once (the template seed).

#### Scenario: PG-ADOPT-002 Failure

- **WHEN** a test class boots its own container or replays migrations
  per test
- **THEN** the requirement is unverified: per-class `IClassFixture`
  usage must be gone and the proof tests must show shared server
  identity, not merely green assertions.

#### Scenario: PG-ADOPT-002 Boundary

- **WHEN** the template delegate returns with connections still open
- **THEN** cloning fails loudly at the first `CreateIsolatedDatabaseAsync`
  (PostgreSQL refuses `TEMPLATE` clones under active connections) rather
  than silently sharing rows between tests.

### Requirement: PG-ADOPT-003 Per-test isolated databases with byte-identical assertions

Each test SHALL receive its own GUID-named database cloned from the
migrated template, and every existing test SHALL keep its assertions
byte-identical.

#### Scenario: PG-ADOPT-003 Success

- **WHEN** the two smallest fixture-using classes plus the proof tests run
- **THEN** all pass with the same assertions as before, each against a
  distinct GUID-named database, with no cross-test rows observable.

#### Scenario: PG-ADOPT-003 Failure

- **WHEN** a test observes rows written by another test
- **THEN** isolation is broken: clone-per-test is mandatory and sharing
  a database between tests is never an optimization.

#### Scenario: PG-ADOPT-003 Boundary

- **WHEN** `MinioFixture` is inspected
- **THEN** it stays per-class unchanged: exactly one class consumes it,
  so there is no shared-infrastructure repetition to remove, and this
  requirement claims nothing about it beyond that verdict.

### Requirement: PG-ADOPT-004 Single-container parallel e2e with unchanged isolation

The e2e run SHALL use ONE postgres container, migrate ONE seed template
database, clone per suite, and run suites N-way parallel with per-suite
logs and aggregated failure reporting, while per-suite isolation and
intra-suite `workers: 1` stay unchanged.

#### Scenario: PG-ADOPT-004 Success

- **WHEN** the smallest suite runs end-to-end and (in full runs) all
  suites run with `CHRONICLEAF_E2E_JOBS` parallelism
- **THEN** the suite passes against its own `TEMPLATE`-cloned database
  with its own server, port, token, and seed; per-suite logs exist; and
  any failure report names the failed suite(s) before exiting nonzero.

#### Scenario: PG-ADOPT-004 Failure

- **WHEN** a suite fails
- **THEN** its server is stopped, its status is recorded, remaining suites
  continue, and the run exits nonzero naming every failed suite — a
  failure never passes silently and never poisons another suite's
  database.

#### Scenario: PG-ADOPT-004 Boundary

- **WHEN** intra-suite Playwright parallelism is proposed
- **THEN** it is refused: `workers: 1` stays because empty-state claims
  sharing one database would break under parallel workers in the same
  suite.

### Requirement: PG-ADOPT-005 Zero product-code change

This change SHALL alter no product behavior: no C# outside `tests/`, no
TSX, no migration, no API route, no envelope, no schema, and no
observable behavior change of any kind.

#### Scenario: PG-ADOPT-005 Success

- **WHEN** the implementation diff is audited
- **THEN** it names only test infrastructure (`tests/`,
  `scripts/e2e.sh`, `scripts/pack-platform.sh`,
  `scripts/verify-platform-feed.sh`, `packages/platform-feed/`, and the
  archived change) with every product assertion byte-identical.

#### Scenario: PG-ADOPT-005 Failure

- **WHEN** the diff touches any product source, migration, or contract
- **THEN** the change is out of scope and the product hunk is reverted,
  not justified.

#### Scenario: PG-ADOPT-005 Boundary

- **WHEN** a new e2e suite is added later
- **THEN** its token file joins the cleanup list in the same commit: the
  list stays complete (this change completes the three missing entries —
  candidates, narrative, variants).
