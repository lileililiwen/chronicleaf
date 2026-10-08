## ADDED Requirements

Visual-no-change invariant (applies to WEB-TOK-001..004): the
sweep SHALL change no computed value. Every replacement SHALL
resolve to the literal it replaces, exactly, so the cascade,
the computed styles and the painted pixels are identical
before and after in every viewport. The oracle is a
computed-style diff: 50+ probes (body font-size, card padding,
action padding, nav gap, page-heading size, shell padding and
the full ledger in the new suite) read against the shipped
stylesheets SHALL equal the pre-change values captured at the
start of the run. A probe that differs fails the change; no
screenshot in the existing DELTA set may move, and the suite
renders synthetic elements only, so no capture can bear a
credential, token, PAT, UUID or session value.

### Requirement: WEB-TOK-001 Sizing sweep with identical rendering
Every raw `rem`/`px` size in `styles.css` SHALL reference the
matching `--space-*` / `--font-size-*` token; where no token
fits, the token SHALL be added to `tokens.css` first. After
the sweep the `styles.css` source (comments stripped,
`var(...)` references stripped, `@media` and `z-index` lines
excluded) SHALL show zero numeric literals with `rem`/`px`/`em`
units. Unitless `0`, `100%`, `z-index` integers, media-query
`px` breakpoints and the `1fr` fraction stay literal by
design.

#### Scenario: WEB-TOK-001 Success
- **WHEN** the sweep is applied and the new suite runs
- **THEN** the stripped-source sizing grep shows zero matches
  and every computed-style probe (body `16px`, shell padding
  `32px`, panel padding `48px`, card padding `16px`, action
  padding `8px 15px`, nav gap `6px`, page heading `28px` and
  the full ledger) equals its captured pre-change value.

#### Scenario: WEB-TOK-001 Failure
- **WHEN** a raw sizing literal survives in `styles.css`, or a
  computed probe differs from its captured pre-change value
- **THEN** the requirement is unverified: the literal moves
  into tokens (or takes the matching token) and any differing
  replacement is reworked until identical — values are never
  "close enough".

#### Scenario: WEB-TOK-001 Boundary
- **WHEN** a declaration carries unitless `0`, `100%`, a
  `z-index` integer, a media-query `px` breakpoint, or the
  `1fr` fraction
- **THEN** it stays literal and the grep oracle excludes
  exactly those lines; anything else numeric-with-unit outside
  `tokens.css` (or outside a `var()` reference) is
  non-conformant.

### Requirement: WEB-TOK-002 Covered color, spacing and type literals
Every raw color, spacing or type literal in `styles.css`
already covered by a token SHALL reference that token, and no
hex literal SHALL appear outside `tokens.css`.

#### Scenario: WEB-TOK-002 Success
- **WHEN** the new suite runs against the sources and the
  built CSS
- **THEN** `styles.css` source shows zero hex literals and the
  hex set of the built `dist` CSS equals the hex set of
  `tokens.css` (token definitions are the only lawful hex).

#### Scenario: WEB-TOK-002 Failure
- **WHEN** a hex literal appears outside `tokens.css`, or the
  built CSS carries a hex no token defines
- **THEN** the requirement is unverified: the value moves
  into tokens or takes the matching token.

#### Scenario: WEB-TOK-002 Boundary
- **WHEN** a future panel needs a color no token names
- **THEN** the token is added to `tokens.css` first and then
  referenced; a local literal is never the answer and this
  requirement's oracle stays green by construction.

### Requirement: WEB-TOK-003 Line-height sweep
Every raw `line-height` in `styles.css` SHALL reference a
token: `1.5` SHALL take `var(--line-height-tight)`, `1.6`
SHALL take `var(--line-height-body)`, and the two orphans
SHALL take new `--line-height-snug` (`1.45`) and
`--line-height-draft` (`1.55`).

#### Scenario: WEB-TOK-003 Success
- **WHEN** the new suite runs
- **THEN** every `line-height:` declaration in `styles.css`
  references `var()`, and the computed line-heights (notice
  `22.5px`, empty body `24px`, draft body `21.7px`, review
  preview `23.68px`) equal their captured pre-change values.

#### Scenario: WEB-TOK-003 Failure
- **WHEN** a `line-height:` declaration carries a raw number,
  or a computed line-height differs from capture
- **THEN** the requirement is unverified and the declaration
  is reworked until identical.

#### Scenario: WEB-TOK-003 Boundary
- **WHEN** a `line-height` inherits (no declaration) or was
  already a token reference
- **THEN** it is left untouched; only raw numeric declarations
  are in scope.

### Requirement: WEB-TOK-004 Tracking sweep
`tokens.css` SHALL define `--tracking-tight: -0.01em` and
`--tracking-tighter: -0.02em` (plus `--tracking-wide: 0.04em`
for the uppercase labels), and every raw `letter-spacing` in
`styles.css` SHALL reference the matching tracking token.

#### Scenario: WEB-TOK-004 Success
- **WHEN** the new suite runs
- **THEN** every `letter-spacing:` declaration in `styles.css`
  references `var()`, `tokens.css` defines both required
  tokens with exactly the specified values, and the computed
  letter-spacings (page heading `-0.56px`, brand `-0.4px`)
  equal their captured pre-change values.

#### Scenario: WEB-TOK-004 Failure
- **WHEN** a `letter-spacing:` declaration carries a raw
  value, a required token is missing or mistyped, or a
  computed tracking differs from capture
- **THEN** the requirement is unverified and the declaration
  or token is reworked until identical.

#### Scenario: WEB-TOK-004 Boundary
- **WHEN** no other tracking value exists in the client
- **THEN** no further tracking token is added; the three
  tokens are the complete set and the oracle pins exactly
  them.
