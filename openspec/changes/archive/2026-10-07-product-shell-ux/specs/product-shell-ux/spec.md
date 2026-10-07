## ADDED Requirements

### Requirement: PRODUCT-SHELL-UX-001 App shell and navigation
The Web client SHALL render one app shell with top-level navigation (Projects,
Timeline, Operations, Credentials, Settings), one header per page, session
internals visible only under Settings, and the core journey readable as one
ordered path.

#### Scenario: PRODUCT-SHELL-UX-001 Success
- **WHEN** the signed-in owner visits any product page
- **THEN** the shell shows the same nav with the current entry active, the
  page shows its own header (no repeated global header), and the core journey
  order (project → milestone → evidence → draft → approve → export) is
  reachable through nav order plus per-view next-step affordances.

#### Scenario: PRODUCT-SHELL-UX-001 Failure
- **WHEN** a page renders outside its nav entry's active state or reachable
  only through a back-link
- **THEN** the shell is non-conformant: every product area is one nav step
  away from every other, and session internals (actor identity, idle timeout,
  lifetime) render only under `/settings`, never on product pages.

#### Scenario: PRODUCT-SHELL-UX-001 Boundary
- **WHEN** an existing Playwright suite asserts a removed selector (notably
  `e2e/shell.spec.ts` on the global brand heading and `setup-pending` text)
- **THEN** the suite is updated inside this package's own commit with the
  migration named in evidence; no suite is deleted or silently weakened to
  make the gate pass.

### Requirement: PRODUCT-SHELL-UX-002 Design tokens across panels
The Web client SHALL define one design-token set (type scale, spacing scale,
color roles, button/input/card primitives) and apply it across all 17 feature
panels with no new ad-hoc styles.

#### Scenario: PRODUCT-SHELL-UX-002 Success
- **WHEN** any of the 17 feature panels renders
- **THEN** its type, spacing, color, buttons, inputs, and cards come from the
  shared tokens/primitives, the layout uses wide viewports instead of one
  narrow centered column, and visual hierarchy (header, section, action,
  state) is consistent across panels.

#### Scenario: PRODUCT-SHELL-UX-002 Failure
- **WHEN** a panel defines a one-off color, type ramp, spacing value, or
  control style outside the token set
- **THEN** the panel is non-conformant and the value moves into tokens or is
  replaced by a primitive; per-panel fixes for shared defects (e.g. the
  credentials label/input collision) are resolved in the primitive, not
  locally.

#### Scenario: PRODUCT-SHELL-UX-002 Boundary
- **WHEN** a panel is only partially restyled at verification time
- **THEN** the requirement fails rather than passing with ad-hoc residue:
  a source audit of the 17 folders shows no style defined outside tokens.

### Requirement: PRODUCT-SHELL-UX-003 Authoring-scaffolding purge
Production UI SHALL contain no spec-authoring or design-boundary text, and a
lint stage SHALL fail the gate if such text returns.

#### Scenario: PRODUCT-SHELL-UX-003 Success
- **WHEN** the production bundle is served and its sources are searched
- **THEN** no page, footer, or empty state renders strings such as "NOT IN
  THIS CHANGE", "Not in this change", "Project setup coming in a later
  change.", or any text naming a change, spec, or design boundary; empty
  states name the owner's next step instead.

#### Scenario: PRODUCT-SHELL-UX-003 Failure
- **WHEN** a forbidden scaffolding string is present in the Web tree or the
  served production bundle
- **THEN** the new `verify.sh` lint stage (e.g. a grep stage over the Web
  sources and built assets) fails loudly before test stages run.

#### Scenario: PRODUCT-SHELL-UX-003 Boundary
- **WHEN** the lint stage runs against a tree with a deliberately planted
  forbidden string versus the clean tree
- **THEN** the planted tree fails and the clean tree passes, proving the
  stage selects its target rather than matching nothing.

### Requirement: PRODUCT-SHELL-UX-004 Screenshot acceptance discipline
Every UI requirement in this package SHALL be proven by Playwright screenshots
compared against the archived 2026-10-07 capture set (DELTA-SCREENSHOTS),
with differences justified; DOM assertions remain for behavior while
screenshots gate appearance.

#### Scenario: PRODUCT-SHELL-UX-004 Success
- **WHEN** implementation is verified
- **THEN** new captures (minimum one per nav entry: Projects/dashboard,
  project-new, Timeline, Operations, Credentials, Settings, in
  empty/generic states) are diffed against the closest archived 2026-10-07
  capture, each difference is justified in HANDOFF evidence, and all
  pre-existing Playwright suites pass unmodified or with in-package,
  explicitly named selector migrations.

#### Scenario: PRODUCT-SHELL-UX-004 Failure
- **WHEN** a UI requirement is asserted by DOM text alone with no screenshot,
  or a screenshot shows a non-empty/non-generic state
- **THEN** the requirement is unverified: any capture bearing a credential,
  token, PAT, Actor UUID, or signed-in session value is rejected and retaken
  in an empty/generic state.

#### Scenario: PRODUCT-SHELL-UX-004 Boundary
- **WHEN** a justified visual difference cannot be attributed to this
  package's restyle (e.g. data-dependent rendering)
- **THEN** the capture is retaken in the deterministic empty/generic fixture
  state; the discipline never justifies away an unexplained difference.
