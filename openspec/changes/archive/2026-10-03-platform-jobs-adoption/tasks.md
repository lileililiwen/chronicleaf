## 1. BFS — Baseline and impact coverage
- [x] 1.1 Map every PLATFORM-JOBS-ADOPTION requirement and Success/Failure/Boundary scenario to the files and symbols named in design.md, the callers of the current `IOperationDispatcher`/`HangfireOperationDispatcher`, the 12 `BackgroundJobsTests`, the operations API and the operations UI; confirm proposal/design/spec agree and record the baseline.
- [x] 1.2 Confirm the completed dependency `background-jobs` (commit `5154b26`), C# .NET 10 / React TypeScript boundary, Ruling 12 feed mechanism, and the `AddPlatformHangfireJobs`-can-configure-PostgreSQL assumption; if it cannot, report the blocker and stop rather than reimplementing the adapter locally.

## 2. DFS — Requirement-by-requirement implementation
- [x] 2.1 Implement PLATFORM-JOBS-ADOPTION-001: commit the two platform packages to the feed, add the pinned references, delete the Chronicleaf dispatcher contract and adapter, make `OperationWorker` an `IJobPayloadHandler`, switch `OperationService` to `Platform.Jobs.IJobDispatcher`, and replace the Hangfire wiring with `AddPlatformHangfireJobs` using the platform dashboard guard.
- [x] 2.2 Implement PLATFORM-JOBS-ADOPTION-002: move the operation layer to `Platform.Core.Time.IClock`, keep the claim/lease/heartbeat/result-commit/supersede rules byte-for-byte in effect, keep `/api/ready` free of job-engine health, and prove the dashboard is unreachable.
- [x] 2.3 Implement PLATFORM-JOBS-ADOPTION-003: extend `scripts/pack-platform.sh` and `scripts/verify-platform-feed.sh` for all five feed files and the only-Infrastructure rule, and re-prove the negative case.

## 3. BFS — Cross-surface regression and completeness
- [x] 3.1 Run `BackgroundJobsTests` three consecutive times with **zero** changes to that file, and confirm the full suite (15 shell, 24 persistence, 23 access, 19 project/milestone, 12 jobs, 9 media) still passes; audit the original impact map for omissions and for any placeholder or duplicated assembly introduced by the Hangfire version move.
- [x] 3.2 Exercise the boundary cases the swap could break: duplicate enqueue still bounded by the claim, dashboard unreachable, readiness unaffected by job health, payload privacy, and the isolated no-sibling build in both directions.

## 4. Verification
- [x] 4.1 Run `dotnet build`, the three consecutive job-test runs, the full suite, the extended feed check and its negative run, the isolated no-sibling build, and the container smoke; execute the Playwright stage and the local Gate. Record exact commands, exit codes and any unavailable prerequisite; no mock-only runtime claims.
- [x] 4.2 Run `node scripts/check-openspec-change-names.mjs`, `openspec validate --all --strict --no-interactive`, `git diff --check`, and review the mapped impact surface and the `git diff` of the 12 test file before archive.
- [ ] 4.3 Only after implementation authorization and all scoped acceptance: archive without --skip-specs, create implementation/tests/archive commit, then HANDOFF.md-only pointer/evidence commit; exactly two commits, no conversational confirmation, stop and do not push.
