# Design: platform-testing-postgres-adoption

## Context and implementation boundary

Repository Chronicleaf only. Primary boundary
`tests/Chronicleaf.Integration.Tests/ChronicleafPostgresFixture.cs`
(adapter over the platform package), one new `CollectionDefinition`, ~30
test classes (attribute-only change), one new proof-test file,
`scripts/pack-platform.sh` (`PACKAGES` list),
`packages/platform-feed/` (one new `.nupkg`),
`scripts/verify-platform-feed.sh` (expected list + test-project pin), and
`scripts/e2e.sh` (harness: shared container, seed template, parallel
runner; seed functions untouched). Sibling `dotnet-platform-libs` is
consumed as a published artifact only and never edited. Zero product-code
change: no C# outside `tests/`, no TSX, no migration, no API, no envelope.

## Language, ownership and release

C# .NET 10 for the test assembly; bash for `e2e.sh`. The platform package
`Platform.Persistence.Postgres.Testing` 0.1.0 is owned by
dotnet-platform-libs and consumed by exact-version `PackageReference` in
the test project only. The adapter, the collection, and the e2e harness
are Chronicleaf-owned. No package install beyond the feed re-pack; native
dependencies pinned as today.

**ADOPT.** Shared-container postgres testing is platform-owned recurring
capability; Chronicleaf consumes the published `0.1.0` artifact. No shared
`/lib` added and no sibling edit authorized.

## Contract and compatibility decisions

