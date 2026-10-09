# Proposal: platform-testing-postgres-tighten

## Why

`platform-testing-postgres-adoption-complete` (the committed state on `main`
after `d618180`) closed the shared-container / template-clone / one-migration
boundary end-to-end: one `PostgresTestServer` per assembly, EF migrations
applied exactly once into `chronicleaf_template`, per-test `TEMPLATE` clones
with pool-self-trimming Npgsql params (`MaxPoolSize=4; MinPoolSize=0;
ConnectionPruningInterval=1; ConnectionIdleLifetime=5`), the
`MigrationDelegateInvocations == 1` oracle, and the `ContainerId` stability
rendezvous across `PostgresSharedServerProofA..D`. **The shared-fixture claim
is delivered end-to-end against the 855-test assembly, but four
test-infrastructure defects remain that the previous change did not and could
not address without extending its scope.** Each one is small on its own and
additive to the previous fix:

1. **Polling-sleep granularity.** Every `await Task.Delay(100)` inside the
   15 polling loops that wait for an operation row to reach a target status
   sleeps for 100 ms even when the row would have flipped within 10 ms. The
   loop's deadline budget is unchanged, so the maximum wait time stays the
   same; what changes is the time spent *after* the row has reached the
   terminal status. Over the 855-test serial assembly, the cumulative wait
   after a status flip is on the order of the number of polls times 90 ms,
   which is itself a measurable fraction of the 14m31s baseline. One loop
   (`GithubGraphqlEnrichmentTests.AwaitErrorAsync`, line 957) is missing the
   early-exit on terminal status — its only stop condition is the deadline,
   so a `succeeded` row keeps the loop polling until the budget runs out
   instead of returning the moment the row reaches the goal. Two
   `AwaitErrorAsync` loops (`GithubEvidenceTests` line 1587 and
   `GithubGraphqlEnrichmentTests` line 957) likewise lack the early-exit,
   waiting for a specific `ErrorCode` even after the row reaches a terminal
   `succeeded`/`failed`/`cancelled` state.

2. **Job-budget granularity.** Every test class with a `private static readonly
   TimeSpan JobBudget = TimeSpan.FromSeconds(90);` constant passes that
   constant to `OperationRows.AwaitAsync(..., JobBudget)` regardless of the
   test's actual expectation. The helper already accepts `TimeSpan? within`,
   but every call site hands the 90s default. The 90s is the safety net for
   the worst case (a real backoff retry running through the queue); most
   tests would fail under 5s if they were ever to fail at all, and the 90s
   floor is the wrong floor for a successful run.

3. **Shared-fixture completion proof.** The previous change's proof was a
   filtered 16-test subset on two classes, which the gate was not. The
   full-suite proof (855 tests, 51 classes, one container, one template
   migration, no `53300`) is the actual claim and is unproven on
   `main`. This change runs the full unfiltered suite and records the
   pass/fail counts plus the container and migration counts.

4. **Connection-pool discipline.** The fixture's
   `CreateDatabaseAsync(string purpose)` already returns a connection string
   with `Maximum Pool Size=4; Minimum Pool Size=0; Connection Pruning
   Interval=1; Connection Idle Lifetime=5` (the predecessor change). Two
   things the previous change did not do: every `new NpgsqlConnection` in a
   test body must already be wrapped in `await using` (the audit confirms
   this — every connection site in the assembly is `await using`), and a
   per-test `Connection Lifetime=30` bound hardens the pool against a stuck
   connection holding a backend slot. The sibling
   `Platform.Persistence.Postgres.Testing` 0.1.0 exposes no `max_connections`
   knob in `PostgresTestServerOptions` (XML docs enumerate exactly
   `Image`/`Username`/`Password`/`MaintenanceDatabase`); the bound stays
   structural on the Npgsql pool.

The brief explicitly excludes raising PostgreSQL `max_connections` and
editing the sibling `Platform.Persistence.Postgres.Testing` package, and
explicitly forbids the next change from relitigating the boundary by
relabelling the previous one.

