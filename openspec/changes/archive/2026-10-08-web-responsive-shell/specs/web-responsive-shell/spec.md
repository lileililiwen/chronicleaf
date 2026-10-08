## ADDED Requirements

### Requirement: WEB-RESP-001 Mobile navigation disclosure
At viewports `<=720px` the six-entry top nav SHALL collapse behind
one `Menu` disclosure button opening the same six entries as a
vertical list overlay, and the project sub-nav SHALL scroll on one
row; both keep their `aria-current` / `aria-expanded` semantics.

#### Scenario: WEB-RESP-001 Success
- **WHEN** the signed-in owner visits any product page at `360px`
  wide
- **THEN** one `Menu` button (`aria-expanded="false"`,
  `aria-controls="app-nav"`) stands where the nav row was;
  opening it shows all six entries as a vertical list, choosing
  one navigates and closes the list, and the active entry keeps
  `aria-current="page"` in both states; the project sub-nav shows
  all six areas on one horizontally scrollable row.

#### Scenario: WEB-RESP-001 Failure
- **WHEN** the disclosure is missing, opens no list, lists fewer
  than six entries, or drops `aria-current` / `aria-expanded` at
  `360px`, or the sub-nav wraps to a second row
- **THEN** the requirement fails: narrow-viewport navigation is
  unreachable-by-glance or unannounced.

#### Scenario: WEB-RESP-001 Boundary
- **WHEN** the viewport is `720px` versus wider (notably `1280px`)
- **THEN** the disclosure is present at `720px` and hidden above
  it, where the nav renders exactly as before with no disclosure
  in the tab order.

### Requirement: WEB-RESP-002 Responsive breakpoints
The Web client SHALL tune nav, product header, timeline, and
milestone rows through exactly three media queries — `480px`,
`720px`, `960px` — with no new layout system.

#### Scenario: WEB-RESP-002 Success
- **WHEN** the owner resizes across the three breakpoints
- **THEN** nav, product header, timeline entries, and milestone
  rows retune (padding, gaps, wrapping, stacking) on the
  delivered flex/grid; no element rebuilds its layout model.

#### Scenario: WEB-RESP-002 Failure
- **WHEN** a fourth breakpoint appears, or a tuned surface
  rebuilds its layout (a new grid/flex model outside `.cards`)
- **THEN** the requirement fails: the package retunes, it does
  not redesign.

#### Scenario: WEB-RESP-002 Boundary
- **WHEN** an existing Playwright suite runs at its delivered
  `1440px` viewport
- **THEN** it keeps passing unmodified: above `960px` only the
  card grid changes, and no selector, testid, or heading moves.

### Requirement: WEB-RESP-003 Narrow-viewport repairs
At viewports `<=480px` the Web client SHALL render
`.shell__panel` with `1.5rem` padding (not `3rem`),
`.session__input` with `min-width: 0` and `width: 100%` (not
`min-width: 16rem`), and `.session__form` with
`align-items: stretch` (not `end`).

#### Scenario: WEB-RESP-003 Success
- **WHEN** the owner signs in or reads a shell panel at `360px`
  wide
- **THEN** the panel padding measures `1.5rem`, the token input
  spans its row without overflowing it, and wrapped form rows
  align through stretching.

#### Scenario: WEB-RESP-003 Failure
- **WHEN** any of the three computed values still reads the wide
  value at `360px`
- **THEN** the requirement fails: the narrow viewport still
  overflows or collides.

#### Scenario: WEB-RESP-003 Boundary
- **WHEN** the viewport is wider than `480px`
- **THEN** the delivered values hold (`3rem` panel padding,
  `16rem` input minimum, `end`-aligned form): the repairs bind
  only where they are needed.

### Requirement: WEB-RESP-004 Sticky application header
`.app__header` SHALL be `position: sticky` with `top: 0`, the
shell background, a stacking `z-index`, and its existing
`border-bottom`, and SHALL NOT obscure first-section content.

#### Scenario: WEB-RESP-004 Success
- **WHEN** the owner scrolls a long product page (notably
  evidence or timeline views)
- **THEN** the header — brand, nav or Menu, session row — stays
  on screen with computed `position: sticky` and `top: 0`, and
  the first section below it reads fully, including after an
  anchored jump.

#### Scenario: WEB-RESP-004 Failure
- **WHEN** the header scrolls away, computes to any other
  `position`, or covers the first section it sits above
- **THEN** the requirement fails: chrome that leaves on a long
  page, or chrome that hides content, is the gap restated.

#### Scenario: WEB-RESP-004 Boundary
- **WHEN** no other element claims sticky positioning
- **THEN** `.app__header` is the only sticky element: the
  package pins the chrome, never the content.

### Requirement: WEB-RESP-005 Session bar in the header
The signed-in session bar SHALL render inside `.app__header` as
one compact row (actor label, sign-out control, details link),
and SHALL NOT render at the bottom of any product page.

#### Scenario: WEB-RESP-005 Success
- **WHEN** the signed-in owner visits any product page
  (including `/settings`)
- **THEN** exactly one `session-gate` renders, inside the
  `header`, as one row reading the `Owner session` heading, the
  actor label, the sign-out control, and the details link; no
  session bar renders in the page body.

#### Scenario: WEB-RESP-005 Failure
- **WHEN** a session bar still renders below the product area on
  any page, or the header row drops the actor label, the
  sign-out, or the details link
- **THEN** the requirement fails: the relocation is total, and
  the row is complete.

#### Scenario: WEB-RESP-005 Boundary
- **WHEN** an existing Playwright suite asserts session hooks
  (notably the sign-in helpers waiting on the `Owner session`
  heading and the `session-sign-out` / `session-details`
  controls)
- **THEN** the suite keeps passing unmodified: the heading and
  all three session testids survive the move verbatim, and the
  row carries no Actor UUID, idle timeout, or lifetime text.

### Requirement: WEB-RESP-006 Card grid and project breadcrumbs
At viewports `>=960px` `.cards` SHALL lay out as
`repeat(auto-fit, minmax(20rem, 1fr))`, and every project-area
view (overview, evidence, evolution, content, publish,
automations) plus the milestone editor SHALL show
`Projects › <name> › <area>` above the `h1` in `.view__crumbs`.

#### Scenario: WEB-RESP-006 Success
- **WHEN** the signed-in owner reads the dashboard or projects
  list at `1280px` with at least two projects, or opens any
  project area or the milestone editor
- **THEN** two cards sit side by side (the card stays a
  `button`, the per-area deep-links stay sibling links), and
  each project page carries the three-part chain above its `h1`
  with the stored project name and the area label.

#### Scenario: WEB-RESP-006 Failure
- **WHEN** cards still stack one per row at `1280px`, a card
  stops being a `button`, a deep-link moves inside the card
  button, or a project area or the editor shows no chain
- **THEN** the requirement fails: wide viewports that waste
  their width, or project pages that do not name where the
  reader is, are the gaps restated.

#### Scenario: WEB-RESP-006 Boundary
- **WHEN** the viewport is narrower than `960px`, or the project
  record is still loading
- **THEN** cards stack exactly as before and the chain shows
  the area with an ellipsis for the unloaded name: no layout
  moves below the breakpoint, and no placeholder record is
  ever drawn.
