## 1. BFS — Baseline and impact coverage

- [x] 1.1 Mapped WEB-FEED-001..005 to `App.tsx` (one `<ToastRegion/>` in
  `ProductShell`), `features/ui-feedback/` (new `Toast.tsx`,
  `revisionConflict.ts`, `FieldCount.tsx`), `styles.css` (appended token-only
  rules; zero existing rules touched), `MilestoneEditor.tsx` (toast +
  helper + 3 counters), `ProjectEditor.tsx` (toast + helper + 2 counters),
  `ClientsArea.tsx` `ClientForm` (toast + 2 counters + notes `maxLength`),
  `MarkdownReviewPanel.tsx` + `markdown-review-export/types.ts` (toast +
  dirty tag + helper + additive `storedRevision` getter),
  `CredentialVaultPanel.tsx` (toast + 8 fieldsets),
  `ScreenshotCapturePanel.tsx` (hint + anchor + form `id`), and new
  `tests/form-feedback.spec.ts` (standalone: dev server + route mocks).
  Baseline recorded: no toast surface anywhere; review `dirty` flag drives
  only the save/approve disabled states; three private conflict wordings
  (milestone, project, review `revision_conflict` branch); capture URL label
  `The pages to photograph (up to 3)` with no minimum and an empty state
  with no link; 0 of 7 bounded fields show a counter and client notes has no
  `maxLength`; vault rows are `li` children with no grouping and the viewport
  group is already `fieldset`/`Viewports`. Frontend-only boundary confirmed:
  no migration/route/envelope/binding/evidence/validation change.

## 2. DFS — Requirement-by-requirement implementation

- [x] 2.1 WEB-FEED-001: `ui-feedback/Toast.tsx` (module store +
  `showToast`/`dismissToast` + `useToast` + always-present `role="status"`
  `aria-live="polite"` region fixed top-center, latest-replaces, 4s
  auto-dismiss, dismiss button); `<ToastRegion/>` mounted once in
  `ProductShell`; success toasts (`Milestone saved.`, `Project saved.`,
  `Client saved.`, `Credential stored.`, `Credential removed.`, `Saved as a
  new revision.`, `Revision approved.`, `Approval withdrawn.`) and failure
  toasts (inline-notice message verbatim, conflict copy for 409s) wired into
  `MilestoneEditor`, `ProjectEditor`, `ClientForm`, `CredentialVaultPanel`
  (save + remove) and `MarkdownReviewPanel` (save + approve + unapprove).
- [x] 2.2 WEB-FEED-002: review editor wrapper gains `review__edit--dirty`
  (gold left border, tokens only) while dirty; title row
  (`review-title-row` + `review-dirty-tag` `Unsaved changes` badge) shows
  while dirty; both clear through the existing `load()` on save; existing
  `review-unsaved` hint kept.
- [x] 2.3 WEB-FEED-003: `formatRevisionConflict(storedRevision)` returns the
  two entity-neutral sentences; both editors render exactly its output;
  `describeFailure` returns exactly its output for `isRevisionConflict` via
  the new `ReviewApiError.storedRevision` getter (mirrors
  `MilestoneApiError`); no other branch changes.
- [x] 2.4 WEB-FEED-004: capture URL label reads `The pages to photograph
  (up to 3, at least 1 required)`; empty state gains `#capture-form` anchor
  (`capture-empty-link`); form gains `id="capture-form"`.
- [x] 2.5 WEB-FEED-005: `<FieldCount>` (`small.form__count`,
  `<field>-count`, `n / max`) beside every bounded field (milestone
  title/version/notes, project name/description, client name/notes; client
  notes gains `maxLength={20000}`); vault rows wrap in
  `fieldset`/`legend` with `providerLabel`; viewport `fieldset`/`Viewports`
  asserted unchanged.

## 3. BFS — Cross-surface regression and completeness

- [x] 3.1 Full pre-existing Playwright set green unmodified (full
  `verify.sh` deferred to the end of the batch per the batch plan): every
  `data-testid` preserved, no label/input `id` changed, no class removed,
  `token-sweep.spec.ts` oracles still green over the appended CSS (token
  references only), no new production dependency, `dist`/test-results
  artifacts never committed.

## 4. Verification

- [x] 4.1 Light checks only: `node
  scripts/check-openspec-change-names.mjs` exit 0, `openspec validate --all
  --strict --no-interactive` green, `npm ci` + `tsc --noEmit` exit 0 + `vite
  build` exit 0 in `src/Chronicleaf.Web`, `npx playwright test
  tests/form-feedback.spec.ts` 8/8 standalone (dev server + route-mocked
  API, no database); `git diff --check` clean; archive readiness with
  exactly two commits (implementation/tests/archive, then HANDOFF-only); full
  `CHRONICLEAF_E2E=1 bash scripts/verify.sh` deferred to the end of the
  batch per the batch plan.