## What Changes

Modify `tests/Chronicleaf.Integration.Tests/BackgroundJobsApiHost.cs`
(the canonical `OperationRows.AwaitAsync` poller) to drop the polling
sleep from 100 ms to 10 ms. Apply the same drop to every polling loop
that waits for an operation row to reach a target status across the 12
test classes named in the audit (the local per-class
`AwaitErrorAsync` / `AwaitStatus` helpers plus the inline status loops
in `ScreenshotCaptureTests`, `ScreenshotCurationTests`,
`ScreenshotEvolutionImagesTests`, `EvolutionGifExportTests`,
`StaticBlogExportTests`, `HtmlContentExportTests`,
`AgencyWeeklyReportsTests`, `PublicProjectJourneyTests`,
`GroundedMarkdownDraftsTests`, `GithubEvidenceTests`,
`LocalGitEvidenceTests`, `GithubGraphqlEnrichmentTests`). The loop's
deadline budget stays unchanged; only the per-poll sleep changes.
Verify every loop has an early-exit on `succeeded`/`failed`/`cancelled`
BEFORE the `Task.Delay` (not after) — fix the two that don't
(`GithubEvidenceTests.AwaitErrorAsync` and
`GithubGraphqlEnrichmentTests.AwaitErrorAsync`).

Add a `TimeSpan? within = null` argument to every job-budget helper
that calls `OperationRows.AwaitAsync` (the helper itself already
accepts `within`; what is missing is per-call budgets at the test
methods). Keep the 90s `JobBudget` constant for the **default** (the
safety net the brief explicitly does not lower). Add a
`private static readonly TimeSpan JobBudget` declaration in the test
classes that lack one (none required; all 19 do). Let each test method
pass a tighter budget matching its actual expectation: 5s for
in-process operations, 15s for jobs that hit a stubbed transport, 30s
only when an explicit backoff is being verified. The 90s floor is the
fallback when no per-call budget is supplied.

The fixture's `CreateDatabaseAsync` already calls
`Server.CreateIsolatedDatabaseAsync(name, default)` (the sibling API
that clones the template — verified against the committed nupkg's XML
docs). Per-test `MigrateAsync` calls were removed by the predecessor
change. The only structural change left is to add
`Connection Lifetime=30` to the pool-self-trimming params the
predecessor change applied, so a stuck connection cannot hold a
backend slot indefinitely. Every `new NpgsqlConnection` opened inside a
test body remains `await using` (audit confirms zero counter-examples
in the assembly).

