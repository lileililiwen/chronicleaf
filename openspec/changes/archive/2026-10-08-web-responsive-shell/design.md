# Design: Web responsive shell (web-responsive-shell)

## Context and implementation boundary
Repository Chronicleaf only. Primary boundary
`src/Chronicleaf.Web/src/App.tsx` (menu disclosure state, header
session control, bottom bar removed),
`src/Chronicleaf.Web/src/features/community-access/Settings/SessionGate.tsx`
(signed-in branch becomes the compact header row),
`src/Chronicleaf.Web/src/styles.css` (sticky header, menu,
sub-nav scroll, `480/720/960px` breakpoints, narrow repairs, card
grid),
`src/Chronicleaf.Web/src/features/projects-and-milestones/ProjectDetail.tsx`
(chain crumb above the `h1`),
`src/Chronicleaf.Web/src/features/projects-and-milestones/MilestoneEditor.tsx`
(chain crumb plus `onNavigate` prop and project-name read),
`src/Chronicleaf.Web/tests/responsive-shell.spec.ts` (new, own e2e
group as suite 30 in `scripts/e2e.sh` with its port `5110`, log
and token vars). Depends on no backend package and changes none:
every API contract annotated here is delivered and stays
byte-identical. Preserve forge.yaml, .standard receipts,
unrelated files, HANDOFF.md (until the second HANDOFF-only
commit), ROADMAP.md, .gitignore, every existing `data-testid`,
every existing class name outside the six gaps, and current_spec
(set to `web-responsive-shell`; HANDOFF.md already points there).

## Language, ownership and release
React + TypeScript + Vite for `src/Chronicleaf.Web`; no C#, no
migration, no API edit. The responsive annotations live in the Web
project; the disclosure, breakpoints, sticky chrome, session row,
card grid and crumbs are shipped as in-tree JSX/CSS augmentations,
never as a third-party component library and never as a runtime
import from a CDN. No package install in this change (no new
dependency of any kind).

**Keep local.** Responsive presentation is Chronicleaf-product
presentation; no matching recurring implementation is evidenced in
sibling platform contracts. Evidence inspected:
dotnet-platform-libs/README.md lists Platform.Jobs and
Platform.Storage families but no Web shell or responsive
scaffold. No shared `/lib` added and no sibling edit authorized.

## Contract and compatibility decisions
Consume the delivered API contracts exactly as served (owner
session, projects/milestones): same paths, payloads, envelope
`{code,message,traceId,fieldErrors?}`, antiforgery/session
handling, and revision/If-Match discipline. No new API route, no
envelope change, no approval-binding change, no evidence-rule
change. The six gaps the package closes are:

- **Mobile nav.** At `<=720px` the six-entry top nav collapses
  behind one `Menu` disclosure button (`data-testid="nav-menu"`,
  `aria-expanded` + `aria-controls="app-nav"`); opening it renders
  the same six entries as a vertical list overlay, and choosing
  one navigates and closes it. The nav itself gains `id="app-nav"`
  (the target the disclosure and the `#app-nav` suite selector
  name) and keeps `aria-label="Product"`, every `data-testid`,
  and `aria-current="page"` on the active entry in both states.
  Above `720px` the button hides and the nav renders exactly as
  today, so every existing suite (which runs at `1440px`) keeps
  its hooks unmodified. The project sub-nav (`ProjectSubNav`,
  `data-testid="project-subnav"`) keeps all six areas and its
  `aria-current` convention; at `<=720px` it stops wrapping and
  scrolls on one row (`flex-wrap: nowrap`, `overflow-x: auto`).
- **Breakpoints.** Exactly three media queries — `480px`,
  `720px`, `960px` — tuning nav, product header, timeline and
  milestone rows. No new layout system: the queries retune
  padding, gaps, wrapping and stacking on the delivered
  flex/grid, nothing is rebuilt.
- **Narrow-viewport repairs.** At `<=480px`: `.shell__panel`
  padding `3rem → 1.5rem`; `.session__input` `min-width: 16rem`
  `→ 0` with `width: 100%`; `.session__form` `align-items: end`
  `→ stretch` so wrapped rows align. Tag, class and testid
  vocabulary untouched.
- **Sticky header.** `.app__header` becomes `position: sticky`
  with `top: 0`, `background: var(--shell-background)`, a
  stacking `z-index`, and its existing `border-bottom` kept. It
  stays in flow, so it never covers the first section; anchored
  jumps land below it through `scroll-margin-top` on the product
  surface.
