# web-perf-and-animation Specification

## Purpose
TBD - created by archiving change web-perf-and-animation. Update Purpose after archive.
## Requirements
### Requirement: WEB-PERF-001 Sized lazy candidate images

The `ScreenshotCapturePanel` candidate `<img>` SHALL carry explicit
`width` and `height` from the delivered `candidate.width` /
`candidate.height`, plus `loading="lazy"` and `decoding="async"`.

#### Scenario: WEB-PERF-001 Success

- **WHEN** the owner opens a milestone whose capture list holds candidates
- **THEN** every candidate image reserves its payload-sized box before its
  bytes arrive (no layout shift when the list populates), loads lazily,
  and decodes asynchronously.

#### Scenario: WEB-PERF-001 Failure

- **WHEN** a candidate image renders without payload-equal dimensions, or
  without `loading="lazy"` / `decoding="async"`
- **THEN** the requirement fails: the box precedes the bytes on every
  candidate, with no exception for below-the-fold rows.

#### Scenario: WEB-PERF-001 Boundary

- **WHEN** a candidate's picture cannot be read (the delivered named
  candidate with no image)
- **THEN** the reserved box still applies and no broken-image control
  reads as a capture problem; no other `<img>` in the tree is restyled by
  this requirement.

### Requirement: WEB-PERF-002 Shared control transition

The Web client SHALL transition `.action`, `.nav__item`, `.filter`,
`.badge` and `button.link` with
`transition: color 120ms, background-color 120ms, border-color 120ms`,
with no new timing token.

#### Scenario: WEB-PERF-002 Success

- **WHEN** the owner hovers, activates or focuses any of the five control
  selectors
- **THEN** color, background-color and border-color ease over 120ms while
  the focus outline still appears instantly.

#### Scenario: WEB-PERF-002 Failure

- **WHEN** a sixth selector carries the transition, a fourth property is
  transitioned, or the interval differs from 120ms
- **THEN** the requirement fails: one transition, five selectors, three
  properties.

#### Scenario: WEB-PERF-002 Boundary

- **WHEN** the `web-token-sweep` source oracles run over the touched CSS
- **THEN** they pass unchanged: the transition declarations introduce no
  raw sizing, no hex, and no bare line-height or letter-spacing.

### Requirement: WEB-PERF-003 Container busy state on list owners

Every panel that owns a list (milestone list, timeline, operations,
credentials, clients, candidates, drafts, publications) SHALL set
`aria-busy={loading}` on its root section while its list is loading,
leaving the `web-accessibility-core` `aria-live` paragraphs untouched.

#### Scenario: WEB-PERF-003 Success

- **WHEN** a list-owning panel is reading its list
- **THEN** its root section reports `aria-busy="true"` alongside the
  announced loading paragraph, and reports `aria-busy="false"` once the
  list settles.

#### Scenario: WEB-PERF-003 Failure

- **WHEN** a root section reports busy while its list renders, or idle
  while its loading paragraph shows, or any loading paragraph is renamed
  or moved by this change
- **THEN** the requirement fails: busy is derived from the same flag that
  gates the loading render, and the announcement side is never touched.

#### Scenario: WEB-PERF-003 Boundary

- **WHEN** an existing suite asserts the same sections, lists or loading
  paragraphs
- **THEN** it passes unmodified: every existing `data-testid`, `id`,
  label, heading and class survives, and the one new wrapper testid is
  additive.

### Requirement: WEB-PERF-004 Reduced-motion guard

`styles.css` SHALL open with a `prefers-reduced-motion: reduce` guard that
collapses `transition-duration` to `0.01ms !important` on `*`,
`*::before` and `*::after`, with a comment naming this package.

#### Scenario: WEB-PERF-004 Success

- **WHEN** an owner who requests reduced motion hovers, activates or
  focuses any transitioned control
- **THEN** the end state applies with effectively no motion, while focus
  rings, colors and announcements keep their meaning.

#### Scenario: WEB-PERF-004 Failure

- **WHEN** the guard is absent from the built CSS, or any transition in
  the tree escapes it
- **THEN** the requirement fails: the guard covers every element and
  pseudo-element, `!important` included.

#### Scenario: WEB-PERF-004 Boundary

- **WHEN** the guard block is audited for token-sweep conformance
- **THEN** it introduces no raw sizing, hex, line-height or letter-spacing
  beyond time literals, and the package-naming comment survives minified
  or not (the source comment is the oracle, not the bundle).

