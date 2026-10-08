# Design: Verify gate adoption (verify-gate-adoption)

## Context and implementation boundary
Repository Chronicleaf only. Primary boundary `.ai-gate/gate.yaml` (new),
`scripts/gate/` (new: six check wrappers plus the attribution parser plus
the adoption test), `scripts/verify.sh` (wrapper rewrite, header updated),
`src/Chronicleaf.Web/tests/frontend-depth-axe.spec.ts` (new). Read-only
neighbors: `scripts/verify-platform-feed.sh`,
`scripts/check-web-scaffolding.sh`, `scripts/e2e.sh`, the six UI suites and
the Web build chain — reused as invoked commands, never modified. Depends
on the sibling `driftwatchdog` binary as an operator prerequisite;
adoption only, no vendoring. Preserve forge.yaml, .standard receipts,
unrelated files, ROADMAP.md, .gitignore and the archived specs. No C#, no
TSX, no migration, no API edit.

## Language, ownership and release
Shell for `scripts/gate/*.sh` (`set -euo pipefail`, repository-relative
paths, no absolute sibling paths per Ruling 12); TypeScript/Playwright for
the one new axe spec, following the form-feedback harness (in-spec vite
dev server, exact pathname+method `/api/*` route mocks, synthetic actor
id). Check contracts live in the manifest; the runner stays in the sibling
repository. No package install beyond the already-pinned `@axe-core/playwright`.

**ADOPT, never fork.** Gate plan resolution, execution, blocking policy,
run history and evidence export belong to driftwatchdog. Chronicleaf owns
its six check commands and its manifest policy. Evidence inspected:
sibling `README.md` integration recipe and business-project policy,
`sibling skills/driftwatchdog-gate/SKILL.md` gate path, sibling
`templates/deployable/gate.toml` starter. No runner code enters this repo.

## Contract and compatibility decisions
The manifest declares a project-defined profile (`chronicleaf-gate`) so the
gate schedules exactly the six declared checks, each bound under
`commands:` — an unbound required check would be `UNBOUND` and fail, so
every declared check has a command. `blocking = [FAIL, REVIEW_REQUIRED]`.
Per-check deadlines under `timeouts:` (dotnet, frontend-depth and e2e at
the 1h maximum; a timeout reports `REVIEW_REQUIRED`, never a silent pass).
`driftwatchdog gate --dry-run` prints the resolved plan and manifest digest
without executing anything and is the manifest oracle.

Check inventory (manifest id → command → pre-existing stage it replaces):
- `platform-feed` → `bash scripts/gate/platform-feed.sh` → the
  `verify-platform-feed.sh` first stage, unchanged semantics.
- `web-scaffolding-lint` → `bash scripts/gate/web-scaffolding-lint.sh` →
  the `check-web-scaffolding.sh` source stage (bundle half stays covered by
  the frontend-build check producing `dist` first; gate order is manifest
  order).
- `dotnet` → `bash scripts/gate/dotnet-tests.sh` → ONE `dotnet build
  Chronicleaf.sln --nologo` then ONE unfiltered `dotnet test
  Chronicleaf.sln --no-build --nologo`, output tee'd to a log; the parser
  reports per-class attribution. The `assert_filter_matches` guard and all
  per-stage `--filter`/`--list-tests` invocations are deleted with the old
  stages: an unfiltered run cannot silently select nothing.
- `frontend-build` → `bash scripts/gate/frontend-build.sh` → `npm ci` +
  `npx tsc --noEmit` + `npx vite build` in `src/Chronicleaf.Web`.
- `frontend-depth` → `bash scripts/gate/frontend-depth.sh` → the new deep
  check (decision 5).
- `e2e-browser` → `bash scripts/gate/e2e-browser.sh` → `bash
  scripts/e2e.sh` when `CHRONICLEAF_E2E=1`, else an explicit SKIP note with
  exit 0 (decision 6).

`scripts/verify.sh` keeps its path and contract (`bash scripts/verify.sh`
exits nonzero on any failing check) and becomes: strict mode, repo-root
resolution, `exec driftwatchdog gate "$@"`. Environment (`CHRONICLEAF_E2E`,
runner flags) flows through untouched. `.project.json`
`verification.command` is unchanged and stays truthful.

## Behavioral model and invariants
- One manifest, one runner, one dotnet run. The solution test suite
  executes exactly once per gate run; failure attribution comes from
  parsing, not from scheduling.
