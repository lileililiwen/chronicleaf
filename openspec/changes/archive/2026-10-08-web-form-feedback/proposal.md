# Proposal: Web form feedback (web-form-feedback)

## Why

Sixty-one changes are implemented, verified and archived, and the Web client
has a shell, tokens, responsive rules and an accessibility core — but filling
in a form still feels like shouting into a well. A live UI audit (2026-10-08,
against the real client on `main`) shows five concrete gaps, all on paths the
owner touches on every visit: a save that succeeds or fails reports itself
only where the form happens to be, so a save that navigates away (project,
milestone, client) leaves no trace it ever happened and a failure below the
fold is never announced; the review editor tracks dirtiness internally yet
shows nothing at the field the owner is editing; the same stale-revision
refusal is worded three different ways in three panels; the capture form's
empty state names no next step and its URL group never says how many pages
make a valid request; and six bounded fields accept a maximum the owner can
only discover by hitting it, because no counter is shown. The product accepts
input it never acknowledges, and a later audit will not catch any of this
from a screenshot. This package closes the five gaps without changing any
backend contract, dependency, route, envelope, approval binding, evidence
rule, or validation limit. Status: Specified handoff; implementation not
started. Stage: POST-MVP (new package; DEMAND satisfied by 2026-10-08 owner
audit order). No drainable package remains ahead of it (HANDOFF records
`web-token-sweep` as the most recent change and `current_spec` is
`web-form-feedback`).

## What Changes

Add one form-feedback layer to the Web client: a single toast region
(`features/ui-feedback/Toast.tsx` plus a `useToast` hook) rendering a
`role="status"` `aria-live="polite"` live region fixed top-center, showing one
toast at a time (the latest replaces), auto-dismissing after 4s with a
dismiss button, wired into the save/approve/remove success and failure paths
of at minimum `MilestoneEditor`, `ProjectEditor`, `CredentialVaultPanel`,
`MarkdownReviewPanel` and `ClientForm`; a dirty-editor indicator on the
review editor (gold left border plus an "Unsaved changes" tag in the title
row while dirty, cleared on save, tokens only); one extracted
`formatRevisionConflict(storedRevision: number | null): string` helper used
by `MilestoneEditor`, `ProjectEditor` and the review panel's
`describeFailure` with identical wording everywhere; capture-form clarity (an
"(at least 1 required)" hint on the URL group and an anchor from the empty
state to `#capture-form`); `maxLength` counters (`<small
data-testid="<field>-count">` reading "n / max", updating on change) on every
bounded field (milestone title/notes, project name/description, client
name/notes); and `fieldset`/`legend` grouping on the credential provider rows
and the capture viewport group with the provider/viewport-group name as the
legend. One new standalone Playwright suite `tests/form-feedback.spec.ts`
(real React rendering through the Vite dev server with route-mocked API
responses, plus unit assertions of the conflict helper — no server, no
database) proves every requirement. No new production dependency, no C#, no
migration, no route, no envelope. See design.md.

## Package Boundary and Split Assessment

Own only the Web client form-feedback layer: the toast region and hook, the
dirty indicator, the conflict-copy helper, the capture hints, the counters
and the fieldset/legend grouping. Repository Chronicleaf, React +
TypeScript + Vite, no C# surface. Depends on no new package: every backend
contract this UI annotates is already delivered and stays byte-identical,
and every validation limit a counter names (`160/20000` milestone title and
notes, `80` milestone version, `120/2000` project name and description,
`160/20000` client name and notes) already exists in `Chronicleaf.Core` and
is only surfaced, never changed. If a backend change proves unavoidable
during implementation, it is named as a separate future package and this one
stays frontend-only. The two deferred owner-BLOCKED packages
(`semantic-narrative-retrieval`, `hosted-managed-ai`) are unchanged and are
not a hidden sub-feature. This unit is accepted by its scoped browser
evidence and outcome, not by completion of the full product. Excluded
surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance

