# Proposal: Web accessibility core (web-accessibility-core)

## Why
Fifty-seven changes are implemented, verified and archived, and every
backend requirement §1–§11 maps to delivered code — but the Web client
satisfies no a11y bar. A live UI audit (2026-10-08, captures held locally)
shows five concrete gaps that a screen reader and a keyboard user feel on
the very first page: the loading state in every panel is a plain `<p>` a
screen reader never announces; every form that can render a field error
shows the message but the input stays neutral so the field looks valid
until focus returns; a single error render uses two class vocabularies
(`.session__error` and `.view__state--error`) with no shared `role="alert"`
on the second; the signed-in product area is a `<div class="product">` and
the signed-out panel is a `<main>` with no `id`, so a "skip to main content"
link has no landmark to target; and no page offers one. The product works
without the user being able to find the work, and a later audit will not
catch any of this from a screenshot. This package closes the five gaps
without changing any backend contract, dependency, route, envelope,
approval binding, evidence rule, or class vocabulary the rest of the
design system owns. Status: Specified handoff; implementation not started.
Stage: POST-MVP (new package; DEMAND satisfied by 2026-10-08 owner audit
order). No drainable package remains ahead of it (HANDOFF records
`workflow-wayfinding` as the most recent change and no `current_spec`).

## What Changes
Add one accessibility core to the Web client: a visible-on-focus skip link
as the first focusable element of every page, targeting a single
`<main id="main" tabindex="-1">` landmark that wraps the signed-in product
area, the signed-out single-card content, and the public journey view;
`aria-live="polite"` on every "Loading X…" `<p>` with `aria-busy` on the
enclosing section; one consolidated `.notice--error` class carrying
`role="alert"` for the two older error vocabularies (`.session__error` and
`.view__state--error`); and `aria-invalid` + `aria-describedby` wiring on
every form input whose sibling renders a field-level error. One new
Playwright suite `tests/a11y-core.spec.ts` plus `@axe-core/playwright` as a
devDependency prove every requirement end-to-end against the real
published app. No new production dependency, no C#, no migration, no
route, no envelope. See design.md.

## Package Boundary and Split Assessment
Own only the Web client accessibility core: the skip link, the `<main>`
landmark, the loading-state live region, the consolidated error class, and
the field-level `aria-describedby` wiring. Repository Chronicleaf, React +
TypeScript + Vite, no C# surface. Depends on no new package: every
backend contract this UI annotates is already delivered and stays
byte-identical. If a backend change proves unavoidable during
implementation, it is named as a separate future package and this one
stays frontend-only. The two deferred owner-BLOCKED packages
(`semantic-narrative-retrieval`, `hosted-managed-ai`) are unchanged and
are not a hidden sub-feature. The class-vocabulary sweep the design names
is explicitly NOT in this package: a later `web-token-sweep` will retire
the older `.session__error` and `.view__state--error` rules; this change
adds the new class, migrates every reference, and leaves the old rules
untouched so an out-of-tree diff stays small. This unit is accepted by its
scoped browser evidence and outcome, not by completion of the full
product. Excluded surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Accessibility annotations, the skip link and the live
region are Chronicleaf-product presentation; no matching recurring
implementation is evidenced in sibling platform contracts. Evidence
inspected: dotnet-platform-libs/README.md lists Platform.Jobs and
Platform.Storage families but no Web shell, design system or
accessibility scaffold. Reuse dependency-owned API contracts (session,
projects, evidence, capture, generation, publication) exactly as served;
never duplicate those concerns. The one new devDependency
(`@axe-core/playwright`) is scoped to the test bundle — it is not in
`dependencies`, is never reached from production sources, and is loaded
only by `tests/a11y-core.spec.ts`. No shared `/lib` added and no sibling
edit authorized.

## BFS Impact Map
- Actor/flow: the signed-in single-user owner (community-access session
  gate stays the entry guard) plus the anonymous public journey reader.
- Boundary/callers: `src/Chronicleaf.Web/src/App.tsx` (skip link, three
  `<main>` landmarks, journey `<main>`), `src/Chronicleaf.Web/src/styles.css`
  (new `.skip-link` and finalized `.notice--error` rules),
  `src/Chronicleaf.Web/src/features/**/Panel.tsx` for every panel that
  renders a "Loading X…" `<p>` (15+ panels — see tasks 1.1), every
  component listed in tasks 1.2 for the `aria-describedby` wiring
  (15 components), and `src/Chronicleaf.Web/tests/a11y-core.spec.ts` (new
  browser suite). No backend code; no C# file; no migration; no API
  contract change.
- Data/integration: no schema, no API, no envelope change; all API calls
  keep their existing paths, payloads and antiforgery/session handling;
  every existing `data-testid` is preserved so the 28+ existing browser
  suites keep their hooks; the two older error classes
  (`.session__error`, `.view__state--error`) keep their existing CSS
  rules so legacy capture selectors keep matching (a later
  `web-token-sweep` will retire them — see non-goals).
- Verification: existing Playwright suites must keep passing
  unmodified; new `tests/a11y-core.spec.ts` proves the five gaps by
  DOM attribute and by axe-core smoke check; one new
  `npm run test:e2e` stage in `scripts/verify.sh` runs the new suite
  against the first e2e group's installation (no new PostgreSQL
  required — the suite asserts structural attributes and an empty
  state, never a populated one, so it does not dirty the database
  the way suites 2–28 do).
- Privacy/compatibility: every capture, every focused element and every
  assertion is on an empty/generic state or a structurally-attribute
  test that contains no credential, token, PAT, Actor UUID, or
  signed-in session value; the axe-core smoke is a single-page probe
  per top-level nav entry (signed-in core flow only — the journey view
  is excluded because it is anonymous and public).

## Capabilities
### New Capabilities
- `web-accessibility-core`: Five closed accessibility gaps on the Web
  client (skip link, `<main>` landmark, `aria-live` loading state,
  single error class with `role="alert"`, `aria-describedby` field
  errors) plus one Playwright regression suite that proves all five.

## Non-goals
Backend changes of any kind (migrations, routes, envelope, approval
bindings, evidence rules, credential handling); class-vocabulary sweep or
token-driven restyle of the error classes (a later `web-token-sweep` will
retire the legacy rules; this change preserves them in place and adds a
new consolidated class); mobile-viewport, print or high-contrast design;
new product workflows or routes; TipTap or any editor rebuild; touching
HANDOFF.md, ROADMAP.md, or .gitignore (the HANDOFF pointer is a separate
commit per the standing rule); new production dependency
(`@axe-core/playwright` is devDependency only); bringing
`@axe-core/playwright` into the production bundle; expanding the
`role="alert"` consolidation to the existing `.notice--conflict` and
`.notice--ok` rules (those are state notices, not error notices, and
their own role is a separate decision); touching the `e2e/shell.spec.ts`
brand-heading assertion (the brand is still the h1 on the signed-out
shell — only the signed-in shell moves its h1 inside the `<main>`); no
new remote, no push, no publication. No implementation, archive, commit,
push or deployment in this authoring work.
