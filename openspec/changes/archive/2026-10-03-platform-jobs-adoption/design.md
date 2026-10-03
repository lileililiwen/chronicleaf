# Design: Adopt Platform.Jobs as the durable scheduling boundary

## Context and implementation boundary

Repository `/home/paul/code/chronicleaf`, branch `main`. C# / .NET 10 (SDK 10.0.400), ASP.NET Core 10,
EF Core 10 + Npgsql, React + TypeScript + Vite. Only the durable operation scheduling boundary moves.

Files to change:

- `packages/platform-feed/` — add `Platform.Jobs.0.1.0.nupkg`, `Platform.Jobs.Hangfire.0.1.0.nupkg`
  from `/home/paul/code/dotnet-platform-libs/artifacts/packages/` (0.1.0, no `.snupkg`).
  `Platform.Core.0.1.0.nupkg` is already committed there.
- `scripts/pack-platform.sh` — add the two packages to the copy set.
- `scripts/verify-platform-feed.sh` — assert all five feed files, the pinned versions, and that only
  `Chronicleaf.Infrastructure` references `Platform.*`.
- `src/Chronicleaf.Infrastructure/Chronicleaf.Infrastructure.csproj` — add
  `<PackageReference Include="Platform.Jobs" Version="0.1.0" />` and
  `<PackageReference Include="Platform.Jobs.Hangfire" Version="0.1.0" />`; move the direct Hangfire
  references to the versions the platform pins (`Hangfire.Core`/`Hangfire.AspNetCore` 1.8.23,
  `Hangfire.PostgreSql` 1.21.1) or drop them where the platform package already supplies them.
