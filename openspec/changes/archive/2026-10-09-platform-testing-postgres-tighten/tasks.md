## 1. BFS — Baseline and impact coverage

- [x] 1.1 Sibling API exact names read from the committed nupkg's XML
  docs: `StartAsync`, `EnsureTemplateSeededAsync`,
  `CreateIsolatedDatabaseAsync`, `BuildDatabaseConnectionString`,
  `ContainerId`, `ContainerName`, `Image`, `ServerConnectionString`,
  `IsTemplateSeeded`, plus `PostgresTestServerOptions` with
  `Image`/`Username`/`Password`/`MaintenanceDatabase` only —
  confirmed no `MaxConnections` knob exists; no sibling edit
  authorised.
- [x] 1.2 Polling-sleep baseline: 15 `await Task.Delay(100)` sites
  across 12 test classes (`BackgroundJobsApiHost.cs:327`,
  `GroundedMarkdownDraftsTests.cs:1500`, `GithubEvidenceTests.cs:1605`,
  `GithubGraphqlEnrichmentTests.cs:362, 975`, `LocalGitEvidenceTests.cs:1499`,
  `ScreenshotCaptureTests.cs:1640`, `ScreenshotCurationTests.cs:1045`,
  `ScreenshotEvolutionImagesTests.cs:858`, `EvolutionGifExportTests.cs:862`,
  `StaticBlogExportTests.cs:1055, 1238`, `HtmlContentExportTests.cs:823`,
  `AgencyWeeklyReportsTests.cs:1026`, `PublicProjectJourneyTests.cs:1056`).
  Two loops (`GithubEvidenceTests.AwaitErrorAsync`,
  `GithubGraphqlEnrichmentTests.AwaitErrorAsync`) lack the
  early-exit on terminal status — fixed in this change.
- [x] 1.3 Job-budget baseline: 19 test classes with `private static
  readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);`
  (`PublisherContractTests`, `PublisherDevtoAdapterTests`,
  `PublisherGhostAdapterTests`, `PublisherHashnodeAdapterTests`,
  `PublisherWordpressAdapterTests`, `PublisherGithubPagesAdapterTests`,
  `PublisherCustomBlogAdapterTests`, `ContentAssetVariantsTests`,
  `NarrativeMemoryTests`, `GroundedMarkdownDraftsTests`,
  `MediumImportPackageTests`, `XiaohongshuContentPackageTests`,
  `LinkedinCopyPackageTests`, `VideoStoryScriptsTests`,
  `HtmlContentExportTests`, `StaticBlogExportTests`,
  `MarkdownReviewExportTests`, `CommunityComposeReleaseTests`,
  `PublicProjectJourneyTests`) — 121 `OperationRows.AwaitAsync(...,
  JobBudget)` call sites total, all passing the 90s default regardless
  of the test's actual expectation.
- [x] 1.4 Connection-pool baseline: `ChronicleafPostgresFixture.CreateDatabaseAsync`
  applies `MaxPoolSize=4; MinPoolSize=0; ConnectionPruningInterval=1;
  ConnectionIdleLifetime=5` to the per-test connection string (predecessor
  change). The audit finds 65 `new NpgsqlConnection` sites in the
  assembly, all `await using` — zero counter-examples. The sibling
  exposes no `max_connections` knob (XML docs), so the bound stays
  structural on the Npgsql pool.
- [x] 1.5 Product-code boundary confirmed: no C# outside `tests/`, no
  TSX, no migration, no API, no envelope change in this package.

## 2. DFS — Requirement-by-requirement implementation

- [x] 2.1 PG-TIGHTEN-001: drop `await Task.Delay(100)` to
  `await Task.Delay(10)` in the canonical `OperationRows.AwaitAsync`
  poller (`BackgroundJobsApiHost.cs:327`); apply the same drop to every
  per-class `AwaitErrorAsync` / `AwaitStatus` helper that polls an
  operation row (`GithubEvidenceTests.cs:1605`,
  `LocalGitEvidenceTests.cs:1499`,
  `GithubGraphqlEnrichmentTests.cs:975`,
  `GroundedMarkdownDraftsTests.cs:1500`) and to every inline status
  loop (`ScreenshotCaptureTests.cs:1640`,
  `ScreenshotCurationTests.cs:1045`,
  `ScreenshotEvolutionImagesTests.cs:858`,
  `EvolutionGifExportTests.cs:862`, `StaticBlogExportTests.cs:1055,
  1238`, `HtmlContentExportTests.cs:823`,
  `AgencyWeeklyReportsTests.cs:1026`,
  `PublicProjectJourneyTests.cs:1056`). The
  `GithubGraphqlEnrichmentTests.cs:362` transport-arrival loop is
  unchanged (it polls a transport, not an operation row). Add an
  early-exit on terminal status
  (`if (last is not null && last.Status is "failed" or "succeeded"
  or "cancelled") break;`) BEFORE the `Task.Delay(10)` in
  `GithubEvidenceTests.AwaitErrorAsync` and
  `GithubGraphqlEnrichmentTests.AwaitErrorAsync`; the other helpers
  already have it.