1. Feed: `Platform.Persistence.Postgres.Testing` joins `PACKAGES` in
   `scripts/pack-platform.sh`; its nuspec closure (`Npgsql` 10.0.0,
   `Testcontainers.PostgreSql` 4.15.0,
   `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.0,
   `Microsoft.Extensions.Logging.Abstractions` 10.0.0) resolves from
   nuget.org, so no further feed entry is needed. The committed `.nupkg`
   plus `nuget.config` remain the only restore path (Ruling 12).
2. The test project references the testing package pinned at `0.1.0`;
   `verify-platform-feed.sh` asserts the committed artifact AND the pin,
   with the same cold-restore-into-empty-folder discipline. Production
   projects stay feed-free as today.
3. The adapter keeps the file, the class name `ChronicleafPostgresFixture`,
   constructor injection, and the static helpers (`MigrateAsync`,
   `ExecuteAsync`, `CountRowsAsync`, `ReadTextAsync`, `CreateContext`)
   byte-identical, so call sites change only their xUnit attribute.
   Internally it holds ONE `PostgresTestServer`: `StartAsync` with
   `PostgresTestServerOptions` (image `postgres:17.6-alpine`, superuser
   `chronicleaf`, test-only password unchanged), then
   `EnsureTemplateSeededAsync` with the existing EF `MigrateAsync` as the
   delegate (context disposed before returning, so clones are never
   blocked), into template `chronicleaf_template`.
4. Per-test databases come from `CreateIsolatedDatabaseAsync` with the
   existing GUID name scheme (`chronicleaf_{purpose}_{guid:N}`, 40 chars,
   satisfying the platform `DatabaseNamePattern`); uniqueness stays, cost
   goes to one `CREATE DATABASE ... TEMPLATE`. Per-test `MigrateAsync`
   calls at call sites are retained as idempotent no-op guards; assertions
   are byte-identical.
5. One xUnit collection (`CollectionDefinition` +
   `ICollectionFixture<ChronicleafPostgresFixture>`) replaces ~30
   `IClassFixture<ChronicleafPostgresFixture>` uses. `AssemblyInfo.cs`
   keeps `DisableTestParallelization`; the collection already serializes,
   and the line is load-bearing documentation for the row-count tests.
6. `MinioFixture` stays per-class: exactly one consumer, no repetition to
   remove. Recorded, not changed.
7. `e2e.sh`: ONE container for the whole run; the seed template database
   (`chronicleaf_e2e_seed`) is migrated once; each suite clones it
   (`CREATE DATABASE ... TEMPLATE`) then runs its install INSERT, so
   per-suite DB names, ports, tokens, and servers are unchanged and
   isolation is unchanged. Suites run N-way parallel (`N=4` default,
   `CHRONICLEAF_E2E_JOBS` overridable) with per-suite logs and aggregated
   failure reporting naming the failed suite(s).
8. Playwright keeps `workers: 1` inside suites: intra-suite parallelism
   would break empty-state claims sharing one DB. Stated as a boundary,
   not a tuning knob.
9. The token-file cleanup list is completed (the `candidates`,
   `narrative`, and `variants` token files were missing) and stays
   complete for any suite added later: a new suite adds its token file to
   the list in the same commit.

## Behavioral model and invariants

- One container serves the whole dotnet test assembly; one container
  serves the whole e2e run. Container count is the proof: `1` per run,
  not per class / per suite.
- Migrations run once per run (dotnet template seed; e2e seed template),
  never per test / per suite. Per-test `MigrateAsync` calls are
  history-table no-ops, not replays.
- Isolation is unchanged: per-test GUID databases; per-suite named
  databases with per-suite servers, ports, tokens, and seeds. No suite
  reads state another suite left behind; no test shares rows with another.
- Parallel e2e suites share NOTHING mutable: one container (read-only
  template + independent clones), one publish directory with per-suite
  token/log files, per-suite server processes on distinct ports.
  Suite-scoped env (`CHRONICLEAF_E2E_QUEUED_OPERATION`,
  `CHRONICLEAF_E2E_DRAFT_MILESTONE`) stays inside each suite's subshell.
- A failed suite never poisons the aggregate: its server is stopped by a
  `RETURN` trap, its status is recorded, remaining suites continue, and
  the final report names every failed suite before exiting nonzero.

## Failure, boundary and rollback policy

A missing feed artifact fails `verify-platform-feed.sh` before any test
runs (same as today). A template delegate that leaks connections fails
loudly at the first clone (PostgreSQL refuses `TEMPLATE` clones under
active connections) rather than silently sharing rows. A suite that cannot
clone, migrate-check, seed, or reach `/api/health` fails that suite only.
Rollback is harness-only: revert the adapter/collection/e2e.sh; no data,
migration, or product surface is touched, so product behavior is
unaffected by construction.

## Verification oracle

Light verification only (full gate runs once at batch end by the
coordinator): `node scripts/check-openspec-change-names.mjs` exit 0;
`openspec validate --all --strict --no-interactive` green;
`dotnet build Chronicleaf.sln --nologo` 0 errors; proof (a):
`dotnet test --filter` on exactly the two smallest fixture-using classes
(`MediumImportPackageTests`, `MilestoneTimelineTests`) PLUS the new
proof tests asserting shared server identity (`ContainerId` equality
across two test classes) — container count `1`, migration replay `1`;
proof (b): exactly ONE e2e suite end-to-end (smallest) showing the
template-clone path works. `git diff --check` clean. Archive without
`--skip-specs`.

## Decision ledger and activation gates

Phase: POST-MVP test-infrastructure adoption. No product-policy decision
is open; every numbered decision above is implementer-binding. Missing
docker/DB at implementation time is recorded prerequisite unavailability,
never fabricated success.

1. Sibling reconnaissance decision = ADOPT (published artifact consumed,
   never copied; no sibling edit).
2. Feed mechanics per Ruling 12 (`pack-platform.sh` sole sibling-path
   location; committed `.nupkg`; exact-version enforcement).
3. Adapter kept (file + class name + helpers), not deleted: deletion
   rewrites ~30 call sites for no behavioral gain.
4. One assembly-shared `PostgresTestServer` via `ICollectionFixture` +
   `CollectionDefinition`.
5. Template seeded once with existing EF `MigrateAsync` as delegate;
   per-test `MigrateAsync` retained as no-op guard.
6. `MinioFixture` unchanged (single consumer).
7. `e2e.sh`: one container, seed template, per-suite `TEMPLATE` clones,
   N-way parallel (`N=4`, `CHRONICLEAF_E2E_JOBS` overridable), per-suite
   logs, aggregated failures.
8. Playwright `workers: 1` kept (empty-state claims share one DB).
9. Token-file cleanup list completed and kept complete.

## Requirement traceability

| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| PG-ADOPT-001 | Feed carries the testing package | Decisions 1–2 | pack-platform.sh + platform-feed + verify-platform-feed.sh + test csproj pin | PG-ADOPT-001 Success/Failure/Boundary | 1.1, 2.1, 3.1, 4.1 | Feed file present; pin exact; cold restore resolves |
| PG-ADOPT-002 | One container serves all dotnet classes | Decisions 3–4 | Fixture adapter + CollectionDefinition + proof tests | PG-ADOPT-002 Success/Failure/Boundary | 1.2, 2.2, 3.1, 4.2 | Two classes assert equal ContainerId; container count 1 |
| PG-ADOPT-003 | Per-test template clones, identical assertions | Decision 5 | CreateIsolatedDatabaseAsync GUID names; retained MigrateAsync guards | PG-ADOPT-003 Success/Failure/Boundary | 1.2, 2.3, 3.1, 4.2 | Two smallest classes green; migration replay 1 |
| PG-ADOPT-004 | e2e single container + parallel suites | Decisions 7–9 | e2e.sh harness; seeds untouched; workers:1 kept | PG-ADOPT-004 Success/Failure/Boundary | 1.3, 2.4, 3.2, 4.3 | One suite end-to-end via clone path; per-suite logs; aggregate names failures |
| PG-ADOPT-005 | Zero product-code change | Boundary | No C#/TSX/migration/API/envelope diff | PG-ADOPT-005 Success/Failure/Boundary | 1.4, 2.5, 3.3, 4.4 | git diff names test-infra + e2e.sh + feed only |
