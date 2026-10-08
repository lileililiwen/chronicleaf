# Design: Web accessibility core (web-accessibility-core)

## Context and implementation boundary
Repository Chronicleaf only. Primary boundary `src/Chronicleaf.Web/src/App.tsx`
(skip link, three `<main id="main" tabindex="-1">` landmarks wrapping the
signed-in product area, the signed-out single-card content and the public
journey view), `src/Chronicleaf.Web/src/styles.css` (one new `.skip-link`
rule and a finalized `.notice--error` rule that carries the same shape
the older `.session__error` and `.view__state--error` rules already
have, so visual behavior is unchanged), every panel that renders a
"Loading X…" `<p>` (15+ panels — see tasks 1.1 for the full list), the 15
form components listed in tasks 1.2 (each `<input>`/`<textarea>`/`<select>`
that has a sibling `FieldError`-style `<p>`), `src/Chronicleaf.Web/tests/a11y-core.spec.ts`
(new, run by `scripts/verify.sh` as the suite-1 stage alongside
`product-shell-ux.spec.ts`), `src/Chronicleaf.Web/package.json`
(`@axe-core/playwright` as a devDependency only), and the
`scripts/verify.sh` verification chain (one new stage). Depends on no
backend package and changes none: every API contract annotated here is
delivered and stays byte-identical. Preserve forge.yaml, .standard
receipts, unrelated files, HANDOFF.md (until the second HANDOFF-only
commit), ROADMAP.md, .gitignore, every existing `data-testid`, and
current_spec (set to `web-accessibility-core` in commit 1's preamble
step).

## Language, ownership and release
React + TypeScript + Vite for `src/Chronicleaf.Web`; no C#, no migration,
no API edit. The accessibility annotations live in the Web project; the
loading-state wrapper, the skip link, the landmark, the consolidated
class and the field-level wiring are shipped as in-tree JSX/CSS
augmentations, never as a third-party component library and never as a
runtime import from a CDN. Native dependencies pinned in lockfiles
during implementation: `@axe-core/playwright` enters the lockfile as a
devDependency at the version `package.json` selects; no package install
in this authoring task.

**Keep local.** A11y annotations are Chronicleaf-product presentation; no
matching recurring implementation is evidenced in sibling platform
contracts. Evidence inspected: dotnet-platform-libs/README.md lists
Platform.Jobs and Platform.Storage families but no Web shell or a11y
scaffold. No shared `/lib` added and no sibling edit authorized.

## Contract and compatibility decisions
Consume the delivered API contracts exactly as served (owner session,
projects/milestones, evidence, capture, generation, publication): same
paths, payloads, envelope `{code,message,traceId,fieldErrors?}`,
antiforgery/session handling, and revision/If-Match discipline. No new
API route, no envelope change, no approval-binding change, no
evidence-rule change. The five gaps the package closes are:

- **Skip link.** One `<a className="skip-link" href="#main">Skip to main
  content</a>` element is rendered as the first child of every shell
  (signed-out, signed-in loading, signed-in ready, journey). Visually
  hidden until focused (`.skip-link { position: absolute; left: -9999px;
  ... } .skip-link:focus { left: ...; outline: 2px solid
  var(--shell-accent); }`), targets `<main id="main">` so activating it
  moves the browser focus to the product landmark. Same gold focus ring
  the rest of the design uses (the `var(--shell-accent)` token).
- **`<main>` landmark.** Three distinct landmarks, all sharing
  `id="main"` and `tabIndex={-1}`: (1) the signed-out single-card
  content (the existing `<main className="shell">` already used on the
  signed-out branch — promote `id` and `tabindex`, also wrap the
  loading-state and signed-out panels); (2) the signed-in product area
  (`<div className="product">` becomes `<main id="main"
  className="product" tabIndex={-1}>`); (3) the public journey view
  (the existing `<main className="shell">` becomes
  `<main id="main" className="shell" tabIndex={-1}>`). The shell
  `<div className="shell shell--app">` stays the chrome and is not
  itself the landmark; the h1-per-page discipline is unchanged
  (the brand button stays a button, not a heading).
