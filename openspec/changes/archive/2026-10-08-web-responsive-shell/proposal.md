# Proposal: Web responsive shell (web-responsive-shell)

## Why
Fifty-nine changes are implemented, verified and archived, and
`web-accessibility-roles` closed five roles gaps (sequential headings,
selection semantics, credentials active nav, non-color status, 40px
touch targets) — but a narrow-viewport audit of the Web client on
2026-10-08 shows six further gaps no package ever owned. At phone
widths the six-entry top nav wraps into an unreadable multi-row
block with no collapse control, and the six-area project sub-nav
wraps the same way; there is no media query anywhere in the client
(`styles.css` ships zero `@media` rules), so the 44rem shell panel,
the 16rem session input and the end-aligned session form overflow or
collide below 480px; the app header scrolls away, leaving a
nav-less page on long evidence and timeline views; the signed-in
session bar sits at the bottom of every product page, below the
content it signs out of; dashboard and projects-list cards stack in
one column even on wide monitors; and project areas show only a
"← Projects" back-link where a reader expects the
"Projects › <name> › <area>" chain the rest of the IA implies. A
phone user meets the first three on the very first page; a desktop
user meets the last two on every project. This package closes the
six gaps without changing any backend contract, dependency, route,
envelope, approval binding, evidence rule, or class vocabulary the
rest of the design system owns. Status: Specified handoff;
implementation not started. Stage: POST-MVP (new package; DEMAND
satisfied by the 2026-10-08 owner audit order, drain-next after
`web-accessibility-roles`).

## What Changes
Six frontend-only fixes in `src/Chronicleaf.Web/src/`: a `Menu`
disclosure collapsing the six-entry top nav at `<=720px` (vertical
list overlay, `aria-expanded` + `aria-controls`, `aria-current`
kept) with the project sub-nav scrolling on one row; `480px` /
`720px` / `960px` media queries tuning nav, product header,
timeline and milestone rows (no new layout system); narrow-viewport
repairs (`.shell__panel` padding `3rem → 1.5rem` at `<=480px`,
`.session__input` `min-width: 16rem → 0` with `width: 100%`,
`.session__form` `align-items: end → stretch`); a sticky
`.app__header` (`position: sticky`, `top: 0`, shell background,
z-index, border kept, first-section content never obscured); the
signed-in session bar relocated from the bottom of every product
page into the header as one compact row (actor label + sign-out +
details link, testids kept); and a two-column `.cards` grid at
`>=960px` (`auto-fit minmax(20rem,1fr)`, card stays a `button`,
deep-links stay siblings) plus a `Projects › <name> › <area>`
breadcrumb above the `h1` on every project-area view and the
milestone editor, reusing `.view__crumbs`. One new Playwright suite
`tests/responsive-shell.spec.ts` proves every requirement
end-to-end at `360/720/960/1280px` viewports against the real
published app. No new production dependency, no C#, no migration,
no route, no envelope. See design.md.

## Package Boundary and Split Assessment
Own only the Web client responsive surface: the menu disclosure,
the three breakpoints, the narrow-viewport repairs, the sticky
header, the session-bar relocation, the card grid, and the
breadcrumbs. Repository Chronicleaf, React + TypeScript + Vite, no
C# surface. Depends on no new package: every backend contract this
UI annotates is already delivered and stays byte-identical. If a
backend change proves unavoidable during implementation, it is
named as a separate future package and this one stays
frontend-only. The two deferred owner-BLOCKED packages
(`semantic-narrative-retrieval`, `hosted-managed-ai`) are unchanged
and are not a hidden sub-feature. This unit is accepted by its
scoped browser evidence and outcome, not by completion of the full
product. Excluded surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Nav collapse, breakpoints, sticky chrome, session
placement, card grids and breadcrumbs are Chronicleaf-product
presentation; no matching recurring implementation is evidenced in
sibling platform contracts. Evidence inspected:
dotnet-platform-libs/README.md lists Platform.Jobs and
Platform.Storage families but no Web shell, design system or
responsive scaffold. Reuse dependency-owned API contracts
(session, projects, evidence, capture, generation, publication)
exactly as served; never duplicate those concerns. No new
dependency of any kind (the suite reuses the Playwright project /
viewport pattern only). No shared `/lib` added and no sibling edit
authorized.