- **Session bar relocation.** The signed-in `SessionGate` leaves
  the bottom of every product page and renders inside
  `.app__header` as one compact row: the `Owner session` heading
  (kept, so every sign-in helper still finds the heading it waits
  on), the actor label ("this installation's owner", the same
  label the settings page renders, never a raw identity), the
  sign-out control (`data-testid="session-sign-out"`) and the
  details link (`data-testid="session-details"`) in one flex row.
  The bottom placement is deleted, including the `/settings`
  exact-path exception (there is no bottom bar left to except).
  Signed-out and loading branches are byte-identical.
- **Card grid and breadcrumbs.** At `>=960px` `.cards` becomes
  `repeat(auto-fit, minmax(20rem, 1fr))`: two columns on wide
  viewports, one column below, with no breakpoint below `960px`
  touched. The card stays a `button` (`.card`) and the
  per-area deep-links stay sibling `button.link`s in
  `.card__areas`. Every project-area view (overview, evidence,
  evolution, content, publish, automations) and the milestone
  editor render `Projects › <name> › <area>` above the `h1` in
  the existing `.view__crumbs` paragraph: the `Projects` crumb
  keeps `data-testid="project-back"` and navigates back, the
  name is the stored project name (an ellipsis while loading,
  never a placeholder record), and the area is the area label
  (`Overview`, `Evidence`, `Content`, `Publish`, `Evolution`,
  `Automations`; `Milestone` in the editor).

Existing Playwright suites keep passing unmodified: every
`data-testid` survives, the per-page single-`h1` assertion still
finds exactly one `h1` (the header control carries an `h2`),
the `Owner session` heading assertions still find their target,
and the nav `aria-current` assertions still find theirs. The new
`tests/responsive-shell.spec.ts` suite proves the six gaps by
viewport (`360/720/960/1280px` via `setViewportSize`), computed
style (`position: sticky`, `overflow-x`, grid tracks) and DOM
(disclosure operable, session bar inside `header`, crumb chain
text).

## Behavioral model and invariants
- **One nav, two postures.** Wide it is the delivered row;
  narrow it is one disclosure opening the same six entries as a
  vertical list. Both postures name the current page
  (`aria-current`) and the closed disclosure still names its
  panel, so the structure is asserted identical in both states.
- **Scroll, never wrap, on the sub-nav.** Narrow viewports keep
  all six areas reachable on one scrollable row rather than a
  wrapped block; wide viewports wrap exactly as today.
- **Chrome stays reachable.** The sticky header keeps brand,
  nav, menu and session controls on screen on long pages without
  covering the content it sits above.
- **Session state lives in the header.** Sign-out and session
  details are header chrome on every product page; the page body
  below holds product content only.
- **Cards widen, crumbs orient.** Wide viewports read two cards
  across; every project page names where the reader is
  (`Projects › <name> › <area>`) above its `h1`.

## Failure, boundary and rollback policy
A removed testid breaks its suite loudly (every testid is
preserved, `app-nav` gains the `id` the disclosure names); a
menu that fails to open at `360px` fails the new suite
(disclosure assertions); a header with computed `position`
other than `sticky` fails the new suite; a session bar outside
`header` fails the new suite; a single-column grid at `1280px`
fails the new suite (bounding-box comparison); a project area
without the three-part chain fails the new suite. Unknown
client-side path keeps the delivered "Nothing here" view
unchanged. Rollback is frontend-only: revert the Web tree and
the e2e.sh suite-30 lines; no data, migration, API surface,
credential, antiforgery value or testid is touched, so stored
evidence, approvals, credentials and operations are unaffected.
Partial work (a gap left unclosed at verification time) fails
its requirement, not silently accepted.

## Verification oracle
Existing suites: every Playwright suite green unmodified, every
`data-testid` preserved, every `product-shell-ux.spec.ts`,
`a11y-core.spec.ts`, `a11y-roles.spec.ts` and `e2e/shell.spec.ts`
assertion still finds its target. New
`tests/responsive-shell.spec.ts` (suite 30, own installation,
`360/720/960/1280px` viewports): menu disclosure present and
operable at `360px` (present at `720px`, hidden at `1280px`),
sub-nav one-row scrollable (`flex-wrap: nowrap` +
`overflow-x: auto`), header computed `position: sticky` with
`top: 0`, exactly one `session-gate` inside `header` with actor
label + sign-out + details in one row, two-column cards at
`1280px` (two project cards side by side), `Projects › <name> ›
<area>` chain on all six areas plus the milestone editor.
Conformance probes: keyboard Tab reaches the Menu button and the
disclosure opens with Enter; a no-CSS read of a crumb still
names all three parts in order.

