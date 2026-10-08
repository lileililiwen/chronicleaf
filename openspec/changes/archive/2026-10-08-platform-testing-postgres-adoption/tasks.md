## 1. BFS — Baseline and impact coverage

- [x] 1.1 Feed baseline: `scripts/pack-platform.sh` `PACKAGES` (6 entries),
  `packages/platform-feed/` (6 nupkgs), `scripts/verify-platform-feed.sh`
  (expected list + Infrastructure pins + cold restore), test-csproj
  references. Sibling nupkg + nuspec + XML docs inspected; API names taken
  from the docs, never guessed.
- [x] 1.2 Dotnet baseline: `ChronicleafPostgresFixture.cs` (per-class
  container, `CreateDatabaseAsync` + static `MigrateAsync` per test),
  `IClassFixture` in ~30 classes, `AssemblyInfo.cs` parallelization off,
  `MinioFixture.cs` consumers counted (exactly one — stays per-class).
- [x] 1.3 e2e baseline: `start_suite`/`stop_suite` per-suite `docker run` +
  `pg_isready` + `dotnet ef database update` + install INSERT, 30 serial
  suites, `workers: 1` in `playwright.config.ts`, token-cleanup list
  missing candidates/narrative/variants.
- [x] 1.4 Product-code boundary confirmed: no C# outside `tests/`, no TSX,
  no migration, no API, no envelope change in this package.

## 2. DFS — Requirement-by-requirement implementation

- [x] 2.1 PG-ADOPT-001: add `Platform.Persistence.Postgres.Testing` to
  `PACKAGES`, run `scripts/pack-platform.sh`, commit the new `.nupkg`;
  pin `0.1.0` in the test project; extend `verify-platform-feed.sh`
  (expected list + pin + cold restore).
- [x] 2.2 PG-ADOPT-002: rewrite the fixture as a thin adapter holding ONE
  `PostgresTestServer` (pinned image, template seeded once via existing
  EF `MigrateAsync` delegate); add the `CollectionDefinition`; migrate
  ~30 classes from `IClassFixture` to the collection (constructor
  injection unchanged); add proof tests asserting shared server identity
  across two test classes.
- [x] 2.3 PG-ADOPT-003: per-test DBs via `CreateIsolatedDatabaseAsync`
  (GUID names kept); retain per-test `MigrateAsync` as no-op guards;
  assertions byte-identical; `MinioFixture` verdict recorded (unchanged).
- [x] 2.4 PG-ADOPT-004: rewrite the `e2e.sh` harness (ONE container, seed
  template migrated once, per-suite `TEMPLATE` clones + install INSERT,
  N-way parallel runner with `CHRONICLEAF_E2E_JOBS`, per-suite logs,
  aggregated failures, completed token cleanup); seeds untouched;
  `workers: 1` kept with the boundary stated.
- [x] 2.5 PG-ADOPT-005: diff audit proves zero product-code change.

## 3. BFS — Cross-surface regression and completeness

- [x] 3.1 Dotnet surface: every migrated class still injects the fixture
  and keeps its assertions; no `IClassFixture<ChronicleafPostgresFixture>`
  remains; `AssemblyInfo.cs` unchanged; no production project references
  the testing package.
- [x] 3.2 e2e surface: per-suite DB names/ports/tokens/servers unchanged;
  seeds write into the clone they always wrote into; no suite shares
  state; cleanup removes the one container, all token files, and the key.
- [x] 3.3 Product surface: `git diff --stat` names test infra, `e2e.sh`,
  feed, and the archived change only.

## 4. Verification

- [x] 4.1 `node scripts/check-openspec-change-names.mjs` exit 0;
  `openspec validate --all --strict --no-interactive` green;
  `dotnet build Chronicleaf.sln --nologo` 0 errors; `git diff --check`
  clean.
- [x] 4.2 Proof (a): `dotnet test --filter` on exactly
  `MediumImportPackageTests` + `MilestoneTimelineTests` plus the proof
  tests — green, container count 1, migration replay 1.
- [x] 4.3 Proof (b): exactly ONE e2e suite end-to-end (smallest) via the
  template-clone path — green with single-suite time recorded.
- [x] 4.4 Archive readiness: `openspec archive
  platform-testing-postgres-adoption --yes` (never `--skip-specs`);
  exactly two commits (no push); full gate deferred to batch end.
