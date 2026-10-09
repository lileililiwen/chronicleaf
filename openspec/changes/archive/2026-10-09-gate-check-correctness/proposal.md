# Proposal: Gate check correctness (gate-check-correctness)

## Why
The adopted `driftwatchdog gate` answered the "one entry point" goal, but two
of its checks were not honest. (1) `platform-feed` was declared with no
`timeouts:` entry, so it inherited the adapter default of 60000 ms while its
own step 4 is a full `dotnet restore --no-cache` of the solution into an empty
package folder (minutes, not seconds). The check was therefore killed
mid-restore on every run and reported `REVIEW_REQUIRED` forever. (2) The
cold-restore proof rewrites every project's `obj/project.assets.json` to name
its scratch package folder as the global packages folder, then the script's
EXIT trap deletes that folder. A later `dotnet build` sees `obj` as up to date,
skips restore, and `dotnet test --no-build` resolves assemblies against the
deleted path — the intermittent `dotnet` FAIL with no test failure that the
last gate run showed while a hand-run of the same tests passed 858/858 in
40m47s. (3) When the `dotnet` check does fail, the gate keeps only the first
2048 bytes (`MAX_DIAGNOSTIC_BYTES`) of combined output, and at default
verbosity the build's per-project lines fill that budget, so the actual test
failure at the tail was invisible. This change makes the two checks correct
and the failure diagnosable. No product code, no test assertions, no API.
Status: Specified handoff; implementation in the same session. Stage: POST-MVP
(gate infrastructure; DEMAND satisfied by the owner's direct order after the
2026-10-09 gate diagnosis).

## What Changes
Add a `platform-feed` timeout (1800000 ms) to `.ai-gate/gate.yaml` so the
cold-restore proof is allowed to finish. Re-restore the solution normally at
the end of `scripts/verify-platform-feed.sh` after the proof, so the
repository's `obj/project.assets.json` files point back at a live package
folder instead of the deleted scratch. Run the `dotnet` check's build and test
with `--verbosity quiet` in `scripts/gate/dotnet-tests.sh` so the failed-test
detail and summary land inside the 2048-byte diagnostic head, while the full
output is still teed to `CHRONICLEAF_DOTNET_LOG` for deep diagnosis. No product
code, no test file, no API, no schema, no migration.

## Package Boundary and Split Assessment
Own only `.ai-gate/gate.yaml`, `scripts/verify-platform-feed.sh` and
`scripts/gate/dotnet-tests.sh`. Repository Chronicleaf, shell/YAML only, no C#
or TypeScript surface. This is one independently verifiable unit: run the
`platform-feed` and `dotnet` checks through the gate and prove both settle to
a terminal status (PASS) and that a `platform-feed` run leaves
`obj/project.assets.json` pointing at a live folder. The driftwatchdog Gate
runner itself (timeouts, diagnostic cap, scheduling) is a sibling concern and
is NOT edited; the one sibling observation made here — the 60s default and the
2048-byte cap — is recorded, not patched. No backend, no UI.

## Sibling and Shared Architecture Reconnaissance
**Extend local config; no sibling edit.** The `driftwatchdog` Gate owns the
runner, the default timeout (`DEFAULT_CHECK_TIMEOUT_MS = 60_000`), the max
(`MAX_CHECK_TIMEOUT_MS`, 1 h) and the diagnostic cap (`MAX_DIAGNOSTIC_BYTES =
2048`). Chronicleaf is the project, so it owns its `commands:` and its
`timeouts:` map; the fix is to declare the timeout the check actually needs and
to keep the check's output small enough that the runner's cap shows the
failure. Evidence inspected: `dotnet-platform-libs` and `driftwatchdog` carry
no Chronicleaf check; the check scripts live in this repository under
`scripts/gate/`. No shared `/lib` added and no sibling edit authorized.

## User Experience and Interface Impact
**N/A — no human actor or interactive workflow exists in this change.** The
only interface is a developer's terminal running `bash scripts/verify.sh`; the
change alters which checks are allowed to finish and what text the terminal
shows when one fails. There is no page, control, form or journey, so the
UI/UX obligations of the OpenSpec authoring rule do not apply. The "user" here
is the operator reading a gate result, and the change makes that result honest
(a finished platform-feed check; a visible test failure).

## BFS Impact Map
- **Actor/flow:** the operator running `bash scripts/verify.sh` (the gate
  wrapper) or `open_spec`-driven local verification.
- **Boundary/callers:** `.ai-gate/gate.yaml` (timeouts map),
  `scripts/verify-platform-feed.sh` (the cold-restore proof and its trap),
  `scripts/gate/dotnet-tests.sh` (the single build + test invocation). No
  caller outside the gate.
- **Data/integration:** no schema, no API, no envelope. The only state touched
  is the local `obj/` package-assets files, restored to a consistent state.
- **Verification:** run `scripts/gate/platform-feed.sh` to completion and
  assert it exits 0 and leaves `obj/project.assets.json` naming a live
  package folder; run `scripts/gate/dotnet-tests.sh` and assert exit 0 with
  quiet output; a planted failure (temporary) shows the failed-test line
  inside 2048 bytes.
- **Privacy/compatibility:** no credential, token, PAT or secret is read or
  written. The scratch folder is a `mktemp` directory and is removed on exit in
  both the success and failure paths.

## Capabilities
### New Capabilities
- `gate-check-correctness`: the `platform-feed` and `dotnet` gate checks run to
  completion, leave `obj/` consistent, and surface a failure inside the
  runner's diagnostic cap.

## Non-goals
Editing the driftwatchdog runner (sibling-owned); raising
`MAX_DIAGNOSTIC_BYTES` or `DEFAULT_CHECK_TIMEOUT_MS` in the sibling; changing
any product or test code; changing what the checks prove (the cold-restore
proof still restores the whole solution into an empty folder); adding new
checks. No implementation, archive, commit, push or deployment beyond this
change's own two commits.
