# Proposal: Adopt Platform.Persistence.Postgres.Testing as the shared test-database boundary (platform-testing-postgres-adoption)

## Why

Every persistence test in `tests/Chronicleaf.Integration.Tests` boots its own
PostgreSQL container: `ChronicleafPostgresFixture` is an `IAsyncLifetime`
per-class fixture (`IClassFixture` in ~30 test classes), so each class pays a
full container start plus a full `CreateDatabaseAsync` + static `MigrateAsync`
replay of the committed EF migrations per test. The same waste repeats in
`scripts/e2e.sh`: `start_suite`/`stop_suite` do a per-suite `docker run` +
`pg_isready` poll + `dotnet ef database update` + install INSERT, and 30 suites
run serially. That cost is pure overhead: the tests assert product behavior,
not container boot, and the suites assert per-installation isolation, not
per-installation PostgreSQL binaries.

The premise that made this waste structural — that no shared fixture exists —
is now wrong: `Platform.Persistence.Postgres.Testing` 0.1.0 is real, built,
and published at
`/home/paul/code/dotnet-platform-libs/artifacts/packages/Platform.Persistence.Postgres.Testing.0.1.0.nupkg`.
It boots ONE pinned-image container per test assembly
(`PostgresTestServer`, held in an xUnit `ICollectionFixture`), migrates ONCE
into a template database via a caller-supplied delegate
(`EnsureTemplateSeededAsync`), and clones per test
(`CreateIsolatedDatabaseAsync` returning the clone's connection string), so
per-test cost drops to a `CREATE DATABASE ... TEMPLATE` while GUID database
names keep uniqueness. The same template-clone discipline ports directly to
`e2e.sh`: one container, one migrated seed template, per-suite clones, suites
run N-way parallel.

## What Changes

- `packages/platform-feed/` gains
  `Platform.Persistence.Postgres.Testing.0.1.0.nupkg` (its nuspec closure is
  `Npgsql`, `Testcontainers.PostgreSql`,
  `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions` — all nuget.org packages, so no
  further feed entry is needed). `scripts/pack-platform.sh` lists it in
  `PACKAGES`; `scripts/verify-platform-feed.sh` asserts it with the same
  exact-version enforcement. The test project references it pinned at `0.1.0`.
- `ChronicleafPostgresFixture.cs` becomes a thin adapter over the platform
  package (kept file and class name, kept constructor injection, kept static
  helpers byte-identical): it holds ONE `PostgresTestServer`, starts it from
  the pinned `postgres:17.6-alpine` image, seeds the template once with the
  existing EF `MigrateAsync` as the delegate, and serves per-test databases
  via `CreateIsolatedDatabaseAsync` with the existing GUID names. All ~30
  test classes move from `IClassFixture` to one `ICollectionFixture` +
  `CollectionDefinition`; every assertion stays byte-identical.
- `scripts/e2e.sh` boots ONE postgres container, migrates ONE seed template
  database, clones per suite (`CREATE DATABASE ... TEMPLATE seed` + install
  INSERT, per-suite DB names/ports/tokens/servers unchanged), and runs suites
  N-way parallel (`N=4` default, `CHRONICLEAF_E2E_JOBS` overridable) with
  per-suite logs and aggregated failure reporting naming the failed suite(s).
  Playwright keeps `workers:1` inside suites. The token-file cleanup list is
  completed (candidates/narrative/variants were missing) and stays complete.
- `MinioFixture` is inspected and left per-class: exactly one class consumes
  it, so there is no repetition to remove.

## Package Boundary and Split Assessment

Single outcome: the same tests and suites run against shared containers and
template-cloned databases, with isolation and assertions unchanged.

Included: the feed entry + pack/verify script updates, the fixture adapter +
collection migration + proof tests, the `e2e.sh` single-container/template/
parallel rewrite, and the Minio inspection verdict. Excluded: any product-code
change (no C#, no TSX, no API, no schema, no envelope, no behavior change);
any sibling edit; any new product workflow.

Split signals considered and rejected: the feed entry and the fixture swap
are not independently verifiable (a feed nothing references proves nothing);
the dotnet and e2e halves share one outcome (stop paying per-database
migration cost) and one acceptance oracle (same assertions, same isolation,
less wall-clock). Migrating Minio separately is rejected because a single
consumer is not shared infrastructure.

Why this is the smallest independently verifiable unit: one actor (the test
and e2e harness), one lifecycle (provision an isolated database), one
acceptance oracle (existing assertions green, isolation intact, container and
migration counts down).

Dependencies: none new (all product packages archived). It is not a
dependency for any future package.

## Sibling and Shared Architecture Reconnaissance

| Candidate | Evidence path/symbol | Reusable code/config/architecture | Compatibility gap | Owner and release boundary | Decision |
|---|---|---|---|---|---|
| `Platform.Persistence.Postgres.Testing` 0.1.0 | nupkg + XML docs: `PostgresTestServer.StartAsync(PostgresTestServerOptions, ct)` (pinned image, default `postgres:17.6-alpine`, password overridable via `PostgresTestServerOptions`), `EnsureTemplateSeededAsync(Func<string, CancellationToken, Task> migrateTemplateAsync, string templateDatabase = "platform_template", ct)` (delegate runs `MigrateAsync` and disposes connections before returning), `CreateIsolatedDatabaseAsync(string? databaseName, ct)` (template clone when seeded, empty DB otherwise), `ContainerId`/`ServerConnectionString`/`IsTemplateSeeded` | One assembly-shared container; once-migrated template; per-test `TEMPLATE` clones | None: the delegate seam takes the existing EF `MigrateAsync` verbatim; GUID names already satisfy the `DatabaseNamePattern` (`^[A-Za-z_][A-Za-z0-9_$]{0,62}$`, current names are 40 chars, letter-led, underscore/hex only) | dotnet-platform-libs, 0.1.0, consumed by exact-version PackageReference | **ADOPT** |
| `Platform.Core` 0.1.0 | already in the feed as a transitive dependency | nothing this change needs | none | dotnet-platform-libs, 0.1.0 | no action |
| Chronicleaf's own `ChronicleafPostgresFixture` container lifecycle | `tests/Chronicleaf.Integration.Tests/ChronicleafPostgresFixture.cs` (`PostgreSqlBuilder`, per-class start, `CreateDatabaseAsync` + static `MigrateAsync` per test) | Kept as a thin adapter: same class name, same constructor injection, same static helpers (`MigrateAsync`, `ExecuteAsync`, `CountRowsAsync`, `ReadTextAsync`, `CreateContext`) so ~30 call sites change only their xUnit attribute | Per-class boot is the waste being removed; nothing else in the repo should copy this pattern after adoption | Chronicleaf | **keep local as adapter**: deleting the file would rewrite every call site for no behavioral gain |
| `MinioFixture` per-class lifecycle | `tests/Chronicleaf.Integration.Tests/MinioFixture.cs`, single consumer (`IClassFixture<MinioFixture>` in one class) | none — a single consumer is not shared infrastructure | none | Chronicleaf | **keep local, unchanged** |
| `scripts/e2e.sh` per-suite `docker run` + `dotnet ef database update` | `start_suite`/`stop_suite`, 30 serial suites | Replaced in-repo: one container, one migrated seed, per-suite `TEMPLATE` clones, N-way parallel runner | Intra-suite Playwright parallelism stays at `workers:1`: empty-state claims sharing one DB would break under parallel workers | Chronicleaf | **rework local**: no sibling equivalent exists |

No extraction is proposed: the shared owner already exists and is being
consumed, so this change adopts rather than extracts. Editing
`dotnet-platform-libs` is explicitly out of scope.

Ruling-12 feed mechanics (workspace ARCHITECTURE.md): the ONLY place a
sibling path may appear is `scripts/pack-platform.sh` (maintenance only,
never invoked by build/restore/verification); restore resolves from
`nuget.config` plus the `.nupkg` files committed under
`packages/platform-feed/`; no environment-variable feed; the testing package
is referenced by the test project only — Core/Application/Api stay feed-free
and production projects must not reference it (its nuspec description states
this; the verify script enforces the production side as today).

## BFS Impact Map

- Actor/flow: no product actor; the test harness and the e2e runner are the
  only consumers. Product behavior is unchanged by construction.
- Boundary/callers: `scripts/pack-platform.sh` (`PACKAGES` list),
  `packages/platform-feed/` (one new `.nupkg`),
  `scripts/verify-platform-feed.sh` (expected list + test-project pin),
  `tests/Chronicleaf.Integration.Tests/ChronicleafPostgresFixture.cs`
  (thin adapter), `AssemblyInfo.cs` (unchanged: collection serializes; the
  disable-parallelization line stays), ~30 test classes (attribute-only
  change), one new proof-test file (two classes asserting shared server
  identity), `scripts/e2e.sh` (harness rewrite; seed functions untouched),
  `src/Chronicleaf.Web/playwright.config.ts` (untouched, `workers: 1` kept).
- Data/integration: no schema, no migration, no API, no envelope change; the
  template delegate runs the EXISTING committed migrations once.
- Verification: light only — name preflight, strict validate, `dotnet build`,
  `dotnet test --filter` on the two smallest fixture-using classes plus the
  proof tests, ONE e2e suite end-to-end (smallest). Full gate runs once at
  batch end by the coordinator, not here.
- Privacy/compatibility: no credentials committed; test passwords stay
  test-only constants; e2e tokens stay run-ephemeral files removed on exit.

## Capabilities

### New Capabilities

- `platform-testing-postgres-adoption`: Shared-container, template-cloned
  test databases for dotnet and e2e, adopted from the published platform
  package with isolation and assertions unchanged.

## Non-goals

Product-code changes of any kind (C#, TSX, migrations, routes, envelope,
approval bindings, evidence rules); new product workflows; touching
`dotnet-platform-libs`; touching HANDOFF.md/ROADMAP.md/.gitignore beyond the
coordinator-owned evidence row; pushing; running the full gate or full
`e2e.sh` here.
