# Proposal: Adopt Platform.Jobs as the durable scheduling boundary

## Why

`background-jobs` (commit `5154b26`) was implemented against the premise that `Platform.Jobs` was
unavailable, and therefore took a raw `Hangfire.Core` / `Hangfire.PostgreSql` dependency plus a
Chronicleaf-owned `IOperationDispatcher` that shadows `Platform.Jobs.IJobDispatcher`. That premise
was wrong: `Platform.Jobs` and `Platform.Jobs.Hangfire` 0.1.0 are real, built, and available in
`dotnet-platform-libs/artifacts/packages`, and `openspec/config.yaml` already states the intent —
*"Consume published Platform.Storage/Local/S3 and Platform.Jobs, no sibling source coupling."*
`background-jobs`' own design said to adopt them.

The same mistake was already corrected once: `local-media-storage` now runs on the genuine
`Platform.Storage` / `Platform.Storage.Local` through a committed repo-relative feed. This change
finishes that correction for jobs, so Chronicleaf consumes the shared platform boundary rather than
carrying a private reimplementation of a contract the workspace already owns.

The existing guarantees are proven and must survive: exactly-one committed result under a two-worker
race, a three-attempt bound with the fixed backoff, lease expiry recoverable exactly once,
cooperative cancellation discarding a late result, and revision supersede. The platform owns
*scheduling*; Chronicleaf keeps owning *operation state*.

## What Changes

- `packages/platform-feed/` gains `Platform.Jobs.0.1.0.nupkg` and `Platform.Jobs.Hangfire.0.1.0.nupkg`.
- `src/Chronicleaf.Infrastructure` references both, pinned `0.1.0`. Core/Application/Api stay feed-free.
- The Chronicleaf-owned `IOperationDispatcher` and its Hangfire wiring are **removed**; the platform
  `IJobDispatcher` (`HangfireJobDispatcher`, registered by `AddPlatformHangfireJobs`) becomes the only
  enqueue path.
- Chronicleaf's operation worker becomes a `Platform.Jobs.Hangfire.IJobPayloadHandler`, so the
  platform's `HangfireJobExecutor` routes dispatched payloads into it inside a real job scope.
- Chronicleaf's clock becomes `Platform.Core.Time.IClock` (`SystemClock` in production,
  `FixedClock` in tests), replacing the locally defined clock abstraction.
- The Hangfire dashboard guard uses the platform's `CallbackDashboardAuthorizationFilter`, and
  `HangfireStorageHealthCheck` reports storage readiness.
- Hangfire moves 1.8.21 → 1.8.23, the version the platform packages pin, and the owned
  `OperationWorker` / `PostgresOperationStore` guarantees are re-proved unchanged.
- `scripts/verify-platform-feed.sh` extends to assert the two new feed files and that no
  Chronicleaf project references a `Platform.*` package outside Infrastructure.

## Package Boundary and Split Assessment

Single outcome: the durable scheduling boundary is the platform's, with every previously verified
operation guarantee intact.

Included: the two packages, the dispatcher swap, the payload-handler routing, the clock, the
dashboard guard, the storage health check, the Hangfire version move, the feed, and re-proving the
twelve existing `BackgroundJobsTests` plus their API and UI surface.

Excluded: changing any operation-state rule (claim, lease, heartbeat, retry bound, cancel, supersede)
— those are `background-jobs` behaviour and are not re-litigated here; recurring jobs, since
`IRecurringJobRegistry` is unused until the media orphan reconciler needs a schedule; any change to
the `POST /api/operations/{id}` API shape; and provider adapters.

Split signals considered and rejected: the feed addition and the dispatcher swap are not
independently verifiable, because a feed that resolves a package nothing references proves nothing;
adopting the dispatcher and the payload handler together is one contract with one acceptance
boundary. Adopting the clock alone would be independently testable but leaves the dispatcher wrong,
and separating recurring jobs is required because nothing consumes it yet.

Why this is the smallest independently verifiable unit: one actor (the Community install), one
lifecycle (dispatch and execute a persisted operation), one acceptance oracle (the twelve existing
job tests, unchanged, passing against the platform adapter).

Dependencies: `background-jobs` (archived). It is not a dependency for any future package, so
nothing else waits on this except that leaving it undone keeps a known deviation in shipped code.

## Sibling and Shared Architecture Reconnaissance

