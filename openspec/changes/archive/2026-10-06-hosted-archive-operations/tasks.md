## 1. BFS — Baseline and impact coverage
- [x] 1.1 Map every requirement and Success/Failure/Boundary scenario to src/Chronicleaf.Infrastructure/Hosted/, owned state/contracts, API/UI callers and HostedArchiveOperationsTests fixtures; confirm proposal/design/spec agree and record baseline.
- [x] 1.2 Confirm completed dependencies (cloud-object-storage, scheduled-project-sync, team-project-permissions, community-compose-release), C# .NET10 / React TypeScript boundary, shared-package adoption and every activation/blocker decision; do not start a blocked handoff or duplicate sibling code.

## 2. DFS — Requirement-by-requirement implementation
- [x] 2.1 Implement HOSTED-ARCHIVE-OPERATIONS-001 Verified Hosted archive recovery through its declared contracts, state/IO and applicable API/UI; add tests asserting its specified success, failure and boundary results before marking complete.
- [x] 2.2 Implement HOSTED-ARCHIVE-OPERATIONS-002 Explicit recoverable deletion through its declared contracts, state/IO and applicable API/UI; add tests asserting its specified success, failure and boundary results before marking complete.

## 3. BFS — Cross-surface regression and completeness
- [x] 3.1 Run HostedArchiveOperationsTests with the exact fixtures in design.md; verify scope, privacy, revision/concurrency, retry/cancel and output invariants across every changed caller, and audit the original impact map for omissions.
- [x] 3.2 Exercise dependency compatibility and retained preexisting history; test documented rollback/partial failure and verify no current-change placeholders, unauthorized side effects or unrelated sibling changes.

## 4. Verification
- [x] 4.1 Run native build and scoped real scenario tests from design.md; execute applicable UI/browser, PostgreSQL or provider-contract checks and local Gate. Record exact commands/results and unavailable prerequisites; no mock-only runtime claims.
- [x] 4.2 Run `node scripts/check-openspec-change-names.mjs`, `openspec validate --all --strict --no-interactive`, `git diff --check`, and review untracked/staged content and the mapped impact surface before archive.
- [x] 4.3 Only after implementation authorization and all scoped acceptance: archive without --skip-specs, create implementation/tests/archive commit, then HANDOFF.md-only pointer/evidence commit; exactly two commits, no conversational confirmation, stop and do not push.

Evidence (all run by the implementer, none taken on report):
- 1.1: requirements mapped — 001 to `ArchiveBackupService` + `archive_backup_manifests` + digest reader (no routes by design), 002 to `ArchiveRetentionService` + `archive_tombstones` + `DELETE/GET/POST-restore /api/projects/{id}/archive-data`; proposal/design/spec agree on operator-run recovery, 30-day window, byte-identical-or-fail, approval-bound retention.
- 1.2: all four dependencies archived at HEAD (`bdfcec9`, `bfa302a`, `0f47fca`, `5a40d46`); .NET 10 C# + no Web UI (scenarios API-observable, as prior CLOUD packages); no sibling edits, no shared `/lib`; journey revoke, schedule disable and media retire all go through delivered services.
- 2.1: `RecordAsync`/`MeasureAsync`/`RehearseAsync` over scoped canonical digests; schema/key/media/content all compared, any drift throws and keeps the old stamp; tests: success stamp, key/schema/media/content failures, unverified-record boundary, foreign-scope digest isolation.
- 2.2: `DeleteAsync` (confirm name + project revision, revokes live journey, disables enabled schedule, credentials/scopes untouched) / `RestoreAsync` (tombstone revision, inside window) / `HoldAsync`/`ClearHoldAsync` / `PurgeExpiredAsync` (window lapsed, no hold, no live approval/projection; retires unreferenced media via delivered store, retains the rest with receipt); tests: success with suspension proofs, guards (422/409/428/401/404), restore + expiry 410, foreign 404 without exposure, purge receipt, window skip, approval block + release, hold block + release.
- 3.1: `HostedArchiveOperationsTests` 17/17 against real disposable PostgreSQL + real API host (seed scope A/B, deterministic clock, real media root, real key file); every filtered gate stage asserted ≥1 test by `scripts/verify.sh`.
- 3.2: rollback is registration removal (no hosted loop, no transport); partial failure is retry-safe by sequencing (suspend first, tombstone last; retire before stamp); `dotnet build --nologo --no-incremental` 0 errors with the same 2 pre-existing warnings as HEAD; full `bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`) incl. `npm ci`, `tsc` + `vite build`; no Web UI touched so no new browser suite.
- 4.2: name preflight exit 0; `openspec validate --all --strict --no-interactive` 54/54; `git diff --check` clean; staged set reviewed (implementation + tests + migration + archive only; HANDOFF.md excluded from commit 1).
