## 1. BFS — Baseline and impact coverage
- [x] 1.1 Map every requirement and Success/Failure/Boundary scenario to `src/Chronicleaf.Web/src/features/static-blog-export/`, the `ProjectPublish.tsx` placement, the delivered export/review/draft/variant/milestone routes and the new Playwright fixtures; confirm proposal/design/spec agree and record baseline.
- [x] 1.2 Confirm completed dependencies (static-blog-export, content-asset-variants, markdown-review-export, grounded-markdown-drafts, projects-and-milestones, project-area-ia, publish-from-variant), the TypeScript-only boundary (no C# change, no migration), and every activation/blocker decision; do not duplicate sibling code.

## 2. DFS — Requirement-by-requirement implementation
- [x] 2.1 Implement STATIC-BLOG-EXPORT-UI-001 picker selection with included/excluded preview (`api.ts`, `ExportPanel.tsx`, Publish-area placement); add suite assertions for its specified success, failure and boundary results before marking complete.
- [x] 2.2 Implement STATIC-BLOG-EXPORT-UI-002 explicit export, watched job and download-only-when-ready; add suite assertions for its specified success, failure and boundary results before marking complete.

## 3. BFS — Cross-surface regression and completeness
- [x] 3.1 Run the new Playwright suite with the exact fixtures in design.md; verify scope, privacy (no credential/body leak in new reads), and output invariants across every changed caller, and audit the original impact map for omissions.
- [x] 3.2 Exercise dependency compatibility and retained preexisting history; run the FULL pre-existing browser set (`bash scripts/e2e.sh` green) and verify no current-change placeholders, unauthorized side effects or unrelated sibling changes.

## 4. Verification
- [x] 4.1 Run `npm ci`, `tsc` + `vite build` (or `tsc --noEmit`), the new Playwright suite plus the full pre-existing browser set, and `git diff --check`. Record exact commands/results and unavailable prerequisites; no mock-only runtime claims.
- [x] 4.2 Run `node scripts/check-openspec-change-names.mjs`, `openspec validate --all --strict --no-interactive`, and review untracked/staged content and the mapped impact surface before archive.
- [x] 4.3 Only after implementation authorization and all scoped acceptance: archive without --skip-specs, create implementation/tests/archive commit (exclude HANDOFF.md), then HANDOFF.md-only pointer/evidence commit; exactly two commits, no conversational confirmation, stop and do not push.
