# Proposal: Web accessibility roles (web-accessibility-roles)

## Why
Fifty-eight changes are implemented, verified and archived, and
`web-accessibility-core` closed five gaps (skip link, `<main>`
landmark, `aria-live` loading state, single error class,
`aria-describedby` field errors) — but a structural audit of the Web
client on 2026-10-08 shows five further gaps the core package did not
own. Headings skip levels on real views: the project timeline renders
an `h3` under the view `h1`, milestone rows render `h3` titles with
`h4` detail panels under an `h1` with no `h2` between, and the team
and tenancy panels render `h4` sections directly under their `h1`.
Selection semantics are inconsistent: the timeline project picker
carries `aria-pressed` chips with no `role="group"`, and the voice
area toggle carries `aria-expanded` with no `aria-controls` pointing
at the panel it shows. The `/settings/credentials` page has no
explicit active-nav rule in `App.tsx`, so its highlight depends on
the generic fallthrough rather than a named decision. Every
`badge--ok` / `badge--pending` / `badge--muted` status marker speaks
only through color, with no non-color marker for a reader who cannot
see the color. And `.action`, `.filter` and `button.link` controls
render below the 40px touch-target floor (chips near 29px). A screen
reader user meets the first three on the very first project page; a
low-vision user meets the fourth on every status; a touch user meets
the fifth on every filter row. This package closes the five gaps
without changing any backend contract, dependency, route, envelope,
approval binding, evidence rule, or class vocabulary the rest of the
design system owns. Status: Specified handoff; implementation not
started. Stage: POST-MVP (new package; DEMAND satisfied by the
2026-10-08 owner audit order, drain-next after
`web-accessibility-core`).

## What Changes
Five frontend-only fixes in `src/Chronicleaf.Web/src/`: sequential
heading hierarchy across every feature panel (one `h1` per view;
stray `h2`/`h3`/`h4`/`h5` promoted or demoted so each view reads
`h1→h2→h3` with no skips, tag changes only, classes untouched so
visuals are byte-identical); consistent selection semantics (top nav
and `ProjectSubNav` keep `aria-current="page"`, filter chips keep
`role="group"` + `aria-pressed` with the timeline picker gaining the
missing group role, the voice toggle gaining `aria-controls` onto the
controlled panel id); an explicit `isActive` rule for
`/settings/credentials` (no new top-level nav entry; unknown settings
sub-routes highlight nothing); a non-color status marker in every
color-coded badge (a data-status-derived `Status: ` accessible name
beside the text already present — the visible word is untouched, so
exact-text assertions keep passing unmodified);
and `min-block-size: 2.5rem` on `.action` and `.filter` plus
`button.link` with comfortable padding. One new Playwright suite
`tests/a11y-roles.spec.ts` proves every requirement end-to-end
against the real published app. No new production dependency, no C#,
no migration, no route, no envelope. See design.md.

## Package Boundary and Split Assessment
Own only the Web client roles surface: heading levels, selection
attributes, the credentials `isActive` rule, badge markers, and
touch-target minimums. Repository Chronicleaf, React + TypeScript +
Vite, no C# surface. Depends on no new package: every backend
contract this UI annotates is already delivered and stays
byte-identical. If a backend change proves unavoidable during
implementation, it is named as a separate future package and this one
stays frontend-only. The two deferred owner-BLOCKED packages
(`semantic-narrative-retrieval`, `hosted-managed-ai`) are unchanged
and are not a hidden sub-feature. This unit is accepted by its scoped
browser evidence and outcome, not by completion of the full product.
Excluded surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Heading levels, selection attributes, badge markers
and touch minimums are Chronicleaf-product presentation; no matching
recurring implementation is evidenced in sibling platform contracts.
Evidence inspected: dotnet-platform-libs/README.md lists
Platform.Jobs and Platform.Storage families but no Web shell, design
system or accessibility scaffold. Reuse dependency-owned API
contracts (session, projects, evidence, capture, generation,
publication) exactly as served; never duplicate those concerns. No
new dependency of any kind (the suite reuses the `@axe-core/playwright`
devDependency only if needed — it is not needed: every assertion here
is structural DOM, no axe scan is added). No shared `/lib` added and
no sibling edit authorized.

## BFS Impact Map
- Actor/flow: the signed-in single-user owner (community-access
  session gate stays the entry guard) plus the anonymous public
  journey reader (journey headings already read `h1→h2→h3` and are
  asserted unchanged).
- Boundary/callers: `src/Chronicleaf.Web/src/App.tsx` (one explicit
  `isActive` branch), `src/Chronicleaf.Web/src/styles.css` (three
  touch-target minimums, no other rule),
  every panel listed in tasks 1.1 for heading tags (tag-only edits,
  classes and `data-testid`s preserved verbatim),
  `src/Chronicleaf.Web/src/features/milestone-timeline/TimelinePage.tsx`
  (picker group role), `src/Chronicleaf.Web/src/features/voice-profiles/VoiceArea.tsx`
  (`aria-controls` + panel id), badge sites in tasks 1.1 (one `Status: `
  accessible name each), `src/Chronicleaf.Web/tests/a11y-roles.spec.ts`
  (new browser suite, own e2e group with its own disposable database
  as suite 29 — Playwright sorts spec files, so sharing suite 1
  would disturb its emptiness suites) and `scripts/e2e.sh` (suite-29
  block, port, log and token vars). No backend code; no C# file; no
  migration; no API contract change.
- Data/integration: no schema, no API, no envelope change; all API
  calls keep their existing paths, payloads and antiforgery/session
  handling; every existing `data-testid` is preserved so all existing
  browser suites keep their hooks; heading tags carry no suite hook
  except `product-shell-ux.spec.ts` (per-page `h1` count — one `h1`
  per view is preserved, so the assertion keeps passing) and
  `e2e/shell.spec.ts` (brand heading — untouched).
- Verification: existing Playwright suites must keep passing
  unmodified; new `tests/a11y-roles.spec.ts` proves the five gaps by
  DOM attribute and bounding-box measurement from its own
  installation (suite 29: no shared state, no seed, every record
  created by owner clicks).
- Privacy/compatibility: every assertion is on an empty/generic state
  or a uniquely-named record this suite creates and owns, containing
  no credential, token, PAT, Actor UUID, or signed-in session value.

## Capabilities
### New Capabilities
- `web-accessibility-roles`: Five closed roles gaps on the Web
  client (sequential headings, selection semantics, credentials
  active nav, non-color status markers, 40px touch targets) plus one
  Playwright regression suite that proves all five.

## Non-goals
Backend changes of any kind (migrations, routes, envelope, approval
bindings, evidence rules, credential handling); visual restyle (tag
changes only — classes, tokens and layout are untouched, so no pixel
moves except the touch-target floor itself); new product workflows or
routes; a new top-level nav entry (credentials stays a settings
sub-route); axe-core scope change (no new scan is added); mobile-viewport,
print or high-contrast design; TipTap or any editor rebuild; touching
HANDOFF.md, ROADMAP.md, or .gitignore (the HANDOFF pointer is a
separate commit per the standing rule); new dependency of any kind;
no new remote, no push, no publication. No implementation, archive,
commit, push or deployment in this authoring work.
