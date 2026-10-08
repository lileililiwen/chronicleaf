# web-accessibility-core Specification

## Purpose
Close the five accessibility gaps the 2026-10-08 audit named on the Web
client: visible-on-focus skip link, single `<main id="main">` landmark
per page, `aria-live` on every loading state, one consolidated
`role="alert"` error class, and `aria-invalid` + `aria-describedby`
wiring on every form field whose sibling renders a `FieldError`-style
error — each proven by a structural Playwright suite and one axe-core
smoke check per top-level nav entry.

## ADDED Requirements

### Requirement: WEB-A11Y-CORE-001 Skip link
The Web client SHALL render a single visible-on-focus skip link as the
first focusable element of every page (signed-out, signed-in loading,
signed-in ready, public journey), with text "Skip to main content",
targeting the product `<main id="main">` landmark, and styled with the
same gold focus ring the rest of the design uses.

#### Scenario: WEB-A11Y-CORE-001 Success
- **WHEN** the owner presses Tab on a fresh page in any shell
- **THEN** the skip link is the first focusable element, it carries
  `href="#main"`, the link text is exactly "Skip to main content", and
  activating it moves focus to `<main id="main">` with the page scrolled
  to the top of the landmark; the focus ring is gold (the
  `var(--shell-accent)` token)

#### Scenario: WEB-A11Y-CORE-001 Failure
- **WHEN** the skip link is absent, sits after another focusable
  element, names a missing target, or carries text other than "Skip to
  main content"
- **THEN** the requirement fails: the keyboard user has no way to
  bypass the nav and reach the product area in one Tab, and a screen
  reader announces whatever the page does carry first instead of the
  intended link

#### Scenario: WEB-A11Y-CORE-001 Boundary
- **WHEN** an existing Playwright suite asserts an element other than
  the skip link (notably `e2e/shell.spec.ts` on the global brand
  heading and `product-shell-ux.spec.ts` on the per-page h1)
- **THEN** the suite keeps passing: the brand heading is still the h1
  on the signed-out shell, the per-page h1s are unchanged on the
  signed-in shell, and the skip link is a sibling, not a replacement

### Requirement: WEB-A11Y-CORE-002 Main landmark
The Web client SHALL render a single `<main id="main" tabindex="-1">`
landmark on every page, wrapping the signed-in product area on the
ready shell, the single-card content on the signed-out / loading shell,
and the public journey view, with no other `<main>` element on the
page and with the shell chrome (nav, header, footer) outside the
landmark.

#### Scenario: WEB-A11Y-CORE-002 Success
- **WHEN** the owner visits any shell (signed-out, signed-in ready,
  public journey)
- **THEN** exactly one `<main id="main" tabindex="-1">` renders, the
  `id` is the same one the skip link targets, the product area / single
  card / journey view is the landmark's only child, and the nav,
  header and footer are siblings of the landmark rather than descendants

#### Scenario: WEB-A11Y-CORE-002 Failure
- **WHEN** the landmark is missing, carries a different id, or wraps
  the chrome rather than the product area
- **THEN** the requirement fails: a screen reader cannot offer "skip
  to main content" without a target, and the landmark semantics are
  ambiguous to assistive technology

#### Scenario: WEB-A11Y-CORE-002 Boundary
- **WHEN** a future change wants a second `<main>` for a sub-area
  (e.g. a dialog-as-main pattern)
- **THEN** the second landmark must carry a different `id` (so the
  skip link target stays unambiguous) and the requirement fails the
  moment two `<main id="main">` elements co-exist on one page

### Requirement: WEB-A11Y-CORE-003 Loading-state announcements
Every panel that renders a "Loading X…" `<p>` (a structural loading
placeholder) SHALL carry `aria-live="polite"` on the `<p>` and
`aria-busy="true"` on the enclosing `<section>` (or the panel's
outer element) while the loading state is shown, so a screen reader
announces the loading message and announces the content when the
loading state is replaced.

#### Scenario: WEB-A11Y-CORE-003 Success
- **WHEN** a panel renders its loading state
- **THEN** the loading `<p>` carries `aria-live="polite"`, the
  enclosing section carries `aria-busy="true"`, and a screen reader
  announces the loading message; when the section becomes ready the
  loading `<p>` is replaced and the polite live region announces the
  new content

#### Scenario: WEB-A11Y-CORE-003 Failure
- **WHEN** a loading `<p>` lacks `aria-live` or the enclosing section
  lacks `aria-busy`
- **THEN** the requirement fails: a screen reader never announces the
  loading state and the user is left to wonder whether the page is
  still working; an automated checker flags the missing live region

#### Scenario: WEB-A11Y-CORE-003 Boundary
- **WHEN** a panel renders a one-shot "no results" or "empty list"
  message that is NOT a loading placeholder
- **THEN** the requirement does not apply (no `aria-live` is added)
  because the message is terminal state, not a transition; the
  audit's structural probe is specifically the "Loading" `<p>`