## BFS Impact Map
- Actor/flow: the signed-in single-user owner (community-access
  session gate stays the entry guard) on viewports `360–1280px`
  wide; the anonymous public journey reader is untouched (journey
  shell keeps its own centered panel, no nav, no session bar).
- Boundary/callers: `src/Chronicleaf.Web/src/App.tsx` (menu
  disclosure state, header session control, bottom bar removed),
  `src/Chronicleaf.Web/src/features/community-access/Settings/SessionGate.tsx`
  (signed-in branch becomes the compact header row; signed-out and
  loading branches byte-identical),
  `src/Chronicleaf.Web/src/styles.css` (sticky header, menu,
  sub-nav scroll, three breakpoints, narrow repairs, card grid —
  no new layout system, every `data-testid` and every class name
  outside the six gaps preserved verbatim),
  `src/Chronicleaf.Web/src/features/projects-and-milestones/ProjectDetail.tsx`
  (chain crumb above the `h1`) and `MilestoneEditor.tsx` (+
  `onNavigate` prop, project-name read for the chain),
  `src/Chronicleaf.Web/tests/responsive-shell.spec.ts` (new
  browser suite, own e2e group with its own disposable database
  as suite 30 — it creates projects, so sharing suite 1 would
  disturb its emptiness suites) and `scripts/e2e.sh` (suite-30
  block, port `5110`, log and token vars). No backend code; no C#
  file; no migration; no API contract change.
- Data/integration: no schema, no API, no envelope change; all API
  calls keep their existing paths, payloads and antiforgery/session
  handling; every existing `data-testid` is preserved
  (`session-gate`, `session-heading`, `session-sign-out`,
  `session-details`, `project-back`, `app-nav`, all nav entries)
  so all existing browser suites keep their hooks; the
  `product-shell-ux.spec.ts` single-`h1` assertion still finds
  exactly one `h1` (the header session control carries an `h2`,
  never an `h1`), and every sign-in helper still finds the visible
  `Owner session` heading it waits on.
- Verification: existing Playwright suites must keep passing
  unmodified; new `tests/responsive-shell.spec.ts` proves the six
  gaps by viewport (`360/720/960/1280px`), computed style
  (`position: sticky`, `overflow-x`, grid tracks) and DOM
  (disclosure operable, session bar inside `header`, crumb chain
  text) from its own installation (suite 30: no shared state, no
  seed, every record created by owner clicks).
- Privacy/compatibility: every assertion is on an empty/generic
  state or a uniquely-named record this suite creates and owns,
  containing no credential, token, PAT, Actor UUID, or signed-in
  session value; the header actor label repeats the settings-page
  label ("this installation's owner"), never a raw identity.

## Capabilities
### New Capabilities
- `web-responsive-shell`: Six closed responsive gaps on the Web
  client (mobile nav disclosure, three breakpoints,
  narrow-viewport repairs, sticky header, session bar in header,
  card grid plus breadcrumbs) plus one Playwright regression
  suite that proves all six at four viewports.

## Non-goals
Backend changes of any kind (migrations, routes, envelope, approval
bindings, evidence rules, credential handling); visual restyle
beyond the six gaps (tokens, palette, type and spacing stay
exactly where `product-shell-ux` left them); a new layout system
(grid/flex rewrites outside `.cards`); new product workflows or
routes; print or high-contrast design; touching HANDOFF.md,
ROADMAP.md, or .gitignore (the HANDOFF pointer is a separate
commit per the standing rule); new dependency of any kind; no new
remote, no push, no publication. No implementation, archive,
commit, push or deployment in this authoring work.
