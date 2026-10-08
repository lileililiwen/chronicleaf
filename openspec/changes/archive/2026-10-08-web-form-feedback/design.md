# Design: Web form feedback (web-form-feedback)

## Context and implementation boundary

Repository Chronicleaf only. Primary boundary
`src/Chronicleaf.Web/src/features/ui-feedback/` (new: `Toast.tsx` owning the
module-level toast store, the `useToast` hook and the `<ToastRegion/>`
component; `revisionConflict.ts` owning `formatRevisionConflict`;
`FieldCount.tsx` owning the shared counter), `src/Chronicleaf.Web/src/App.tsx`
(one `<ToastRegion/>` inside the signed-in product shell, outside `<main>` so
it is chrome rather than content), `src/Chronicleaf.Web/src/styles.css` (new
token-only rules appended; zero existing rules touched),
`MilestoneEditor.tsx` / `ProjectEditor.tsx` / `ClientsArea.tsx`
(`ClientForm`) / `MarkdownReviewPanel.tsx` / `CredentialVaultPanel.tsx` /
`ScreenshotCapturePanel.tsx` (wiring only),
`markdown-review-export/types.ts` (one additive `storedRevision` getter on
`ReviewApiError`, mirroring `MilestoneApiError`), and
`src/Chronicleaf.Web/tests/form-feedback.spec.ts` (new standalone suite that
starts the Vite dev server and route-mocks `/api/*`). Depends on no backend
package and changes none: every API contract annotated here is delivered and
stays byte-identical, and every `maxLength` a counter names already exists in
`Chronicleaf.Core` (`Milestone.TitleMaxLength` 160, `Milestone.NotesMaxLength`
20000, `Project.NameMaxLength` 120, `Project.DescriptionMaxLength` 2000,
`Client.NameMaxLength` 160, `Client.NotesMaxLength` 20000). Preserve forge.yaml,
.standard receipts, unrelated files, HANDOFF.md (until the second
HANDOFF-only commit), ROADMAP.md, .gitignore, every existing `data-testid`,
and `current_spec` (already `web-form-feedback`).

## Language, ownership and release

React 19 + TypeScript + Vite for `src/Chronicleaf.Web`; no C#, no migration,
no API edit. The feedback elements live in the Web project as in-tree
JSX/CSS augmentations over the delivered token set (`tokens.css` roles and
scales via `styles.css` aliases); no new production dependency, no component
library, no CDN import. No package install in this authoring task (`vite`
and `@playwright/test` are already devDependencies and the new suite uses
only those).

**Keep local.** Toast rendering, dirty state, conflict copy, counters and
fieldset grouping are Chronicleaf-product presentation; no matching recurring
implementation is evidenced in sibling platform contracts. Evidence inspected:
dotnet-platform-libs/README.md lists Platform.Jobs and Platform.Storage
families but no Web feedback or form-annotation system. No shared `/lib`
added and no sibling edit authorized.

## Contract and compatibility decisions

Consume the delivered API contracts exactly as served (projects/milestones,
clients, vault, drafts, review, capture): same paths, payloads, envelope
`{code,message,traceId,fieldErrors?}`, antiforgery/session handling, and
revision/If-Match discipline. No new API route, no envelope change, no
approval-binding change, no evidence-rule change. The five gaps the package
closes are:

- **Toast region.** `ui-feedback/Toast.tsx` owns a module-level store (the
  current toast plus its dismiss timer, never React state held by a panel),
  `showToast(message, kind)` (latest replaces: the previous timer is
  cancelled and the previous toast is dropped), `dismissToast()`, and
  `useToast()` over `useSyncExternalStore`. `<ToastRegion/>` renders one
  always-present `<div className="toast-region" role="status"
  aria-live="polite" data-testid="toast-region">` fixed top-center; the toast
  itself (`data-testid="toast"`, message plus a `Dismiss` button with
  `aria-label="Dismiss"` and `data-testid="toast-dismiss"`) renders only
  while a toast is current. Auto-dismiss is 4000ms after the latest show.
  The region mounts once in `ProductShell`, so a toast shown by a save that
  navigates away (project/milestone/client create) survives the navigation
  instead of unmounting with the form. Panels announce outcomes only; the
  inline notices keep the actionable detail and stay exactly as they are.
- **Dirty-editor indicator.** The review editor wrapper (`div[data-testid=
  review-editor]`) gains `review__edit--dirty` while `dirty` is true: a gold
  left border (`border-left: var(--border-flag) solid var(--shell-accent)`,
  tokens only, no raw literal) with token padding. The title row
  (`div.review__title-row[data-testid=review-title-row]` wrapping the Title
  label) gains `<span className="badge badge--pending"
  data-testid="review-dirty-tag">Unsaved changes</span>` while dirty. Both
  clear on save because `load()` already resets `dirty` to false; no new
  state is added. The existing `review-unsaved` decision-row hint is kept
  (different job: it explains why approval is disabled, not that the text is
  unsaved).