- [x] 2.2 PG-TIGHTEN-002: keep the 90s `JobBudget` constant as the
  default safety net (the brief explicitly does not lower it). At each
  `OperationRows.AwaitAsync(..., JobBudget)` call site, replace `JobBudget`
  with a tighter per-call budget matching the test's actual
  expectation: `TimeSpan.FromSeconds(5)` for in-process operations,
  `TimeSpan.FromSeconds(15)` for jobs that hit a stubbed transport,
  `TimeSpan.FromSeconds(30)` only when an explicit backoff is being
  verified. The choice at each site is recorded in the call itself
  (the literal `TimeSpan.FromSeconds(N)` argument), so a reader of the
  test sees the budget without cross-referencing `JobBudget`.
- [x] 2.3 PG-TIGHTEN-003: extend the audit's two-class `ContainerId`
  equality proof + one-class `MigrationDelegateInvocations == 1` proof
  with the full unfiltered `dotnet test Chronicleaf.sln --no-build
  --nologo tests/Chronicleaf.Integration.Tests/` run as the actual
  end-to-end proof. Capture pass/fail counts, duration, container
  count, migration count.
- [x] 2.4 PG-TIGHTEN-004: add `Connection Lifetime=30` to the
  pool-self-trimming params in `ChronicleafPostgresFixture.CreateDatabaseAsync`
  (the predecessor change applied four params; this change adds a
  fifth); update the comment block to enumerate all five params and
  document the choice (sibling exposes no `max_connections` knob;
  the bound is structural on the Npgsql pool). Add a new oracle test
  `The_per_test_pool_string_carries_the_self_trimming_parameters` to
  `PostgresSharedServerProofTests.cs` that asserts all five pool
  params are present on the per-test connection string. Audit
  confirms zero `new NpgsqlConnection` without `await using` in the
  assembly.

## 3. BFS — Cross-surface regression and completeness

- [x] 3.1 Test surface: every migrated class still injects the
  fixture, still calls `CreateDatabaseAsync`, still has its
  `ProvisionedDatabaseAsync` helper return the cloned connection
  string; every polling loop still terminates on either target status
  or deadline; every `OperationRows.AwaitAsync` call site still passes
  a `within` argument that matches the test's expectation;
  `git diff --stat` names test-infra files only.
- [x] 3.2 Pool surface: per-test clone strings carry the five Npgsql
  pool params; the fixture's `ServerConnectionString` and
  `BuildDatabaseConnectionString` are untouched (the maintenance-database
  connection stays at default pool size because it is only used for
  administrative `CREATE DATABASE ... TEMPLATE` calls during the test,
  not for the per-test workload).
- [x] 3.3 Product surface: `git diff --stat` names
  `tests/Chronicleaf.Integration.Tests/*.cs` and the archived change
  only; no C# outside `tests/`.

## 4. Verification

- [x] 4.1 `node scripts/check-openspec-change-names.mjs` exit 0;
  `openspec validate --all --strict --no-interactive` green;
  `dotnet build Chronicleaf.sln --nologo` 0 errors; `git diff
  --check` clean.
- [x] 4.2 Full unfiltered `dotnet test Chronicleaf.sln --no-build
  --nologo tests/Chronicleaf.Integration.Tests/` green end-to-end
  with the exact pass/fail count and duration recorded (the proof
  is the full run, not a filtered subset; delta vs the 14m31s
  baseline recorded in HANDOFF).
- [x] 4.3 Container-count oracle green: the two existing proof
  tests plus the new stable-`ContainerId` test observe the same
  `Server.ContainerId` value across the assembly run.
- [x] 4.4 Migration-count oracle green: the existing probe test
  asserts `ChronicleafPostgresFixture.MigrationDelegateInvocations
  == 1`.
- [x] 4.5 Pool-string oracle green: the new
  `The_per_test_pool_string_carries_the_self_trimming_parameters`
  probe asserts all five pool params are present on the per-test
  connection string.
- [x] 4.6 `bash scripts/gate/dotnet-tests.sh` exit 0 (the same
  single unfiltered run used by the full gate's dotnet stage).
- [x] 4.7 Archive readiness: `openspec archive
  platform-testing-postgres-tighten --yes` (never `--skip-specs`);
  exactly two commits (no push); full gate deferred to batch end.
