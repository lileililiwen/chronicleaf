# Design: platform-testing-postgres-tighten

## Context and implementation boundary

Repository Chronicleaf only. Primary boundary
`tests/Chronicleaf.Integration.Tests/BackgroundJobsApiHost.cs` (the
canonical `OperationRows.AwaitAsync` poller; 100 ms → 10 ms), the 12
test classes named in the audit (each with 1-2 polling loops, plus an
early-exit correctness fix on `GithubEvidenceTests.AwaitErrorAsync`
and `GithubGraphqlEnrichmentTests.AwaitErrorAsync`), the 19 test
classes with a `JobBudget` constant (each with 1-12
`OperationRows.AwaitAsync` call sites gaining a tighter per-call
`within` argument matching the test's expectation),
`tests/Chronicleaf.Integration.Tests/ChronicleafPostgresFixture.cs`
(one additional `Connection Lifetime=30` pool parameter plus a
comment block documenting the choice), and
`tests/Chronicleaf.Integration.Tests/PostgresSharedServerProofTests.cs`
(one additional oracle test asserting the per-test pool string carries
all five pool params). Sibling `dotnet-platform-libs` is consumed as a
published artifact only and never edited. Zero product-code change: no
C# outside `tests/`, no TSX, no migration, no API, no envelope.

## Language, ownership and release

C# .NET 10 for the test assembly. The sibling package
`Platform.Persistence.Postgres.Testing` 0.1.0 is owned by
`dotnet-platform-libs` and consumed as a published artifact only
(reconnaissance decision EXTEND, like the predecessor change; no
sibling edit, no fork). The poller, the per-class polling helpers,
the per-call budget tightening, the additional pool parameter, and the
pool-string oracle test are Chronicleaf-owned. No package install; no
feed re-pack; no `nuget.config` change; no nupkg re-pack.

**ADOPT, never edit.** Shared-server postgres testing is
platform-owned recurring capability; Chronicleaf consumes the
published `0.1.0` artifact. No shared `/lib` added and no sibling
edit authorized.

## Contract and compatibility decisions

1. **Polling-sleep granularity.** The canonical
   `OperationRows.AwaitAsync(connectionString, operationId, expectedStatus, within)`
   poller in `BackgroundJobsApiHost.cs` drops the per-poll sleep from
   `await Task.Delay(100)` to `await Task.Delay(10)`. The loop's
   deadline budget (`within ?? TimeSpan.FromSeconds(90)`) is
   unchanged; only the per-poll wait changes. The same drop is applied
   to every per-class `AwaitErrorAsync` / `AwaitStatus` helper that
   polls an operation row to a target status (`GithubEvidenceTests`
   line 1587, `LocalGitEvidenceTests` line 1481, `GithubGraphqlEnrichmentTests`
   line 957, `GroundedMarkdownDraftsTests` line 1478) and to every
   inline status loop (`ScreenshotCaptureTests` line 1640,
   `ScreenshotCurationTests` line 1045,
   `ScreenshotEvolutionImagesTests` line 858,
   `EvolutionGifExportTests` line 862, `StaticBlogExportTests` lines
   1055 and 1238, `HtmlContentExportTests` line 823,
   `AgencyWeeklyReportsTests` line 1026,
   `PublicProjectJourneyTests` line 1056). The
   `GithubGraphqlEnrichmentTests` transport-arrival loop at line 362
   is unchanged (it polls a transport, not an operation row, and the
   transport's `Requests` list is only flushed by the polling thread —
   the 100 ms there is not a wait-for-status but a wait-for-arrival
   that has no early-exit by construction).

2. **Early-exit correctness.** Every polling loop that waits for an
   operation row to reach a target status has an early-exit on the
   terminal status (`succeeded`/`failed`/`cancelled`/`superseded`)
   BEFORE the `Task.Delay`. The audit found two loops that lack this
   early-exit: `GithubEvidenceTests.AwaitErrorAsync` (line 1587) and
   `GithubGraphqlEnrichmentTests.AwaitErrorAsync` (line 957). Both
   wait for a specific `errorCode` and rely solely on the deadline to
   terminate, which means a row that has already reached a terminal
   status keeps the loop polling until the budget runs out. Each gains
   an early-exit `if (last is not null && last.Status is "failed" or
   "succeeded" or "cancelled") break;` BEFORE the `Task.Delay`. The
   `GroundedMarkdownDraftsTests.AwaitErrorAsync` (line 1478) and
   `StaticBlogExportTests.AwaitErrorAsync` (line 1030) already have
   this early-exit — they are not touched.

3. **Per-call budget tightening.** The 90s `JobBudget` constant stays
   the safety net (it is the default `OperationRows.AwaitAsync`
   applies when `within` is null). Each test method that calls
   `OperationRows.AwaitAsync(..., JobBudget)` is rewritten to pass a
   tighter budget matching the test's actual expectation: 5s for
   in-process operations (most cases — the operation runs in the test
   process and reaches the terminal status within seconds), 15s for
   jobs that hit a stubbed transport (the transport sleeps on a
   signal, then returns), 30s only when an explicit backoff is being
   verified (the backoff path takes longer to settle). The 90s floor
   stays in `JobBudget` so any test that forgets to pass an explicit
   budget still has a generous safety net.

4. **Connection-pool discipline.** The fixture's
   `CreateDatabaseAsync(string purpose)` returns a connection string
   built from `Server.CreateIsolatedDatabaseAsync(name, default)` (the
   sibling API that clones the template) plus the Npgsql pool params
   composed via `NpgsqlConnectionStringBuilder` so they do not parse
   the sibling string:
   - `Maximum Pool Size=4` (predecessor change, retained);
   - `Minimum Pool Size=0` (predecessor change, retained);
   - `Connection Pruning Interval=1` (predecessor change, retained);
   - `Connection Idle Lifetime=5` (predecessor change, retained);
   - `Connection Lifetime=30` (new, this change) — recycles any
     connection older than 30 s, so a stuck connection cannot hold a
     PostgreSQL backend slot indefinitely.
   `Pooling` stays at the Npgsql default (`true`) so within-test reuse
   keeps the suite fast; the boundary is structural on the pool, not
   on `Pooling=false` (which would force a TCP+TLS handshake on every
   `OpenAsync` and slow the suite). The audit confirms every
   `new NpgsqlConnection` opened inside a test body is wrapped in
   `await using` — zero counter-examples in the assembly.

5. **Sibling options audit.** The sibling's XML docs enumerate
   `PostgresTestServerOptions` properties as `Image`, `Username`,
   `Password`, `MaintenanceDatabase` only — **no `MaxConnections`
   knob**. The brief explicitly forbids editing the sibling to add one,
   so the bound stays structural on the Npgsql pool.

6. **Shared-fixture completion proof.** The full unfiltered dotnet
   suite (855 tests, 51 classes) is the proof: the assembly reports
   one PostgreSQL container, one template-seed migration, no `53300`
   errors, no per-test `MigrateAsync` against an already-migrated
   database. The two-class `ContainerId` equality proof
   (`PostgresSharedServerProofA` and `PostgresSharedServerProofB`)
   and the `MigrationDelegateInvocations == 1` proof
   (`PostgresSharedServerProofC`) are unchanged; the
   `ContainerId` stability rendezvous
   (`PostgresSharedServerProofD`) is unchanged. One new oracle test
   (`The_per_test_pool_string_carries_the_self_trimming_parameters`)
   reads `ChronicleafPostgresFixture.CreateDatabaseAsync("pool-format")`
   and asserts the returned string contains
   `Maximum Pool Size=4`, `Minimum Pool Size=0`,
   `Connection Pruning Interval=1`, `Connection Idle Lifetime=5`, and
   `Connection Lifetime=30`.

## Behavioral model and invariants

- One container serves the whole dotnet test assembly; one template
  seed migration runs at most once; every per-test database is cloned
  from that seeded template (the sibling's `CreateIsolatedDatabaseAsync`
  contract).
- Polling granularity: every operation-status loop returns within 10
  ms of the row reaching the target status (down from 100 ms). Over
  the 855-test serial assembly, the cumulative wait after a status
  flip drops by approximately `(number of polls) × 90 ms per loop`,
  which is itself a measurable fraction of the 14m31s baseline.
- Early-exit invariant: every loop that polls an operation row to a
  target status returns as soon as the row reaches a terminal
  status (`succeeded`/`failed`/`cancelled`/`superseded`), never
  waiting for the deadline. Two loops in the audit lacked this
  early-exit and are fixed in this change.
- Per-call budget invariant: every `OperationRows.AwaitAsync` call
  site passes a `within` argument that matches the test's actual
  expectation (5s in-process, 15s stubbed-transport, 30s backoff).
  The 90s `JobBudget` constant stays the safety-net default for any
  call site that omits `within`.
- Pool-self-trimming invariant: the cumulative live PostgreSQL
  connections on the shared server are at most
  `1 (active test) + (active test's pool history)` in steady state,
  and `Connection Lifetime=30` recycles any connection older than 30 s
  so a stuck connection cannot hold a backend slot indefinitely.

## Failure, boundary and rollback policy

- A wrong `Task.Delay` argument fails `dotnet build` (the call is
  type-checked against `Task.Delay(int)` / `Task.Delay(TimeSpan)`);
  the failure is loud at compile time, not silent at runtime.
- A loop that loses its early-exit returns to the predecessor
  failure class (status reached but loop polls until deadline); the
  `dotnet test` run time increases measurably and the regression is
  caught by the full-suite proof.
- A wrong `within` argument (e.g. 100 ms when the test needs 5 s)
  fails the test with a `TimeoutException` from `AwaitAsync`; the
  failure is loud at runtime, not silent.
- A wrong Npgsql pool-param string fails `dotnet build` (the helper
  is type-checked against `NpgsqlConnectionStringBuilder`); the
  failure is loud at compile time, not silent at runtime.
- A pool param that does not actually trim the pool (e.g. an Npgsql
  option renamed in a future release) surfaces as
  `53300: too many clients already` on a real full-suite run; the
  proof is the full run itself, so this failure class is caught by
  the `dotnet test --no-build` invocation in the verification oracle.
- The outage test's separate container (built by `PostgreSqlBuilder`
  in `CommunityPersistenceTests`) has no shared-fixture dependency,
  so its success does not mask a regression in the shared fixture.
