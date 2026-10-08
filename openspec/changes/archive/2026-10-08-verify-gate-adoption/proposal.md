# Proposal: Verify gate adoption (verify-gate-adoption)

## Why
Chronicleaf's verification entry point is `scripts/verify.sh` (902 lines,
~27 filtered `dotnet test Chronicleaf.sln` stages each preceded by a
`dotnet test --list-tests` guard — ≈54 dotnet invocations), plus
`scripts/e2e.sh`, `scripts/verify-platform-feed.sh` and
`scripts/check-web-scaffolding.sh`. The per-stage
`--filter "FullyQualifiedName~..."` design carries the failure class the
`assert_filter_matches` guard exists for: `dotnet test --filter` with a
pattern that selects no tests exits 0, so a mistyped filter is a silent
green no-op and every stage pays a guard invocation to close it. The six
new UI suites (a11y-core, a11y-roles, responsive-shell, token-sweep,
form-feedback, perf-animation) run, but no single deep frontend check names
them together with a full WCAG pass — the "shallow gate" critique has no
concrete answer. The sibling driftwatchdog repository owns a shared gate
runner (`driftwatch gate`) with a documented business-project integration
recipe; chronicleaf has no gate manifest and does not use it. This package
adopts that runner as chronicleaf's verification entry point, collapses the
dotnet section to a single unfiltered run with parsed per-class
attribution, wires the existing custom stages in as checks, adds one deep
frontend check, and keeps `scripts/verify.sh` as a thin wrapper. No product
behavior changes: no C#, no TSX.

