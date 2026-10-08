# web-accessibility-roles Specification

## Purpose
TBD - created by archiving change web-accessibility-roles. Update Purpose after archive.
## Requirements
### Requirement: WEB-A11Y-ROLES-001 Sequential heading hierarchy
The Web client SHALL render exactly one `h1` per view with every
other heading nested sequentially (`h1→h2→h3`, never stepping up
more than one level), across the dashboard, projects list, project
detail areas, timeline page, settings, credentials, operations,
clients, and review panel views.

#### Scenario: WEB-A11Y-ROLES-001 Success
- **WHEN** the signed-in owner visits any named view (including a
  project detail area over a project with a confirmed milestone, the
  timeline page with a chosen project, and the review panel)
- **THEN** the view carries exactly one `h1`, and walking its
  headings in DOM order never steps up more than one level (the
  ProjectOverview timeline heading and the MilestoneRows sections
  nest under the view `h1` as `h2`s with `h3` children).

#### Scenario: WEB-A11Y-ROLES-001 Failure
- **WHEN** a view renders an `h3` with no `h2` above it (notably the
  timeline heading under the view `h1`, a milestone-row title, or a
  team/tenancy section), a second `h1`, or any other skipped level
- **THEN** the requirement fails: a screen reader announcing the
  heading tree reports a jump, and the view is non-conformant until
  the stray heading is promoted or demoted into sequence.

#### Scenario: WEB-A11Y-ROLES-001 Boundary
- **WHEN** an existing Playwright suite asserts headings (notably
  `product-shell-ux.spec.ts` on exactly one `h1` per nav page and
  `e2e/shell.spec.ts` on the brand heading)
- **THEN** the suite keeps passing unmodified: the retag moves tags
  only, every `className` and `data-testid` stays verbatim, and the
  per-view `h1` count is unchanged.

### Requirement: WEB-A11Y-ROLES-002 Consistent selection semantics
Selection controls SHALL use one vocabulary per pattern: top nav and
`ProjectSubNav` carry `aria-current="page"`; filter chips
(`MilestoneRows`, `OperationsPanel`, timeline picker) render inside
`role="group"` with `aria-pressed`; the voice toggle is a disclosure
with `aria-expanded` plus `aria-controls` naming the controlled
panel id.

#### Scenario: WEB-A11Y-ROLES-002 Success
- **WHEN** the owner filters milestones, operations, or the timeline
  project choice, or opens the writing-voice area
- **THEN** every chip sits in a labelled `role="group"` and carries
  `aria-pressed`, and the voice toggle carries `aria-expanded` with
  `aria-controls` resolving to the visible panel id.

#### Scenario: WEB-A11Y-ROLES-002 Failure
- **WHEN** a chip carries `aria-pressed` outside a group (notably the
  timeline picker), or the voice toggle carries `aria-expanded`
  with no `aria-controls` target
- **THEN** the requirement fails: a screen reader announces a state
  with no scope, or a disclosure that names nothing.

#### Scenario: WEB-A11Y-ROLES-002 Boundary
- **WHEN** no filter option is active (the `all` chip) or the voice
  area is closed
- **THEN** the group and the `aria-controls` reference still render:
  `aria-pressed="false"` is the honest unpressed state and the
  closed disclosure still names its panel, so the structure is
  asserted identical in both states.

### Requirement: WEB-A11Y-ROLES-003 Settings/credentials active nav
`/settings/credentials` (and only real settings sub-routes) SHALL
highlight the correct top-nav entry through an explicit `isActive`
rule in `App.tsx`, with no new top-level nav entry.

#### Scenario: WEB-A11Y-ROLES-003 Success
- **WHEN** the owner deep-links `/settings/credentials` (no click,
  a fresh document load)
- **THEN** the Credentials nav entry carries `aria-current="page"`
  and no other entry does.

#### Scenario: WEB-A11Y-ROLES-003 Failure
- **WHEN** the credentials page shows no active nav item, highlights
  the wrong entry, or the change adds a top-level nav entry to fix it
- **THEN** the requirement fails: the page is unreachable-by-glance
  from the nav, or the nav grew a duplicate for a settings sub-route.

#### Scenario: WEB-A11Y-ROLES-003 Boundary
- **WHEN** the owner visits an unknown settings sub-route
  (e.g. `/settings/no-such-page`)
- **THEN** no nav entry carries `aria-current`: only real settings
  sub-routes highlight, and an unknown address guesses no parent.

### Requirement: WEB-A11Y-ROLES-004 Status not by color alone
Every `badge--ok` / `badge--pending` / `badge--muted` render SHALL
pair its color with the text already present plus a non-color marker
(a data-status-derived `Status: ` accessible name), covering timeline
entries, credential rows, and milestone rows.

#### Scenario: WEB-A11Y-ROLES-004 Success
- **WHEN** any badge with a color modifier renders (confirmed /
  candidate / dismissed milestone, timeline entry, credential state,
  archived marker, curation primary, evidence availability)
- **THEN** the badge carries its visible status word plus a `Status: `
  accessible name, so a screen reader announces the named status and
  the accessible-name computation names it without the color.

#### Scenario: WEB-A11Y-ROLES-004 Failure
- **WHEN** a modifier badge renders its color with text but no
  non-color marker
- **THEN** the requirement fails: the status speaks through color
  alone for every reader who cannot see it.

#### Scenario: WEB-A11Y-ROLES-004 Boundary
- **WHEN** a badge has no color modifier (plain `.badge`, e.g. an
  operation-row status word with its explanatory sentence beside it)
- **THEN** the prefix is not required: the requirement scopes to
  `badge--ok` / `badge--pending` / `badge--muted` renders, and the
  plain-badge row already pairs its word with a full sentence.

### Requirement: WEB-A11Y-ROLES-005 Touch targets
`.action` and `.filter` controls SHALL measure at least 40px on the
block axis (`min-block-size: 2.5rem`); `button.link` SHALL meet the
same floor with comfortable padding; nav rows and card action rows
SHALL keep their layout.

#### Scenario: WEB-A11Y-ROLES-005 Success
- **WHEN** the owner views any filter row, action row, or crumb row
  (dashboard, operations, milestone filters, timeline picker, card
  areas)
- **THEN** every `.action`, `.filter` and `button.link` control
  measures ≥40px tall, and nav rows and card action rows still wrap
  and align as before.

#### Scenario: WEB-A11Y-ROLES-005 Failure
- **WHEN** any `.action`, `.filter` or `button.link` control
  measures below 40px on the block axis
- **THEN** the requirement fails: the control is below the touch
  floor and the minimum does not bind it.

#### Scenario: WEB-A11Y-ROLES-005 Boundary
- **WHEN** a control is disabled, hidden, or off-screen (notably a
  filter row above a loading state, or actions inside a closed
  disclosure)
- **THEN** it is excluded from measurement: only rendered, visible
  controls are asserted, so a loading state cannot fail the floor.