- **Conflict copy.** One `formatRevisionConflict(storedRevision: number |
  null): string` in `ui-feedback/revisionConflict.ts`, entity-neutral by
  construction (the signature takes no noun, so no call site can drift):
  `null` reads `This record changed since you opened it, so your edit was not
  saved.` and a number reads `This record changed since you opened it (the
  stored revision is N). Your edit was not saved.` `MilestoneEditor` and
  `ProjectEditor` render exactly its output in place of their two private
  wordings; the review panel's `describeFailure` returns exactly its output
  for `isRevisionConflict`, fed by a new `storedRevision` getter on
  `ReviewApiError` that parses `fieldErrors['If-Match']` exactly the way
  `MilestoneApiError.storedRevision` already does. No other `describeFailure`
  branch changes.
- **Capture-form clarity.** The first URL label reads `The pages to
  photograph (up to 3, at least 1 required)` (bound stays
  `CAPTURE_BOUNDS.maximumPages`; the "at least 1" is the submit guard
  `submittable` already enforces, now said aloud). The empty state gains an
  anchor `<a href="#capture-form" data-testid="capture-empty-link">` to the
  form, which gains `id="capture-form"` (testid kept). Native anchor jump;
  no router involvement.
- **Counters and fieldsets.** Every `input`/`textarea` with `maxLength`
  renders `<FieldCount value max testId>` (`<small className="form__count"
  data-testid="<field>-count">` reading `n / max`, `n` the live length):
  milestone title/version/notes, project name/description, client name/notes.
  `ClientForm` notes gains the `maxLength={20000}` the backend
  (`Client.NotesMaxLength`) already enforces. Each credential provider row
  wraps its content in `<fieldset><legend>{providerLabel(provider)}</legend>`
  (existing name span and every testid kept, so vault suites keep their
  hooks); the capture viewport group is already a `fieldset` with a
  `Viewports` legend and is asserted, not restructured.

Existing Playwright suites keep passing unmodified: every `data-testid`
survives, no label/input `id` changes, no class removed (one wrapper `div`
around the review Title label; one `fieldset` inside each vault `li`; one
`small` per bounded field; one `id` added to the capture form). The
`web-token-sweep` source oracles keep passing because every new CSS
declaration references a token (no raw sizing/hex, no bare line-height or
letter-spacing).

## Behavioral model and invariants

- **One region, one toast, latest wins.** At most one toast is ever current;
  a second `showToast` replaces the first and restarts the 4s clock. The
  region itself never unmounts and is never focusable; the dismiss button is
  the only new tab stop, and only while a toast is current.
- **Toasts announce, notices explain.** Success toasts read `X saved.` /
  `Revision approved.` / `Credential removed.` (exact strings in
  tasks 2.x); failure toasts carry the same message the inline notice shows
  (conflict copy verbatim for 409s). No toast is the only record of a
  failure: the inline notice is always rendered beside it.
- **Dirty is derived, never stored twice.** The tag and the border read the
  panel's existing `dirty` flag; save clears it through the existing
  `load()`. A tag without the border (or vice versa) is non-conformant.
- **One conflict sentence.** The three call sites render byte-identical
  output for the same `storedRevision`. A fourth wording anywhere in the Web
  tree fails WEB-FEED-003.
- **Counters never lie about the bound.** Each counter's `max` is the same
  literal as the field's `maxLength`; the counter is the field's sibling in
  the same `form__field`, so assistive technology meets them together.

## Failure, boundary and rollback policy

A removed testid breaks its suite loudly (every testid is preserved; new ones
are additive). An unmocked API route in the standalone suite fails the test
rather than hanging (route-mocked fetch rejects fast; the suite registers a
catch-all 500 envelope for calls a test does not name). Unknown client-side
path keeps the delivered "Nothing here" view. Rollback is frontend-only:
revert the Web tree; no data, migration, API surface, credential,
antiforgery value or testid is touched, so stored evidence, approvals,
credentials and operations are unaffected. Partial wiring (a panel that saves
silently) fails the new suite, not silently accepted. A second toast kind, a
toast queue, or a counter whose `max` disagrees with its field's `maxLength`
is out of scope and fails review, not scope-creep accepted. If implementation
proves a backend change unavoidable, it is named as a separate future
package and this one stays frontend-only.

## Verification oracle