**Keep local.** Toast rendering, dirty indicators, conflict copy, counters
and fieldset grouping are Chronicleaf-product presentation; no matching
recurring implementation is evidenced in sibling platform contracts. Evidence
inspected: dotnet-platform-libs/README.md lists Platform.Jobs and
Platform.Storage families but no Web feedback, toast, or form-annotation
system. Reuse dependency-owned API contracts (projects, milestones, clients,
vault, drafts, review, capture) exactly as served; never duplicate those
concerns. No shared `/lib` added and no sibling edit authorized.

## BFS Impact Map

- Actor/flow: the signed-in single-user owner (community-access session gate
  stays the entry guard). Anonymous and journey readers never meet a form, so
  no anonymous surface changes.
- Boundary/callers: `src/Chronicleaf.Web/src/App.tsx` (one `<ToastRegion/>`
  in the product shell), `src/Chronicleaf.Web/src/features/ui-feedback/`
  (new: `Toast.tsx`, `revisionConflict.ts`, `FieldCount.tsx`),
  `src/Chronicleaf.Web/src/styles.css` (new token-only rules; zero existing
  rule touched), `MilestoneEditor.tsx` + `ProjectEditor.tsx` (toast,
  conflict helper, counters), `ClientsArea.tsx` `ClientForm` (toast,
  counters, `maxLength` on notes), `MarkdownReviewPanel.tsx` (toast, dirty
  tag, conflict helper) + `markdown-review-export/types.ts` (one additive
  `storedRevision` getter on `ReviewApiError`, mirroring `MilestoneApiError`),
  `CredentialVaultPanel.tsx` (toast, fieldsets), `ScreenshotCapturePanel.tsx`
  (hint, anchor, form `id`), and `src/Chronicleaf.Web/tests/form-feedback.spec.ts`
  (new standalone suite). No backend code; no C# file; no migration; no API
  contract change.
- Data/integration: no schema, no API, no envelope change; all API calls keep
  their existing paths, payloads and antiforgery/session handling; every
  existing `data-testid` is preserved so the 30+ existing browser suites keep
  their hooks (new testids are additive: `toast-region`, `toast`,
  `toast-dismiss`, `review-dirty-tag`, `*-count`, `capture-empty-link`,
  provider `*-legend`).
- Verification: existing Playwright suites must keep passing unmodified; new
  `tests/form-feedback.spec.ts` proves the five gaps behaviorally against
  the real components (Vite dev server plus route-mocked API, no database)
  and unit-asserts the helper; `web-token-sweep`'s source oracles keep
  passing because every new CSS declaration references tokens (verified in
  the light checks by `tsc`, `vite build` and the new suite, with full
  `verify.sh` deferred to the end of the batch per the batch plan).
- Privacy/compatibility: every assertion is on empty/generic states or
  structurally-attribute checks with fixture values only — no credential,
  token, PAT, Actor UUID, or signed-in session value in any committed file,
  fixture, or capture.

## Capabilities

### New Capabilities

- `web-form-feedback`: Five closed form-feedback gaps on the Web client
  (toast region, dirty-editor indicator, single conflict-copy helper,
  capture-form clarity, `maxLength` counters, fieldset/legend grouping) plus
  one standalone Playwright suite that proves all five.

## Non-goals

Backend changes of any kind (migrations, routes, envelope, approval
bindings, evidence rules, credential handling, validation limits — the
`20000` client-notes bound already exists in `Chronicleaf.Core` and is only
surfaced); new toast kinds beyond success/error; toast queuing or history
(one toast at a time, latest replaces, by design); per-field toast detail
(the toast names the outcome, the inline notice keeps the detail);
restyling any panel beyond the new feedback elements; mobile-viewport,
print or high-contrast design; new product workflows or routes; touching
HANDOFF.md, ROADMAP.md, or .gitignore (the HANDOFF pointer is a separate
commit per the standing rule); new production dependency; no new remote, no
push, no publication. No implementation, archive, commit, push or deployment
in this authoring work.