## Decision ledger and activation gates
Phase: POST-MVP. DEMAND satisfied by the 2026-10-08 owner audit
order — no further activation needed. No unresolved
product-policy decision in this scoped contract. Third-party
versions are lockfile implementation inputs, not an invitation
to redesign contracts. Missing browsers/DB at implementation time
is recorded prerequisite unavailability, never fabricated
success.

### Owner-decided responsive decisions (owner-decided via the coordinator, 2026-10-08)
1. **Collapse point.** KEEP `720px` as the single nav collapse
   point. One disclosure below, the delivered row above; no
   intermediate posture.
2. **Disclosure shape.** KEEP one `Menu` button opening a
   vertical list overlay of the same six entries. No hamburger
   icon font, no drawer animation, no new dependency.
3. **Sub-nav shape.** KEEP all six areas on one horizontally
   scrollable row at `<=720px`. No dropdown, no area is hidden
   behind a second disclosure.
4. **Breakpoint set.** KEEP exactly three media queries —
   `480px`, `720px`, `960px`. No new layout system; the queries
   retune the delivered flex/grid only.
5. **Narrow repairs.** KEEP the three named repairs
   (`.shell__panel` padding, `.session__input` width,
   `.session__form` alignment) and no others. Tokens, palette,
   type and spacing stay exactly where `product-shell-ux` left
   them.
6. **Sticky scope.** KEEP `position: sticky` on `.app__header`
   only, with the shell background, a z-index, and the existing
   border. No other element becomes sticky.
7. **Session placement.** KEEP the session bar in the header as
   one compact row (actor label + sign-out + details link) on
   every product page, with the bottom placement deleted. The
   `Owner session` heading and all three session testids stay,
   so no existing suite is migrated.
8. **Card grid.** KEEP `auto-fit minmax(20rem, 1fr)` at
   `>=960px`. The card stays a `button`; deep-links stay
   siblings; no breakpoint below `960px` is touched.
9. **Crumb vocabulary.** KEEP `Projects › <name> › <area>` in
   the existing `.view__crumbs` paragraph above the `h1`. The
   `Projects` crumb keeps `data-testid="project-back"`; no new
   crumb class is added.

## Requirement traceability
| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| WEB-RESP-001 mobile nav | Menu disclosure + scrolling sub-nav | Collapse-point + disclosure-shape + sub-nav-shape decisions above | App.tsx disclosure + styles.css menu/sub-nav rules | Success / Failure / Boundary | 1.1, 2.1, 3.1, 4.1 | Browser at 360px: disclosure opens the six entries, navigates + closes; sub-nav one row |
| WEB-RESP-002 breakpoints | 480/720/960 queries | Breakpoint-set decisions above | styles.css three media queries | Success / Failure / Boundary | 1.1, 2.2, 3.1, 4.1 | Browser + source: three queries tuning nav/header/timeline/rows, no layout rebuild |
| WEB-RESP-003 narrow repairs | Panel/input/form fixes | Narrow-repairs decisions above | styles.css 480px rules | Success / Failure / Boundary | 1.1, 2.3, 3.1, 4.1 | Browser at 360px: panel padding, full-width input, stretched form rows |
| WEB-RESP-004 sticky header | Sticky app header | Sticky-scope decisions above | styles.css header rules | Success / Failure / Boundary | 1.1, 2.4, 3.1, 4.1 | Browser: computed position sticky top 0, first section unobscured |
| WEB-RESP-005 session in header | Header session row | Session-placement decisions above | App.tsx header + SessionGate compact branch | Success / Failure / Boundary | 1.1, 2.5, 3.1, 4.1 | Browser: one session-gate inside header, bottom placement gone, testids kept |
| WEB-RESP-006 cards + crumbs | Two-column grid + chains | Card-grid + crumb-vocabulary decisions above | styles.css cards query + ProjectDetail/MilestoneEditor crumbs | Success / Failure / Boundary | 1.1, 2.6, 3.1, 4.1 | Browser at 1280px: two cards across; chain on six areas + editor |