## What Changes
Add one gate manifest (`.ai-gate/gate.yaml` with `runtime:
driftwatchdog`, per the sibling's business-project recipe) declaring a
project-defined profile with six checks: `platform-feed`,
`web-scaffolding-lint`, `dotnet`, `frontend-build`, `frontend-depth`,
`e2e-browser`. Replace the ~27 filtered dotnet stages plus guards with ONE
`dotnet build` then ONE unfiltered `dotnet test --no-build` in
`scripts/gate/dotnet-tests.sh`; per-package failure attribution is
preserved by `scripts/gate/attribute-dotnet-failures.sh`, which parses the
single run's `Failed <class>.<test>` lines and reports counts per
test-class (a single unfiltered run cannot silently select nothing, which
removes the failure class `assert_filter_matches` exists for). Wire the
existing stages in as check commands: platform-feed, web-scaffolding-lint,
`npm ci` + `tsc --noEmit` + `vite build`, and the e2e browser suites via
`scripts/e2e.sh` (honoring the existing `CHRONICLEAF_E2E=1` path). Add ONE
new custom check, `scripts/gate/frontend-depth.sh`: it executes the three
standalone UI suites (token-sweep, form-feedback, perf-animation) plus a
new axe-core full WCAG pass per top-level nav page
(`tests/frontend-depth-axe.spec.ts`, vite dev server with route-mocked
`/api/*`, no server, no database), and proves the three app-backed UI
suites (a11y-core, a11y-roles, responsive-shell) are wired into
`scripts/e2e.sh` by asserting each file is named in a `run_suite`
invocation — failing loudly if any is unwired. Keep `scripts/verify.sh`
working as a thin wrapper that calls the gate (file kept: HANDOFF evidence
and `.project.json` reference it); its header comment is updated to name
the new structure. See design.md.

## Package Boundary and Split Assessment
Own only the verification entry point: the gate manifest, the six
`scripts/gate/` check wrappers, the attribution parser, the new axe spec,
and the `verify.sh` wrapper header. Repository Chronicleaf; no product
runtime surface — no C# change, no TSX change, no migration, no API route,
no envelope, no approval binding, no evidence rule. Depends on nothing new
except the sibling runner binary, which is an operator prerequisite, not a
vendored dependency (never copied or forked into this repo). If a product
behavior change proves unavoidable during implementation, it is named as a
separate future package and this one stays gate-only. This unit is accepted
by its scoped oracles (dry-run plan validation, seeded-failure
attribution, wrapper nonzero propagation, light frontend checks), not by a
full-gate run — the full gate runs once at the end of the batch per the
batch plan. Excluded surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**ADOPT the sibling runner.** The verification-orchestration concern is
owned by driftwatchdog; chronicleaf declares policy and check commands and
executes nothing of the runner's internals locally.

| Sibling surface inspected | Evidence | Decision |
|---|---|---|
| Gate runner + plan resolution (`driftwatchdog gate`, `--dry-run`, `--format json`) | `README.md` "Engineering Gate" + "Integration recipe" (§223–277); `driftwatchdog gate --help` on this host | ADOPT as chronicleaf's verification entry point |
| Business-project manifest (`.ai-gate/gate.yaml`, `runtime: driftwatchdog`, custom `profile` selects exactly its declared `checks`) | `README.md` "Business-project policy at `.ai-gate/gate.yaml"` (§492–556); `templates/deployable/gate.toml` starter | ADOPT the `.ai-gate/gate.yaml` surface (fits a repo that already stages work in `scripts/`) |
| Gate ownership rule (driftwatchdog owns plan/execution/blocking; projects own check commands) | `skills/driftwatchdog-gate/SKILL.md` "Gate path" | ADOPT: no runner code copied, forked, or vendored into chronicleaf |
| Workspace check wiring (`declared-verification` reads `.project.json` `verification.command`) | `README.md` "Declared-verification Gate concern" (§761–798) | KEEP local: `.project.json` still names `bash scripts/verify.sh`, which now calls the gate |

No shared `/lib` added and no sibling edit authorized. Do not edit anything
under `/home/paul/code/driftwatchdog/`.

## BFS Impact Map
- Actor/flow: the single-user owner running verification locally before
  archive; the batch coordinator running the full gate once at batch end.
- Boundary/callers: `scripts/verify.sh` (becomes a thin wrapper; header
  rewritten; HANDOFF evidence and `.project.json`
  `verification.command` keep referencing it), `scripts/e2e.sh` (unchanged;
  invoked by the `e2e-browser` check), `scripts/verify-platform-feed.sh`
  and `scripts/check-web-scaffolding.sh` (unchanged; invoked by their
  checks), `src/Chronicleaf.Web` (`npm ci`, `tsc --noEmit`, `vite build`,
  Playwright suites — commands reused, no suite modified except the one new
  axe spec file added), `.ai-gate/gate.yaml` (new manifest surface).
- Data/integration: no schema, no API, no envelope change; the dotnet run
  executes the same solution tests, only scheduled once instead of ~27
  times.
- Verification: light only for this change — name preflight, strict
  OpenSpec validation, Web install/typecheck/build, `dotnet build`, the new
  gate-adoption test green, manifest valid under
  `driftwatchdog gate --dry-run`. Full `CHRONICLEAF_E2E=1` gate runs once at
  batch end and is NOT run here.
- Privacy/compatibility: no credentials, tokens, PATs, or session values in
  any committed file; the axe spec uses route mocks with a synthetic actor
  id, following the form-feedback pattern.

## Capabilities
### New Capabilities
- `verify-gate-adoption`: Driftwatch gate manifest, single unfiltered
  dotnet run with per-class attribution, and a deep frontend check as the
  shared verification entry point.

## Non-goals
Product behavior changes of any kind (no C#, no TSX, no migrations, no
routes, no envelope, no approval bindings, no evidence rules); copying,
forking, or vendoring the sibling runner; editing anything under
`/home/paul/code/driftwatchdog/`; touching HANDOFF.md rows, ROADMAP.md, or
.gitignore beyond the one evidence row this change's own completion
requires (coordinator owns them); running the full gate in this change
(batch-end coordinator run); implementation, archive, commit, push or
deployment in this authoring work beyond the authorized two commits.