- Rollback is harness-only: revert the canonical poller, the per-class
  polling helpers, the per-call budget arguments, the additional pool
  parameter, and the new oracle test; no product, data, migration, or
  API surface is touched, so stored evidence, approvals, credentials
  and operations are unaffected.

## Verification oracle

- `node scripts/check-openspec-change-names.mjs` exit 0.
- `openspec validate --all --strict --no-interactive` green.
- `dotnet build Chronicleaf.sln --nologo` 0 errors.
- Full unfiltered `dotnet test Chronicleaf.sln --no-build --nologo
  tests/Chronicleaf.Integration.Tests/` green end-to-end with the
  exact pass/fail count and duration recorded (the proof is the full
  run, not a filtered subset).
- Container-count oracle green: the two existing proof tests plus the
  stable-`ContainerId` test observe the same `Server.ContainerId`
  value across the assembly run.
- Migration-count oracle green: the
  `The_migration_delegate_runs_exactly_once` probe reads
  `ChronicleafPostgresFixture.MigrationDelegateInvocations` and
  asserts it equals `1`.
- Pool-string oracle green: the new
  `The_per_test_pool_string_carries_the_self_trimming_parameters`
  probe reads the connection string returned by
  `ChronicleafPostgresFixture.CreateDatabaseAsync("pool-format")` and
  asserts it contains all five pool params.