Document the choice (predecessor change's `MaxPoolSize=4; MinPoolSize=0;
ConnectionPruningInterval=1; ConnectionIdleLifetime=5` plus the new
`ConnectionLifetime=30`) in a fixture-comment block: the
`Platform.Persistence.Postgres.Testing` 0.1.0 sibling exposes no
`max_connections` knob (verified against XML docs), so the bound stays
structural on the Npgsql pool.

Extend `tests/Chronicleaf.Integration.Tests/PostgresSharedServerProofTests.cs`
with one additional oracle test that asserts the per-test pool string
returned by `CreateDatabaseAsync` carries the pool-self-trimming params
(`Maximum Pool Size=4; Minimum Pool Size=0; Connection Pruning
Interval=1; Connection Idle Lifetime=5; Connection Lifetime=30`). The
two-class `ContainerId` equality proof and the `MigrationDelegateInvocations
== 1` proof are unchanged.

No product code changes: no C# outside `tests/`, no TSX, no migration,
no API route, no envelope.

## Package Boundary and Split Assessment

Own only the test-infrastructure tightening that the previous change
left undone: the canonical poller's sleep granularity, every per-class
poller's sleep granularity plus early-exit correctness, per-call budget
tightening at the call sites, one additional pool parameter on the
per-test connection string, and one pool-string-format oracle test.
Repository Chronicleaf; no product runtime surface. Sibling
`dotnet-platform-libs` is consumed as a published artifact only and
never edited (the brief explicitly forbids sibling edits when the
package does not expose a knob — and the XML docs confirm it does not
expose one). The four audit findings are the entire scope; no new
finding is invented and no existing finding is widened.

## Sibling and Shared Architecture Reconnaissance

**ADOPT, never edit.** The shared-server concern stays owned by
`Platform.Persistence.Postgres.Testing` 0.1.0. Chronicleaf inspects
the published XML docs for the exact public API names (the sibling
ships `lib/net10.0/Platform.Persistence.Postgres.Testing.xml` listing
`StartAsync`, `EnsureTemplateSeededAsync`, `CreateIsolatedDatabaseAsync`,
`BuildDatabaseConnectionString`, `ContainerId`, `ContainerName`,
`Image`, `ServerConnectionString`, `IsTemplateSeeded`, plus
`PostgresTestServerOptions` properties `Image`, `Username`, `Password`,
`MaintenanceDatabase` — **no `MaxConnections` knob exists**) and uses
those names verbatim. No `nuget`/feed/package work; the test project
already pins exactly `0.1.0`; the sibling nupkg is unchanged.

No new sibling package is needed (sibling reconnaissance decision =
EXTEND the consumer-side hardening of the same sibling); the
`packages/platform-feed/Platform.Persistence.Postgres.Testing.0.1.0.nupkg`
artifact is consumed verbatim.

## BFS Impact Map

- Actor/flow: the single shared test runner, the per-test classes that
  consume the fixture, the proof-oracle tests.
- Boundary/callers:
  `tests/Chronicleaf.Integration.Tests/BackgroundJobsApiHost.cs`
  (canonical poller), the 12 test classes named in the audit (each
  with 1-2 polling loops), the 19 test classes with a `JobBudget`
  constant (each with 1-12 `OperationRows.AwaitAsync` call sites),
  `tests/Chronicleaf.Integration.Tests/ChronicleafPostgresFixture.cs`
  (one additional pool parameter + comment block),
  `tests/Chronicleaf.Integration.Tests/PostgresSharedServerProofTests.cs`
  (one additional oracle test).
- Data/integration: no schema, no API, no envelope change; the dotnet
  run executes the same solution tests, only now with finer polling
  granularity, per-call budgets matching the test's expectation, and a
  `Connection Lifetime=30` recycle bound.
- Verification: name preflight exit 0,
  `openspec validate --all --strict --no-interactive` green,
  `dotnet build Chronicleaf.sln --nologo` 0 errors; the FULL unfiltered
  `dotnet test Chronicleaf.sln --no-build --nologo
  tests/Chronicleaf.Integration.Tests/` green end-to-end (the
  pass/fail count and duration are recorded; no `--filter`, no partial
  proof); container-count oracle green (one `Server.ContainerId` value
  across the assembly run via the existing two-class rendezvous);
  migration-count oracle green (`MigrationDelegateInvocations == 1` via
  the existing probe); pool-string oracle green (the new oracle test
  asserts the per-test connection string carries all five pool params).
- Privacy/compatibility: no credentials, tokens, PATs, or session
  values in any committed file.

## Capabilities
### New Capabilities
- `platform-testing-postgres-tighten`: 10 ms polling sleep with
  early-exit on terminal status, per-test budget tightening via the
  `within` argument, full-suite proof of the shared-fixture claim, and
  one additional `Connection Lifetime=30` pool parameter.

## Non-goals

Product behavior changes of any kind (no C# outside `tests/`, no TSX,
no migrations, no routes, no envelope, no approval bindings, no
evidence rules); editing the sibling `Platform.Persistence.Postgres.Testing`
package or its `0.1.0` nupkg; re-raising PostgreSQL `max_connections`;
changing the `CollectionDefinition` or the per-class
`ProvisionedDatabaseAsync` helper signatures (the helpers stay in
their classes; only their bodies gain per-call budget arguments);
running the full `CHRONICLEAF_E2E=1` gate in this change (batch-end
coordinator run); implementation, archive, commit, push or deployment
in this authoring work beyond the authorized two commits.