- The attribution parser is generic: it groups the single run's `Failed
  <fully-qualified>.<test> [...]` lines by test-class (method segment
  stripped) and prints `count class` rows; it hardcodes no class-to-package
  map, so a renamed class can never silently fall out of attribution. It
  exits nonzero iff the log holds at least one failure line, and the
  `dotnet-tests.sh` wrapper propagates the test run's own exit code — the
  parser attributes, it never verdicts.
- The depth check never duplicates the e2e harness: the three standalone
  suites plus the new axe spec execute inside `frontend-depth.sh` with no
  server and no database; the three app-backed suites execute inside the
  `e2e-browser` check, and the depth script proves that wiring with a grep
  oracle over `scripts/e2e.sh` (`run_suite` naming each of the three
  files) rather than by re-driving published apps and disposable databases.
- A forbidden scaffolding string still fails the gate before any test runs
  (manifest order: lint checks before `dotnet`), and the bundle half stays
  covered because `frontend-build` produces `dist` and the lint script
  scans it when present.
- `CHRONICLEAF_SKIP_DB_TESTS` is retired: a single unfiltered run cannot
  honestly report partial coverage, so requesting it is a loud error naming
  the retirement, not a narrowed green run.

## Failure, boundary and rollback policy
A missing `driftwatchdog` binary fails the wrapper loudly (command-not-found
propagates nonzero) instead of falling back to the old stages — a fallback
would be two gates. A malformed manifest fails `gate --dry-run` at parse
time before any check runs. A timed-out check is `REVIEW_REQUIRED` with
missing evidence, distinct from `FAIL`. Unknown check output is the
runner's text-mode contract (exit 0 → PASS, nonzero → FAIL). Rollback is
gate-only: restore `scripts/verify.sh` and delete `.ai-gate/` plus
`scripts/gate/`; no product, data, migration, or API surface is touched, so
stored evidence, approvals, credentials and operations are unaffected. A
partial adoption (manifest without all six commands bound) fails dry-run
rather than passing with unwired checks.

## Verification oracle
Manifest oracle: `driftwatchdog gate --dry-run` exit 0 with all six checks
in the resolved plan (no execution, no persistence). Parser oracle: a
seeded log containing failures in two classes attributes each to its class
with exact counts and exits nonzero; a clean log exits 0. Wrapper oracle: a
stubbed `driftwatchdog` exiting 1 on `PATH` makes `bash scripts/verify.sh`
exit nonzero. Depth oracle: the three standalone suites plus the new axe
spec green; the e2e-wiring grep green on the committed tree and red on a
tree with one suite filename removed from `run_suite` position. Light
checks for this change: name preflight exit 0, `openspec validate --all
--strict --no-interactive` green, `npm ci` + `tsc --noEmit` + `vite build`
green, `dotnet build` 0 errors, the new gate-adoption test green. The full
`CHRONICLEAF_E2E=1` gate is the batch-end coordinator run, NOT this change.

## Decision ledger and activation gates
Phase: POST-MVP gate infrastructure. DEMAND satisfied by explicit owner
order (this change's brief) — no further activation needed. No unresolved
product-policy decision: there is no product surface. Missing
`driftwatchdog` binary, Docker daemon, or Playwright browsers at
implementation time is recorded prerequisite unavailability, never
fabricated success.

### Numbered decisions (owner-decided items recorded, not re-litigated)
1. Adopt the sibling `driftwatch gate` runner as chronicleaf's verification
   entry point (sibling reconnaissance decision = ADOPT).
2. Manifest surface is `.ai-gate/gate.yaml` with `runtime: driftwatchdog`
   (business-project convention; fits a repo that already stages work in
   `scripts/`). No native `gate.toml` alongside it — native wins on
   conflict, so exactly one manifest exists.
3. Dotnet consolidation is ONE `dotnet build` then ONE unfiltered
   `dotnet test --no-build`; per-stage filters and `--list-tests` guards
   are deleted, and attribution is a generic per-class parse of the single
   run's output.
4. The six checks above are the complete gate; every declared check binds a
   command under `commands:`, and `blocking` stays `[FAIL,
   REVIEW_REQUIRED]`.
5. The depth check executes token-sweep, form-feedback, perf-animation plus
   the new per-nav-page axe spec standalone, and proves a11y-core,
   a11y-roles, responsive-shell wiring into `scripts/e2e.sh` by grep — it
   does not re-drive the e2e harness.
6. The `e2e-browser` check honors the existing `CHRONICLEAF_E2E=1` path:
   with it set, `scripts/e2e.sh` runs; without it, the check prints an
   explicit SKIP note and exits 0. Full-gate evidence at batch end runs with
   it set.
7. `CHRONICLEAF_SKIP_DB_TESTS` is retired with a loud error (decision in
   Behavioral model); partial coverage can no longer report green.
8. `scripts/verify.sh` stays as a thin wrapper calling the gate (HANDOFF
   evidence and `.project.json` reference it); only its header comment and
   body change.
9. This change uses LIGHT verification only (oracles above); the full gate
   runs once at batch end by the coordinator, not here.

## Requirement traceability
| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| GATE-ADOPT-001 | Gate manifest adopting the sibling runner | Decisions 1, 2, 4 | `.ai-gate/gate.yaml` | GATE-ADOPT-001 Success/Failure/Boundary | 1.1, 2.1, 3.1, 4.1 | `driftwatchdog gate --dry-run` exit 0, six checks resolved |
| GATE-ADOPT-002 | Single dotnet run with parsed attribution | Decision 3 | `scripts/gate/dotnet-tests.sh` + `attribute-dotnet-failures.sh` | GATE-ADOPT-002 Success/Failure/Boundary | 1.2, 2.2, 3.1, 4.1 | Seeded-log attribution exact; no `--filter`/`--list-tests` remains in gate path |
| GATE-ADOPT-003 | Existing stages wired + one deep frontend check | Decisions 4, 5, 6 | `scripts/gate/*.sh` + `tests/frontend-depth-axe.spec.ts` | GATE-ADOPT-003 Success/Failure/Boundary | 1.3, 2.3, 3.1, 4.1 | Standalone suites + axe green; e2e-wiring grep red when unwired |
| GATE-ADOPT-004 | `verify.sh` stays a working thin wrapper | Decisions 7, 8 | `scripts/verify.sh` | GATE-ADOPT-004 Success/Failure/Boundary | 1.4, 2.4, 3.1, 4.1 | Stubbed-runner failure propagates nonzero; header names new structure |