- **Delete** `src/Chronicleaf.Application/Operations/IOperationDispatcher.cs` (the Chronicleaf-owned
  dispatcher contract and `LeaseHeartbeat`'s home is unaffected — see below) and
  `src/Chronicleaf.Infrastructure/Operations/HangfireOperationDispatcher.cs` plus its DI registration.
- `src/Chronicleaf.Application/Operations/OperationWorker.cs` — keep every claim/lease/heartbeat/
  commit rule; change only how it is reached (see Behavioral model).
- `src/Chronicleaf.Application/Operations/OperationService.cs` — depend on
  `Platform.Jobs.IJobDispatcher` instead of the local interface.
- `src/Chronicleaf.Api/Program.cs` — replace the Hangfire server/storage/dashboard registration with
  `AddPlatformHangfireJobs`, and register the payload handler.
- `tests/Chronicleaf.Integration.Tests/BackgroundJobsTests.cs`, `BackgroundJobsApiHost.cs` — host wiring
  only. **No assertion may change.**
- `Dockerfile` — the feed copy already carries these; confirm no path change is needed.

Must not change: the `operations` table and any migration; the `/api/operations/*` route shapes,
statuses and error codes; the outbox; every other `Platform.*` consumer; the media, projects, access
and persistence layers; `nuget.config`; the retry/lease/timeout constants in `OperationPolicy`.

## Language, ownership and release

- **Adopt** `Platform.Jobs` 0.1.0 and `Platform.Jobs.Hangfire` 0.1.0 by exact-version
  `PackageReference` from the committed repo-relative feed. No sibling source coupling, no
  environment-variable feed — Ruling 12 of the workspace `ARCHITECTURE.md`, already satisfied by
  `packages/platform-feed/`.
- **Keep local** the operation state machine: `Core/Operation.cs`, `IOperationStore`,
  `PostgresOperationStore`, `OperationWorker`, `OperationPolicy`. `.ai-rules/architecture.md` reserves
  Chronicleaf-owned state to Chronicleaf, and the platform exposes a scheduling boundary, not an
  operation-state contract. Deleting or rewriting them is out of scope.
- Release boundary: this changes no public API and no schema, so it ships as an internal dependency
  change. `Platform.Jobs` is not published to nuget.org and nothing in this change requires that.
- Test commands: `dotnet test Chronicleaf.sln --filter "FullyQualifiedName~BackgroundJobsTests"` and
  the full `CHRONICLEAF_E2E=1 bash scripts/verify.sh`.

## Contract and compatibility decisions

**Dispatcher.** `Platform.Jobs.IJobDispatcher.EnqueueAsync(JobPayload, CancellationToken)`.
`JobPayload.Create(name, arguments)` carries the name `"chronicleaf.operation"` and exactly one
argument, the operation id — no credential, no evidence, no payload body. Chronicleaf's local
`IOperationDispatcher` is deleted; `OperationService` takes `IJobDispatcher`. This is the single
enqueue path, so a duplicate-enqueue guard must still be enforced in the **claim**, not in the queue.

**Handler routing.** `Platform.Jobs.Hangfire.HangfireJobExecutor` is the Hangfire entry point. It
resolves the single registered `IJobPayloadHandler` in the job's DI scope and routes every payload to
it. Chronicleaf's `OperationWorker` implements `IJobPayloadHandler.HandleAsync(JobPayload, ct)`:
it reads the operation id out of `payload.Arguments`, runs the existing claim → heartbeat → execute →
commit sequence, and returns. The id arrives as a JSON `JsonElement`
(`HangfireJobExecutor.PayloadJsonOptions` is `JsonSerializerDefaults.Web`), so the handler must
convert it explicitly and reject a missing or non-Guid argument as a permanent failure rather than
guessing. Failures rethrow so Hangfire's retry model stays in charge, and the operation row — not the
queue — is the authority on attempt count.

**Clock.** `Platform.Core.Time.IClock` replaces the Chronicleaf-local clock in the operation layer
only: `SystemClock` in production, `FixedClock` in tests. `IJobDispatcher`'s documented requirement
that implementations read the time from `IClock` rather than `DateTimeOffset.UtcNow` is satisfied by
construction. Migrating the persistence/access/media clocks is explicitly **not** in this change.

**Dashboard.** `AddPlatformHangfireJobs` must be called with the dashboard disabled; where a guard is
wanted, use the platform's `CallbackDashboardAuthorizationFilter`. The observable requirement is that
the dashboard path is not reachable — verified as today, not by reading configuration.

**Readiness.** `HangfireStorageHealthCheck` is available but must **not** gate `/api/ready`.
COMMUNITY-PERSISTENCE-002 defines readiness as process plus database schema only, and that contract
was verified; job-engine health is reported separately, never as `schema_mismatch` or
`database_unavailable`.

**Version move.** Hangfire 1.8.21 → 1.8.23 to match the platform package's pins. A minor Hangfire
move may change retry/queue defaults, so the twelve job tests — which assert attempt counts, backoff
and exactly-once commit — are the compatibility proof, not a version-string check.

**Feed verification.** `scripts/verify-platform-feed.sh` gains the two new files and the
only-Infrastructure rule, and must still fail when the feed is removed (negative case already proven
for storage; re-prove it).

## Behavioral model and invariants

Unchanged from `background-jobs`, and the oracle is that the twelve tests pass **without editing an
assertion**:

| Rule | Mechanism | Must remain |
|---|---|---|
| Exactly-once result | single conditional `UPDATE` claim; a second worker's claim matches no row | one committed result under a two-worker race |
| Result commit | one statement conditional on still-running under this worker's lease token and on the input revision not having moved | a late or stale result is discarded |
| Attempt bound | `OperationPolicy.MaxAttempts` 3, backoff 5s then 30s, `Retry-After` clamped to 300s | exactly three attempts for an explicit transient failure |
| Permanent failure | permanent classification fails after one attempt | one attempt, redacted `error_code` |
| Lease | 180s lease, 15s heartbeat, expired lease recoverable exactly once | one recovery, never two |
| Cancellation | cooperative token; the result commit is conditional | terminal `cancelled`, late result discarded |
| Supersede | input revision moved before the result commit | `superseded`, no stale result attached |
| Idempotent enqueue | UNIQUE (scope_id, kind, idempotency_key) | identical payload returns the original; mismatched is a conflict |
| Payload privacy | one argument, the operation id | no credential or evidence ever reaches the queue |

`LeaseHeartbeat`'s corrected lifetime rule is retained exactly as committed: the source the object
hands out is created in the constructor and released only by `Stop()` under a gate that makes the
hand-off single. This was a real defect fixed in `5154b26`; do not reintroduce `using` on a source
that outlives the method.

## Failure, boundary and rollback policy

| Case | Behavior |
|---|---|
| Platform package missing from the feed | build fails `NU1301`; the feed check fails first. Never a fallback to the old dispatcher |
| Dispatch fails | operation stays `queued`; no result committed; the outbox may re-dispatch, and the claim still bounds execution to once |
| Payload argument missing or not a Guid | permanent failure, `error_code` redacted, no state guess |
| Hangfire 1.8.23 behaviour change | surfaces as a job-test failure, not a silent retry-count drift |
| Dashboard reachable | FAIL; the smoke asserts it is not |
| Readiness would start failing on job health | FAIL; readiness stays process + schema |
| Rollback | revert the csproj/DI/handler commit; no migration, so no data rollback. The feed files may be removed in the same revert |

## Verification oracle

Existing `BackgroundJobsTests` (12) pass **unchanged** against the platform adapter — that
unchanged-passing is the primary acceptance evidence, and editing an assertion to accommodate the
swap is a failure of this change, not a fix.

1. `dotnet build Chronicleaf.sln --nologo` — 0 warnings, 0 errors; no duplicate-assembly conflict from
   the Hangfire version move.
2. `dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~BackgroundJobsTests"` — 12/12,
   run **three times consecutively** to re-prove the lease and two-worker scenarios are not flaky.
3. `git diff -- tests/Chronicleaf.Integration.Tests/BackgroundJobsTests.cs` — **empty**. Any change
   there must be reported and justified before it is accepted.
4. Full `dotnet test Chronicleaf.sln` — 15 shell + 24 persistence + 23 access + 19 project/milestone +
   12 jobs + 9 media, 0 failed.
5. `CHRONICLEAF_E2E=1 bash scripts/verify.sh` — exit 0, including the extended feed stage and 21/21
   Playwright.
6. `bash scripts/verify-platform-feed.sh` — 0, with a **negative** run proving it exits nonzero when
   `packages/platform-feed` is absent.
7. Isolated no-sibling `/tmp` checkout with an empty `NUGET_PACKAGES` — build exit 0, 0 warnings;
   and `NU1301` exit 1 when only the feed is removed. SHELL-003 must still hold with the new packages.
8. `scripts/verify.sh` container smoke or an equivalent `docker build` + `docker run` — health 200,
   ready 200, anonymous operations 401, dashboard not reachable.
9. `git diff --check`; `node scripts/check-openspec-change-names.mjs` exit 0;
   `openspec validate --all --strict --no-interactive` 53/53 before and after archive.

## Decision ledger and activation gates

Resolved: the platform packages exist and are consumable (verified in
`dotnet-platform-libs/artifacts/packages` and the nuspec dependency graph). The feed mechanism is
decided by Ruling 12; the `NUGET_PLATFORM_FEED` variable stays rejected. Adoption is authorized by the
owner on 2026-10-03 as a dedicated follow-up change, with the existing guarantees re-proved rather
than assumed.

Deferred: recurring jobs and `IRecurringJobRegistry` (no consumer until the media orphan reconciler
needs a schedule); `IClock` migration for the persistence, access and media layers; publishing
`Platform.*` to a public feed.

Assumption to check, not assume: that `AddPlatformHangfireJobs` can be configured for PostgreSQL
storage in Chronicleaf's existing schema. If it cannot, that is a **blocker** — report it and stop
rather than reimplementing the adapter locally, which is the deviation this change exists to remove.

Not a blocker: if a new EF migration appears necessary, report it as scope error before adding it.

## Requirement traceability

| Requirement | Design decision | Implementation | Scenarios | Tasks | Oracle |
|---|---|---|---|---|---|
| PLATFORM-JOBS-ADOPTION-001 Platform-owned dispatch | Dispatcher/Handler routing/Version move | `Chronicleaf.Infrastructure.csproj`, `Program.cs`, `OperationWorker`, `OperationService` | Success, Failure, Boundary | 2.1, 3.1 | Tests 1, 2, 3, 4, 5, 7 |
| PLATFORM-JOBS-ADOPTION-002 Chronicleaf-owned operation state | Keep local, Readiness, Version move | `OperationWorker`, `PostgresOperationStore`, `OperationPolicy` (unchanged) | Success, Failure, Boundary | 2.2, 3.1 | Test 3 (empty diff), 4, 5, 8 |
| PLATFORM-JOBS-ADOPTION-003 Reproducible platform dependency | Feed, Feed verification | `packages/platform-feed/`, `scripts/pack-platform.sh`, `scripts/verify-platform-feed.sh` | Success, Failure, Boundary | 2.3, 3.2 | Tests 6, 7, 9 |