### Requirement: WEB-A11Y-CORE-004 Single error class with role="alert"
Every error render in the Web client SHALL carry
`class="notice--error"` and `role="alert"`, replacing the older
`.session__error` and `.view__state--error` class names; the two
older class names SHALL NOT appear on any rendered element. The
consolidated class SHALL preserve the same red border + red text the
older classes carried. The two older CSS rules MAY remain in
`styles.css` so a legacy capture / out-of-tree diff stays small
(retirement is the responsibility of a later `web-token-sweep`).

#### Scenario: WEB-A11Y-CORE-004 Success
- **WHEN** an error render reaches the DOM
- **THEN** the element carries `class="notice--error"` and
  `role="alert"`, a screen reader announces the error on insertion,
  and the visual treatment (red border, red text) is unchanged from
  the prior `.session__error` / `.view__state--error` render

#### Scenario: WEB-A11Y-CORE-004 Failure
- **WHEN** an error render still carries `.session__error` or
  `.view__state--error` instead of `.notice--error`
- **THEN** the requirement fails: a screen reader may not announce
  the error (the legacy class is not paired with `role="alert"` in
  every site), and the visual treatment drifts from the consolidated
  vocabulary; the regression suite flags the legacy class

#### Scenario: WEB-A11Y-CORE-004 Boundary
- **WHEN** an existing Playwright suite asserts an error render's
  `data-testid`
- **THEN** the suite keeps passing: every `data-testid` is preserved
  verbatim; only the class and the `role` attribute change. The two
  legacy CSS rules remain in `styles.css`, so a stray element
  captured out-of-tree that still names one of the old classes keeps
  its visual treatment (and is caught by the regression suite that
  names no element by the old class)

### Requirement: WEB-A11Y-CORE-005 Field aria-describedby + aria-invalid
For every `<input>`, `<textarea>` and `<select>` whose sibling
renders a `FieldError`-style error message, the input SHALL carry
`aria-invalid="true"` while the error is present and
`aria-describedby="<id>"` pointing at the error element's stable id
(e.g. `id="project-name-error"`), and the error element SHALL carry
the matching `id`. When no error is present, neither attribute is
set on the input (absence is the valid state, not
`aria-invalid="false"`). Cover at minimum MilestoneEditor,
ProjectEditor, ClientForm, CredentialVaultPanel,
ScreenshotCapturePanel, GenerationPreviewPanel, VoiceProfilePanel,
GitHubConnectionPanel, LocalRepositoryPanel, PublicationPanel,
PublishForm, AdapterTargetPanel, ExportPanel, MarkdownReviewPanel
and NarrativeMemoryPanel.

#### Scenario: WEB-A11Y-CORE-005 Success
- **WHEN** the owner focuses a field that has a sibling field error
- **THEN** the focused field carries `aria-invalid="true"`, the field
  also carries `aria-describedby` pointing at the error element's
  `id`, the error element exists in the DOM with that `id`, and a
  screen reader reads the field name followed by the error message

#### Scenario: WEB-A11Y-CORE-005 Failure
- **WHEN** a field with a sibling field error carries no
  `aria-invalid` or no `aria-describedby`, or the `aria-describedby`
  target is absent or wrong
- **THEN** the requirement fails: a screen reader announces the
  field as valid even when the server has just refused the value,
  the error message is associated with the page rather than the
  field, and an automated checker flags the missing wiring

#### Scenario: WEB-A11Y-CORE-005 Boundary
- **WHEN** a field has no current error
- **THEN** the input carries neither `aria-invalid` nor
  `aria-describedby` (absence is the valid state per WAI-ARIA) and
  the regression suite asserts the attributes are unset, not that
  they are set to `"false"` or to an empty string

### Requirement: WEB-A11Y-CORE-006 Axe-core smoke + regression suite
A database-isolated browser suite (`tests/a11y-core.spec.ts`) SHALL
prove every WEB-A11Y-CORE-001..005 requirement end-to-end against the
real published application, run by `scripts/verify.sh` as the suite-1
stage alongside `product-shell-ux.spec.ts`, and SHALL additionally
run one axe-core smoke check per top-level nav entry (Projects,
Timeline, Operations, Credentials, Settings) reporting zero critical
or serious violations. `@axe-core/playwright` SHALL be a devDependency
only and SHALL NOT enter the production bundle.

#### Scenario: WEB-A11Y-CORE-006 Success
- **WHEN** the suite runs against the published app on its own
  database with the real access token
- **THEN** each structural assertion passes (skip link first
  focusable on every shell, `<main id="main">` present and
  reachable, every loading `<p>` carries `aria-live="polite"`, every
  error render carries `.notice--error` and `role="alert"`, every
  field with an error carries `aria-invalid="true"` and
  `aria-describedby`), and the five axe-core smoke checks report
  zero critical or serious violations

#### Scenario: WEB-A11Y-CORE-006 Failure
- **WHEN** any structural assertion fails or any axe-core smoke check
  reports a critical or serious violation
- **THEN** the gate fails loudly with the specific failure named
  (which page, which selector, which axe rule); a green gate is
  evidence the audit's five named gaps are closed and the audit
  itself reports no new critical or serious regression
