# Tasks: video-story-panel

## 1. BFS — Baseline and impact coverage
- [x] Map requirements to layers/callers/tests (design §1/§5/§9; discovery GET + edit guard + Web feature + e2e block).
- [x] Confirm the implementation-handoff gate: exact files, commands, oracles (design §1/§2/§8).
- [x] Verify all consumed routes exist with exact paths/shapes (generate/export/refusal/review/asset/evidence/screenshots/voice reads).
- [x] Record CRUD matrix (design §9); name `content-asset-retirement` as the explicit out-of-scope hole plus the UI-unoffered unapprove control.
- [x] Add test skeleton: `tests/video-story-panel.spec.ts` outline + e2e.sh suite-27 block plan (port 5107, `seed_video_story_panel`).

## 2. DFS — Requirement-by-requirement implementation
- [x] VIDEO-STORY-PANEL-003: `IVideoStoryService.ReadForMilestoneAsync` + `GET /api/milestones/{id}/video-story` (null asset when none, 404 foreign/absent, 401 sessionless); facts in `VideoStoryScriptsTests`.
- [x] VIDEO-STORY-PANEL-004: `ContentReviewService.EditAsync` refuses the video-story type 422 naming `bodyMarkdown`; fact proves the approved export still serves identical bytes.
- [x] VIDEO-STORY-PANEL-001: `video-story-panel/api.ts` + `types.ts` (discovery read, generate, review reuse, byte download, publish-attempt refusal; verbatim `VideoStoryApiError`).
- [x] VIDEO-STORY-PANEL-001: `VideoStoryPanel.tsx` (duration select, story/screenshot/evidence pickers over delivered routes, shot composer with derived timings, honest empty states, no-synthesis copy, nothing auto-fires).
- [x] VIDEO-STORY-PANEL-002: `VideoStoryReviewSection.tsx` (shot-order preview with served thumbnails/subtitles/narration/transitions, press-only approve, always-offered download with verbatim `approval_required`/`approval_stale`/`media_unavailable`, publish-attempt with verbatim `publisher_unsupported`).
- [x] Mount in `MilestoneRows.tsx` evolution detail under `ScreenshotCapturePanel` with archived/confirmed gating.
- [x] Browser suite `tests/video-story-panel.spec.ts` + `scripts/e2e.sh` suite 27 (own port 5107, `seed_video_story_panel`: approved story + selected screenshot + bare milestone; select → preview → approve → byte-equal download; refusals verbatim; regenerate round-trip; empty states; armed POST counters).

## 3. BFS — Cross-surface regression and completeness
- [x] Evolution area renders all prior panels unchanged; existing testids hold.
- [x] Full `bash scripts/e2e.sh` green (new suite + all pre-existing, 27 suites).
- [x] No current-change placeholders; `git diff --check` clean; no other change's files touched (beyond the two named backend pieces + their tests).
- [x] CRUD matrix re-verified against shipped UI triggers; holes named, none silent.

## 4. Verification
- [x] `npm ci`, `tsc --noEmit`, `vite build` green.
- [x] Extended `VideoStoryScriptsTests` green (existing verify.sh stage) + new Playwright suite green + full `bash scripts/e2e.sh` green.
- [x] `git diff --check` clean.
- [x] `openspec validate --all --strict --no-interactive` green; archive with `openspec archive video-story-panel --yes` (no `--skip-specs`); confirm canonical spec promoted.