Existing suites: every Playwright suite green unmodified (full `verify.sh`
deferred to the end of the batch per the batch plan; the light checks run
`tsc`, `vite build` and the new suite only). New
`tests/form-feedback.spec.ts` (standalone: Vite dev server started in
`beforeAll`, `/api/*` route-mocked, no database): project save shows a
`role="status"` toast that the dismiss button clears; milestone save shows a
toast that survives the post-save navigation; vault save shows a toast that
auto-dismisses (~4s); review typing shows the dirty tag plus the gold
border and save clears both; the helper returns the two exact sentences and
all three panels import it; the capture URL label names "(at least 1
required)", the empty state links to `#capture-form`, and the link reveals
the form; typing updates every counter to `n / max`; every provider row is
a `fieldset` whose `legend` is the provider name and the viewport group is a
`fieldset` with the `Viewports` legend. Conformance probes: `tsc --noEmit`
exit 0, `vite build` exit 0, `token-sweep.spec.ts` still green (its oracles
cover the appended CSS), and the new suite 8/8.

## Decision ledger and activation gates

Phase: POST-MVP. DEMAND satisfied by the 2026-10-08 owner audit order — no
further activation needed. No unresolved product-policy decision in this
scoped contract. Third-party versions are lockfile implementation inputs, not
an invitation to redesign contracts. Missing browsers at implementation time
is recorded prerequisite unavailability, never fabricated success.

### Owner-decided feedback decisions (owner-decided via the coordinator, 2026-10-08)

1. **One toast at a time, latest replaces.** No queue, no history, no
   stacking. A second outcome supersedes the first; the 4s clock restarts.
2. **Auto-dismiss 4s with a dismiss button.** Both, not either: the toast
   leaves on its own and the owner can send it away sooner.
3. **Region lives in the shell, not the panel.** `ProductShell` owns the one
   `<ToastRegion/>` so post-save navigation cannot unmount the announcement.
4. **Entity-neutral conflict copy.** `This record changed …` in all three
   panels — the helper takes no noun so no fourth wording can drift in. The
   review conflict branch drops its `(code)` suffix to stay byte-identical;
   the code remains in logs and the traceId envelope.
5. **Dirty tag plus gold border, tokens only.** The tag reuses
   `badge--pending`; the border reuses `--border-flag` + `--shell-accent`.
   No new color, no new token.
6. **Counters on every bounded field, including version.** The brief names
   six fields; the version input (`maxLength` 80) gets one too, because
   "every input/textarea with maxLength" leaves no exception.
7. **Client notes bound is surfaced, not set.** `maxLength={20000}` mirrors
   the delivered `Client.NotesMaxLength`; no validation rule changes.
8. **Vault name span stays beside the legend.** The `legend` carries the
   provider name for grouping; the existing `credential-<p>-name` span stays
   so vault suites keep their hooks verbatim.
9. **Standalone suite with route-mocked API.** The new suite starts the Vite
   dev server and mocks `/api/*` per test — real components, real rendering,
   no database — and unit-asserts the helper import directly.

## Requirement traceability

| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| WEB-FEED-001 toast region | One `role="status"` region, 4s + dismiss, latest replaces, wired into 5 panels | Toast-region decisions above | `ui-feedback/Toast.tsx` + `App.tsx` + 5 panels | Success / Failure / Boundary | 1.1, 2.1, 3.1, 4.1 | Mocked browser: toast on save, dismiss clears, auto-dismiss ~4s, survives navigation |
| WEB-FEED-002 dirty indicator | Gold border + tag while dirty, cleared on save | Dirty-indicator decisions above | `MarkdownReviewPanel.tsx` + token-only CSS | Success / Failure / Boundary | 1.1, 2.2, 3.1, 4.1 | Mocked browser: tag + gold border on typing, both gone after save |
| WEB-FEED-003 conflict helper | One helper, identical wording in 3 call sites | Conflict-copy decisions above | `revisionConflict.ts` + 2 editors + `describeFailure` + `storedRevision` getter | Success / Failure / Boundary | 1.1, 2.3, 3.1, 4.1 | Unit: helper output exact; static: all three import it; browser: 409 copy identical |
| WEB-FEED-004 capture clarity | URL hint + empty-state anchor to `#capture-form` | Capture-clarity decisions above | `ScreenshotCapturePanel.tsx` | Success / Failure / Boundary | 1.1, 2.4, 3.1, 4.1 | Mocked browser: hint text, anchor href, link reveals form |
| WEB-FEED-005 counters + fieldsets | `n / max` counters on every bounded field; fieldset/legend on vault rows + viewport group | Counter/fieldset decisions above | 2 editors + `ClientForm` + `FieldCount.tsx` + vault + capture + token-only CSS | Success / Failure / Boundary | 1.1, 2.5, 3.1, 4.1 | Mocked browser: counters update on typing; fieldset/legend present with names |