| Candidate | Evidence path/symbol | Reusable code/config/architecture | Compatibility gap | Owner and release boundary | Decision |
|---|---|---|---|---|---|
| `Platform.Jobs` 0.1.0 | `src/Platform.Jobs/IJobDispatcher.cs`, `JobPayload.cs`, `BackgroundJobsOptions.cs`, `IClock` from `Platform.Core/Time/` | `IJobDispatcher.EnqueueAsync(JobPayload, ct)`; `JobPayload.Create(name, args)`; `BackgroundJobs` options section; `IClock`/`SystemClock`/`FixedClock` | Chronicleaf's own `IOperationDispatcher` duplicates the dispatcher role and its local clock duplicates `IClock` | dotnet-platform-libs, 0.1.0, consumed by exact-version PackageReference | **adopt** |
| `Platform.Jobs.Hangfire` 0.1.0 | `HangfireJobDispatcher.cs`, `HangfireJobExecutor.cs`, `IJobPayloadHandler.cs`, `ScopedJobActivator.cs`, `CallbackDashboardAuthorizationFilter.cs`, `HangfireStorageHealthCheck.cs`, `DependencyInjection/ServiceCollectionExtensions.cs` | Hangfire-backed `IJobDispatcher`; app-owned `IJobPayloadHandler` routing; DI scope activation; dashboard authorization filter; storage health check; `AddPlatformHangfireJobs` that never overwrites an app-owned dispatcher | Pins Hangfire 1.8.23 against Chronicleaf's current 1.8.21; Chronicleaf has no `IJobPayloadHandler` and its worker is invoked by its own entry point | dotnet-platform-libs, 0.1.0 | **adopt** |
| `Platform.Core` 0.1.0 | already in the feed as a transitive dependency of `Platform.Storage` | `IClock` is the shared time contract | Chronicleaf defines its own clock abstraction used by persistence, access, media and jobs | dotnet-platform-libs, 0.1.0 | **adopt** for jobs and the operation layer; migrating every other consumer's clock is out of scope |
| Chronicleaf's own `OperationWorker` / `PostgresOperationStore` | `src/Chronicleaf.Application/Operations/`, `src/Chronicleaf.Infrastructure/Operations/` | The exactly-once claim, lease, heartbeat, result commit and supersede rules | None — the platform exposes a scheduling boundary, not operation state | Chronicleaf | **keep local**: this is Chronicleaf-owned state per `.ai-rules/architecture.md`, and the platform has no equivalent contract to adopt |

No extraction is proposed: the shared owner already exists and is being consumed, so this change
adopts rather than extracts. Editing `dotnet-platform-libs` is explicitly out of scope.

## BFS Impact Map

- **Capabilities affected:** background operation execution only. Readiness, access, projects,
  milestones, media and the static shell are untouched except that they share the solution build.
- **Contracts:** Chronicleaf's `IOperationDispatcher` is deleted in favour of
  `Platform.Jobs.IJobDispatcher`. `OperationService` gains a dependency on the platform interface;
  the public `/api/operations/{id}` and `/api/operations/{id}/cancel` routes, their statuses and
  their error envelope are unchanged.
- **Data and persistence:** the `operations` table, its indexes, the outbox and every migration are
  **unchanged** — this is an adapter swap, not a schema change. No new migration is expected; if one
  is required, that is a scope error and must be reported rather than smuggled in.
- **Integrations and configuration:** Hangfire 1.8.21 → 1.8.23; `BackgroundJobs`/`HangfireJobs`
  options sections; `ConnectionStrings__Chronicleaf` unchanged; the dashboard stays disabled.
- **Callers:** the outbox dispatcher, `OperationService`, `Program.cs` registration, the DI
  composition root, the operations API, `OperationsPanel`, and the 21 existing Playwright fixtures.
- **Failure and boundary behavior:** a dispatch failure, a lost lease, an exhausted retry bound, a
  cancellation and a superseded revision all keep their current observable outcome. The dashboard
  must remain unreachable. Readiness must still be process/database-only and must not start
  reporting job-engine health as a reason to be unready.
- **Dependencies:** `Platform.Jobs` → `Platform.Core`; `Platform.Jobs.Hangfire` → `Platform.Jobs` +
  Hangfire 1.8.23 + `Hangfire.PostgreSql` 1.21.1. All resolve from the committed feed.
- **Security/privacy:** unchanged — payloads still carry only the operation id, never a credential or
  evidence, and no new configuration key exposes a secret.
- **Compatibility:** `BackgroundJobsTests` (12), the operations API fixtures and the operations UI
  fixtures must pass **unchanged**; that unchanged-passing is the primary oracle.

## Capabilities

### New Capabilities
- `platform-jobs-adoption` — the platform owns enqueue, routing and dashboard guard; Chronicleaf
  keeps owning operation state and its guarantees.

## Non-goals

- Re-designing any operation-state rule, retry bound, lease duration or timeout.
- Recurring jobs, the media orphan reconciler schedule, and `IRecurringJobRegistry`.
- Migrating the clock of the persistence, access or media layers to `IClock`.
- Any GitHub, evidence, capture, AI, draft or export surface.
- Publishing `Platform.*` to nuget.org, or editing `dotnet-platform-libs`.
