# Design: Gate check correctness (gate-check-correctness)

## Context and implementation boundary
Repository Chronicleaf only. Boundary: `.ai-gate/gate.yaml` (the `timeouts:`
map), `scripts/verify-platform-feed.sh` (the cold-restore proof and its EXIT
trap), `scripts/gate/dotnet-tests.sh` (the single `dotnet build` +
`dotnet test --no-build`). No C#, no TypeScript, no test file, no migration, no
API. Preserve every other check's command and timeout, every product file,
HANDOFF.md, ROADMAP.md, .gitignore and current_spec (none active).

## Language, ownership and release
Bash and YAML only. The check scripts are Chronicleaf-owned; the runner is
driftwatchdog-owned (a sibling) and is consumed, never edited. No dependency is
added or pinned. No release artifact changes.

## Contract and compatibility decisions
- `platform-feed` is declared in `.ai-gate/gate.yaml` with
  `timeouts.platform-feed: 1800000` (30 min). 30 min is chosen because the
  check's step 4 is a `dotnet restore Chronicleaf.sln --no-cache` of the whole
  solution into an empty folder, observed at ~11 min warm-host and higher
  under load; it stays below the runner's 1 h cap. No other check's timeout
  changes.
- `scripts/verify-platform-feed.sh` gains exactly one trailing step: a normal
  `dotnet restore Chronicleaf.sln --nologo --verbosity quiet` after the
  cold-restore resolution checks and before the final success line, so the
  `obj/project.assets.json` files written by the cold restore (which named the
  scratch folder) are rewritten to name the live global package folder. The
  proof itself (restore into an EMPTY folder, then assert each platform package
  resolved there) is unchanged.
- `scripts/gate/dotnet-tests.sh` runs `dotnet build ... --verbosity quiet` and
  `dotnet test ... --verbosity quiet`. `--verbosity quiet` keeps the build to a
  single success line and the test output to failed-test detail plus the
  summary, so both fit the runner's 2048-byte diagnostic head. The full output
  still tees to `CHRONICLEAF_DOTNET_LOG` (default `dotnet-gate.log`). The
  exit-code propagation is unchanged.

## Behavioral model and invariants
- The `platform-feed` check reaches a terminal status within its timeout and is
  never killed mid-restore. When it finishes, the repository's `obj` files name
  a live package folder; a subsequent `dotnet build` is consistent with them.
- The `dotnet` check's failure output is visible within the runner's 2048-byte
  head. A pass emits one build line and the per-project test summary lines.
- No check's proof is weakened: the platform-feed check still performs the
  empty-folder restore and asserts resolution from the committed feed.

## Failure, boundary and rollback policy
If the cold restore fails, `fail()` still exits nonzero with the restore log,
and the trailing re-restore never runs (the script already aborted). If the
re-restore itself fails, the script fails loudly rather than leaving a
half-restored `obj`. The scratch directory stays under `mktemp` and is removed
by the EXIT trap in both paths. Rollback is three file edits with no data or
API surface.

## Verification oracle
`bash scripts/gate/platform-feed.sh` exits 0 and, immediately after, each
project's `obj/project.assets.json` `packageFolders` names a directory that
exists (spot-check `src/Chronicleaf.Core`). `bash scripts/gate/dotnet-tests.sh`
exits 0 with `--verbosity quiet`. A deliberate temporary failing test, run once,
produces a diagnostic whose first 2048 bytes contain the failed-test line (the
mechanism that was previously hidden). The full gate is not required for this
scoped change; the two checks are run directly.

## Decision ledger and activation gates
Phase: POST-MVP. DEMAND satisfied by the owner's direct order after the
2026-10-09 gate diagnosis (the `dotnet` FAIL with no test failure, the
permanent `platform-feed` `REVIEW_REQUIRED`, and the truncated diagnostic). No
unresolved product-policy decision. The runner's defaults are recorded as
sibling facts and deliberately not patched here.

## Requirement traceability
| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| GATE-FIX-001 | platform-feed completes | timeout 1800000 in manifest | `.ai-gate/gate.yaml` | GATE-FIX-001 Success/Failure/Boundary | 1.1, 2.1, 4.1 | `scripts/gate/platform-feed.sh` exits 0 within the timeout |
| GATE-FIX-002 | obj left consistent | trailing normal restore | `scripts/verify-platform-feed.sh` | GATE-FIX-002 Success/Failure/Boundary | 1.1, 2.2, 4.1 | `obj/.../project.assets.json` names a live folder after a run |
| GATE-FIX-003 | failure visible in cap | quiet build and test | `scripts/gate/dotnet-tests.sh` | GATE-FIX-003 Success/Failure/Boundary | 1.1, 2.3, 4.2 | first 2048 bytes of a failing run contain the failed-test line |