- **Loading-state announcements.** Every "Loading X…" `<p>` in
  `src/Chronicleaf.Web/src/features/**/*.tsx` becomes
  `<p aria-live="polite">`, and the enclosing `<section>` (or the
  panel's outer element) gains `aria-busy="true"`. The audit lists 15
  loading paragraphs across 14 files (see tasks 1.1); the change is
  applied by direct edit, not by a new wrapper component, because a
  wrapper would shadow the existing `data-testid`s the suites depend
  on. One small inline comment in App.tsx states the convention.
- **Single error class.** `.notice--error` already exists in
  `styles.css` (lines 686-691 and 702-705) carrying the same red border
  + red text the older `.session__error` and `.view__state--error` rules
  do. Add `role="alert"` to every render: the rename is
  `className="session__error"` → `className="notice--error" role="alert"`
  and `className="view__state view__state--error"` →
  `className="view__state notice--error" role="alert"`. Every existing
  `data-testid` is preserved verbatim so the 28+ existing browser
  suites keep their hooks. The two older CSS rules
  (`.session__error`, `.view__state--error`) stay in `styles.css` so a
  legacy capture that names them keeps matching; a later
  `web-token-sweep` will retire them. The cross-class sibling
  `.notice--conflict` keeps its `role="alert"` (it already has it) and
  is unchanged.
- **Field-level `aria-describedby` + `aria-invalid`.** For every
  `<input>`, `<textarea>` and `<select>` whose sibling renders a
  `FieldError`-style `<p>`, the input gains `aria-invalid="true"` (while
  the error is present) and `aria-describedby="<id>"` pointing at the
  error element's stable id (e.g. `id="project-name-error"`). The error
  element gains the matching `id`. When no error is present, the input
  is neither invalid nor described by the missing id. Cover at minimum
  the 15 components listed in tasks 1.2.

Existing Playwright suites keep passing unmodified: every `data-testid`
survives, the `e2e/shell.spec.ts` brand-heading assertion still finds
its target (the brand is still the h1 on the signed-out shell), and
the `product-shell-ux.spec.ts` "no scaffolding" assertion still finds
its absence. The new `tests/a11y-core.spec.ts` suite proves the five
gaps by DOM attribute (skip link first focusable, `<main id="main">`
on every page, `aria-live` on every loading `<p>`, `role="alert"` on
every error render with `.notice--error`, `aria-describedby` on every
field error) and by one axe-core smoke check per top-level nav entry
(Projects, Timeline, Operations, Credentials, Settings) to catch
adjacent violations the audit did not name.

## Behavioral model and invariants
- **One skip link per page, always first focusable.** The element is
  the first child of every `<body>`-level shell (signed-out, signed-in
  loading, signed-in ready, journey). Pressing Tab on a fresh page
  focuses the link; activating it moves focus to `<main id="main">` and
  the page scrolls to the top of the landmark. The link text is
  exactly "Skip to main content" so a screen reader announces it
  verbatim.
- **One `<main>` per page, always `id="main"`.** The landmark wraps the
  product area on the signed-in shell, the single-card content on the
  signed-out shell, and the public journey view. The chrome (nav,
  header, footer) stays outside the landmark. `tabIndex={-1}` makes
  the landmark programmatically focusable so the skip link can move
  focus into it without making it part of the tab order.
- **Loading state is announced.** A screen reader that encounters a
  "Loading X…" `<p aria-live="polite">` inside `<section aria-busy="true">`
  reads the loading message aloud when the section becomes busy and
  announces the content when the section becomes ready (the loading
  paragraph is replaced by content, the polite live region fires once
  per change, and `aria-busy` clears).
- **One error vocabulary.** Every error render in the Web tree is a
  single class (`.notice--error`) with `role="alert"`. A screen reader
  that encounters one announces it immediately on insertion. The two
  older classes stay in `styles.css` (see `web-token-sweep` below) so
  a legacy capture that names them keeps matching.
- **Field error is wired.** A focused input with a sibling error
  announces the error on focus (via `aria-describedby`) and carries
  `aria-invalid="true"` so an automated checker flags it. When the
  field is valid, neither attribute is set — `aria-invalid="false"`
  is intentionally avoided because the WAI-ARIA spec says absence is
  the valid state.

## Failure, boundary and rollback policy
A removed testid breaks its suite loudly (every testid is preserved);
a missing skip link fails the new suite (the first-focusable assertion
is structural); a missing landmark fails the new suite (the
`<main id="main">` assertion is structural); a missing live region
fails the new suite (the `aria-live` assertion is per-paragraph);
a missing `role="alert"` fails the new suite (the per-render
assertion). The axe-core smoke check fails the new suite on any
violation of the four impact levels (critical, serious, moderate,
minor) it surfaces for the navigation set. Unknown client-side path
keeps the delivered "Nothing here" view, now inside the `<main>` with
nav intact. Rollback is frontend-only: revert the Web tree and the
new e2e stage; no data, migration, API surface, credential,
antiforgery value or testid is touched, so stored evidence, approvals,
credentials and operations are unaffected. Partial wiring (a panel
left with `session__error`) fails the role-alert assertion, not
silently accepted. The two legacy class rules stay in `styles.css` so
a `web-token-sweep` package can retire them in a later change
without this one having to ship a CSS deletion.

## Verification oracle
Existing suites: every Playwright suite green unmodified, every
`data-testid` preserved, every `e2e/shell.spec.ts` and
`tests/product-shell-ux.spec.ts` assertion still finds its target.
New `tests/a11y-core.spec.ts`: skip link is the first focusable
element on every shell (signed-out, signed-in ready, journey);
`<main id="main">` is present and is the skip link's target;
every "Loading X…" `<p>` in the 15 audited panels carries
`aria-live="polite"`; every error render in the Web tree carries
`role="alert"` and `.notice--error` (the audit lists 20+ renders —
see tasks 1.1 / 1.2 for the full list); every field with a
`FieldError` carries `aria-invalid="true"` while the error is
present and `aria-describedby` pointing at the error's stable id;
one axe-core smoke check per top-level nav entry (Projects,
Timeline, Operations, Credentials, Settings) reports zero critical
or serious violations. Conformance probes: navigate the top-level
nav with the keyboard alone from the skip link into the `<main>` and
on through the nav; assert a screen reader announces every loading
state (structural: `aria-live` present) and every error (structural:
`role="alert"` present); assert the consolidated class is the only
one carrying `role="alert"` outside the existing
`.notice--conflict` notice.

## Decision ledger and activation gates
Phase: POST-MVP. DEMAND satisfied by the 2026-10-08 owner audit order —
no further activation needed. No unresolved product-policy decision in
this scoped contract; accessibility direction items the author refused
to guess are returned as open questions, not decided here.
Third-party versions are lockfile implementation inputs, not an
invitation to redesign contracts. Missing browsers/DB at implementation
time is recorded prerequisite unavailability, never fabricated
success.

### Owner-decided a11y decisions (owner-decided via the coordinator, 2026-10-08)
1. **Skip link target.** KEEP `<main id="main">` as the single landing
   landmark for the skip link, even though the signed-in shell has
   two visible regions (nav and product). One skip link, one target.
2. **Live region politeness.** KEEP `aria-live="polite"` (not
   `assertive`) for every loading state — the loading message is
   informational and an assertive announcement would interrupt the
   user's own narration of the previous page.
3. **Error role.** KEEP `role="alert"` for both the consolidated
   `.notice--error` and the existing `.notice--conflict` (which
   already has it). State notices (`.notice--ok`, `.notice--muted`)
   stay without `role="alert"` because they are not errors.
4. **`aria-invalid` semantics.** KEEP the absent-when-valid model: a
   field with no current error has neither `aria-invalid` nor
   `aria-describedby`. `aria-invalid="false"` is intentionally NOT
   used because the WAI-ARIA spec says absence is the valid state.
5. **Legacy class residue.** KEEP `.session__error` and
   `.view__state--error` in `styles.css` for this change. A later
   `web-token-sweep` package will retire them; this change adds the
   new class and migrates every reference, and leaves the legacy
   rules untouched so a capture / out-of-tree diff stays small.
6. **axe-core scope.** KEEP the smoke check to one page per
   top-level nav entry (5 calls), not the full panel surface. The
   audit is a structural regression test, not a full a11y
   certification.

## Requirement traceability
| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| WEB-A11Y-CORE-001 skip link | First focusable on every shell, targets `<main id="main">` | Skip-link + landmark decisions above | App.tsx + styles.css `.skip-link` rule | Success / Failure / Boundary | 1.1, 2.1, 3.1, 4.1 | Browser: first focusable, href="#main", target exists; gold focus ring on focus |
| WEB-A11Y-CORE-002 main landmark | `<main id="main" tabindex="-1">` on every shell, wraps the right region | Landmark decisions above | App.tsx (three landmarks: signed-out, signed-in product, journey) | Success / Failure / Boundary | 1.1, 2.2, 3.1, 4.1 | Browser: `<main id="main">` present, skip link target, tabindex -1 |
| WEB-A11Y-CORE-003 loading live region | Every "Loading X…" `<p>` has aria-live; section has aria-busy | Live-region decisions above | 15 audited `<p>` tags + enclosing `<section>` (see tasks 1.1) | Success / Failure / Boundary | 1.1, 2.3, 3.1, 4.1 | Browser: `aria-live="polite"` on every "Loading" `<p>`, `aria-busy` on enclosing section |
| WEB-A11Y-CORE-004 single error class | `.notice--error` + `role="alert"` on every error render | Class-vocabulary decisions above | styles.css finalized; 20+ JSX sites renamed (see tasks 1.1) | Success / Failure / Boundary | 1.1, 2.4, 3.1, 4.1 | Browser: every error render carries `.notice--error` and `role="alert"`, no `.session__error` / `.view__state--error` |
| WEB-A11Y-CORE-005 field aria-describedby | Every field error wired to its input via id + aria-describedby + aria-invalid | Field-wiring decisions above | 15 audited components (see tasks 1.2); preserve every data-testid | Success / Failure / Boundary | 1.1, 2.5, 3.1, 4.1 | Browser: focus a field with an error, assert aria-describedby points at the error id, aria-invalid="true" |