- `bash scripts/gate/dotnet-tests.sh` exit 0 (the same single
  unfiltered run used by the full gate's dotnet stage).
- `git diff --check` clean.

## Decision ledger and activation gates

Phase: POST-MVP test-infrastructure tightening. No product-policy
decision is open; every numbered decision above is implementer-binding.
Missing Docker daemon at implementation time is recorded prerequisite
unavailability, never fabricated success.

1. Sibling reconnaissance decision = EXTEND (consumer-side hardening
   of the same sibling; no sibling edit, no fork).
2. Polling-sleep granularity: 100 ms → 10 ms; loop deadline budget
   unchanged; every loop's early-exit on terminal status preserved
   (or added, for the two that lacked it).
3. Per-call budget: 5s in-process / 15s stubbed-transport / 30s
   backoff; the 90s `JobBudget` constant stays the safety-net default.
4. Pool-self-trimming params (this change):
   `Maximum Pool Size=4; Minimum Pool Size=0; Connection Pruning
   Interval=1; Connection Idle Lifetime=5; Connection Lifetime=30`;
   `Pooling` stays default; no `max_connections` raise; no sibling
   edit.
5. One new oracle test in `PostgresSharedServerProofTests`:
   `The_per_test_pool_string_carries_the_self_trimming_parameters`
   asserts all five pool params are present on the per-test
   connection string.
6. Verification is the full unfiltered suite, not a filtered subset;
   the proof is the actual pass/fail counts and duration, plus the
   one-container / one-migration / five-pool-params oracles.

## Requirement traceability

| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| PG-TIGHTEN-001 | 10 ms polling sleep with early-exit on terminal status | Decisions 1, 2 | `BackgroundJobsApiHost.AwaitAsync` + 12 test class polling helpers + inline status loops | PG-TIGHTEN-001 Success/Failure/Boundary | 1.1, 2.1, 3.1, 4.1 | Full unfiltered suite green; `Task.Delay(10)` everywhere; early-exit on terminal status before delay everywhere |
| PG-TIGHTEN-002 | Per-call budget via `within` argument; 90s default safety net | Decision 3 | 19 test classes with `JobBudget` constant | PG-TIGHTEN-002 Success/Failure/Boundary | 1.2, 2.2, 3.1, 4.1 | Per-call budgets 5s/15s/30s matching test expectation; 90s default still applied when `within` is null |
| PG-TIGHTEN-003 | Full-suite proof of the shared-fixture claim | Decisions 1-4 + 6 | Full unfiltered `dotnet test` run + one-container + one-migration oracle | PG-TIGHTEN-003 Success/Failure/Boundary | 1.3, 2.3, 3.1, 4.2 | 855 tests green; `Server.ContainerId` stable across run; `MigrationDelegateInvocations == 1` |
| PG-TIGHTEN-004 | Connection-pool discipline: `await using` + one additional `Connection Lifetime=30` + pool-string oracle | Decisions 4, 5 | Fixture adapter + audit + pool-string oracle test | PG-TIGHTEN-004 Success/Failure/Boundary | 1.4, 2.4, 3.1, 4.3 | Audit confirms zero `new NpgsqlConnection` without `await using`; pool-string oracle confirms all five pool params |
