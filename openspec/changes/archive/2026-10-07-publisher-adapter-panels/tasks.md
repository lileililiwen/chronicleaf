# Tasks: publisher-adapter-panels

## 1. BFS — Baseline and impact coverage
- [x] Map requirements to layers/callers/tests (design §5 matrix; no route/envelope/migration touched).
- [x] Confirm the implementation-handoff gate: exact files, commands, oracles (design §1/§2/§8).
- [x] Verify no suite asserts disabled target options or a fixed mode list (grep over `src/Chronicleaf.Web/tests/`).
- [x] Verify each adapter binds its `TargetLine` as preview warnings[0] (read all six publishers).
- [x] Record CRUD matrix (design §9 table); name `publication-history` as the explicit out-of-scope hole.

## 2. DFS — Requirement-by-requirement implementation
- [x] PUBLISHER-ADAPTER-PANELS-001: add parameterless ctors to the five options records; add `_configured` + `StartConfigured` + one regression fact per suite; prove each fact fails without its ctor (reproduction).
- [x] PUBLISHER-ADAPTER-PANELS-002: build `AdapterTargetPanel.tsx`, wire into `PublishForm` for both namespaces, show classification + guidance + bound-configuration line.
- [x] PUBLISHER-ADAPTER-PANELS-003: mode box derives from the picked descriptor; export-only options selectable; preview refusal `publisher_target_export_only` shown verbatim.
- [x] Browser suite `tests/publisher-adapter-panels.spec.ts` + `scripts/e2e.sh` suite-24 block (six sections, silenced dispatch, reused seed).

## 3. BFS — Cross-surface regression and completeness
- [x] Both publish hosts render the adapter block with unchanged consent; existing testids hold.
- [x] `publish-from-variant`, `publications`, `journey-publish` suites still green (full `bash scripts/e2e.sh`).
- [x] No current-change placeholders; `git diff --check` clean; no other change's files touched.

## 4. Verification
- [x] `dotnet test` five adapter stages (each asserts ≥1 test; new facts included).
- [x] `npm ci`, `tsc --noEmit`, `vite build` green.
- [x] New Playwright suite green + full `bash scripts/e2e.sh` green.
- [x] `openspec validate --all --strict --no-interactive` green; archive with `openspec archive publisher-adapter-panels --yes` (no `--skip-specs`); confirm canonical spec promoted.
