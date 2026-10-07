# Tasks: content-pack-panels

## 1. BFS — Baseline and impact coverage
- [x] Map requirements to layers/callers/tests (design §1/§5/§9; no route/envelope/migration touched).
- [x] Confirm the implementation-handoff gate: exact files, commands, oracles (design §1/§2/§8).
- [x] Verify all consumed routes exist with exact paths/shapes (adapt/export/refusal/review/list reads).
- [x] Record CRUD matrix (design §9); name `content-asset-retirement` as the explicit out-of-scope hole.
- [x] Add test skeleton: `tests/content-pack-panels.spec.ts` outline + e2e.sh block plan.

## 2. DFS — Requirement-by-requirement implementation
- [x] CONTENT-PACK-PANELS-001: `content-pack-panels/api.ts` + `types.ts` (adapt LI/XHS, refusal POSTs, byte download; verbatim `PackApiError`).
- [x] CONTENT-PACK-PANELS-001: `LinkedInPackPanel.tsx` (+ shared `PackReviewSection.tsx`): picker, derive, inert preview + media + voice, approve/edit, download, `publisher_unsupported` verbatim.
- [x] CONTENT-PACK-PANELS-002: `MediumPackPanel.tsx`: approved-asset picker, inert preview + manifest note, download, `publisher_unsupported` verbatim with no upstream call, export-only stated.
- [x] CONTENT-PACK-PANELS-003: `XiaohongshuPackPanel.tsx`: picker, derive with title/tags/cover/CTA, editable preview, approve/edit, download, re-approval stated, no-login/no-post stated.
- [x] Mount all three in `MilestoneRows.tsx` content area under `ContentVariantPanel` with archived/confirmed gating.
- [x] CONTENT-PACK-PANELS-004: extend `ContentVariantService.ListAsync` with an
  opt-in pack read (`?kinds=packs`; default unchanged, unknown kinds 422),
  document on `IContentVariantService.ListAsync`, add one list fact each to
  `LinkedinCopyPackageTests` and `XiaohongshuContentPackageTests` (listed when
  asked, absent by default, unknown refused), run all three dotnet stages green.
- [x] Browser suite `tests/content-pack-panels.spec.ts` + `scripts/e2e.sh` suite block (own port, `seed_content_pack_panels`, byte-equality + verbatim + no-post counters).

## 3. BFS — Cross-surface regression and completeness
- [x] Content area renders all prior panels unchanged; existing testids hold.
- [x] Full `bash scripts/e2e.sh` green (new suite + all pre-existing).
- [x] No current-change placeholders; `git diff --check` clean; no other change's files touched.
- [x] CRUD matrix re-verified against shipped UI triggers; holes named, none silent.

## 4. Verification
- [x] `npm ci`, `tsc --noEmit`, `vite build` green.
- [x] New Playwright suite green + full `bash scripts/e2e.sh` green.
- [x] `git diff --check` clean.
- [x] `openspec validate --all --strict --no-interactive` green; archive with `openspec archive content-pack-panels --yes` (no `--skip-specs`); confirm canonical spec promoted.
