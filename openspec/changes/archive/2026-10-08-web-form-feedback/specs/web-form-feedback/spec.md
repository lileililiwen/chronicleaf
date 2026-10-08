## ADDED Requirements

### Requirement: WEB-FEED-001 Toast region for form outcomes

The Web client SHALL render one always-present toast region (`role="status"`,
`aria-live="polite"`, fixed top-center) that shows one toast at a time (the
latest replaces), auto-dismisses 4s after the latest show, offers a dismiss
button, and is wired into the save/approve/remove success and failure paths
of at minimum `MilestoneEditor`, `ProjectEditor`, `CredentialVaultPanel`,
`MarkdownReviewPanel` and `ClientForm`.

#### Scenario: WEB-FEED-001 Success

- **WHEN** the owner saves a project, milestone, client or credential, or
  approves a review revision
- **THEN** a `role="status"` toast names the outcome, the toast survives a
  post-save navigation, a second outcome replaces the first, the toast leaves
  on its own after 4s, and the dismiss button clears it sooner.

#### Scenario: WEB-FEED-001 Failure

- **WHEN** a save, approval or removal is refused (including a 409
  stale-revision refusal)
- **THEN** an error toast carries the same message the inline notice shows
  (the shared conflict copy for 409s) while the inline notice itself is still
  rendered beside it: no toast is ever the only record of a failure.

#### Scenario: WEB-FEED-001 Boundary

- **WHEN** a panel saves without showing a toast, or two toasts render at
  once, or the region is missing `role="status"` / `aria-live="polite"`
- **THEN** the requirement fails: every wired panel announces every outcome
  exactly once through the single shell-level region.

### Requirement: WEB-FEED-002 Dirty-editor indicator

The review editor SHALL show a gold left border plus an "Unsaved changes" tag
in the title row while dirty, reusing design tokens only, and both SHALL
clear on save.

#### Scenario: WEB-FEED-002 Success

- **WHEN** the owner types in the review title or body
- **THEN** the editor wrapper gains its gold left border and the title row
  shows the `Unsaved changes` tag; after a successful save both are gone.

#### Scenario: WEB-FEED-002 Failure

- **WHEN** the editor is dirty but the tag or the border is absent (or either
  survives a successful save)
- **THEN** the requirement fails: dirtiness is never shown in one place but
  not the other, and never shown for stored text.

#### Scenario: WEB-FEED-002 Boundary

- **WHEN** the border or tag is styled with a raw color, spacing or type
  value outside the token set
- **THEN** the requirement fails: the indicator reuses tokens only, so the
  `web-token-sweep` source oracles keep passing unchanged.

### Requirement: WEB-FEED-003 Single revision-conflict copy

One `formatRevisionConflict(storedRevision: number | null): string` helper
SHALL be the only stale-revision wording in the Web client, used by
`MilestoneEditor`, `ProjectEditor` and the review panel's `describeFailure`
with identical wording everywhere.

#### Scenario: WEB-FEED-003 Success

- **WHEN** a milestone, project or review write loses a revision race
- **THEN** all three panels render byte-identical text for the same stored
  revision (a bare sentence for `null`, the same sentence naming the stored
  revision otherwise).

#### Scenario: WEB-FEED-003 Failure

- **WHEN** any panel renders a conflict sentence the helper did not produce
- **THEN** the requirement fails: a fourth wording anywhere in the Web tree
  is non-conformant.

#### Scenario: WEB-FEED-003 Boundary

- **WHEN** the review refusal carries no stored revision
- **THEN** `describeFailure` renders the helper's `null` sentence rather
  than inventing a revision or falling back to a private wording.

### Requirement: WEB-FEED-004 Capture-form clarity

The capture URL group SHALL hint "(at least 1 required)" and the capture
empty state SHALL link to `#capture-form`.

#### Scenario: WEB-FEED-004 Success

- **WHEN** the owner reads the capture form or its empty state
- **THEN** the URL group names the minimum (`at least 1 required`) and the
  empty state offers an anchor that reveals the form.

#### Scenario: WEB-FEED-004 Failure

- **WHEN** the form has no reachable `id="capture-form"` for the anchor, or
  the submit guard accepts an empty request the hint forbids (or vice versa)
- **THEN** the requirement fails: hint, guard and anchor agree on the
  minimum of one page.

#### Scenario: WEB-FEED-004 Boundary

- **WHEN** the milestone is archived (no form is offered)
- **THEN** no anchor is rendered: the empty state never links to a form that
  is not there.

### Requirement: WEB-FEED-005 Length counters and fieldset grouping

Every input/textarea with `maxLength` SHALL show a `<small
data-testid="<field>-count">` counter reading "n / max" that updates on
change, and the credential provider rows and the capture viewport group SHALL
be wrapped in `fieldset`/`legend` with the provider/viewport-group name as
the legend.

#### Scenario: WEB-FEED-005 Success

- **WHEN** the owner types in a bounded field (milestone title/notes,
  project name/description, client name/notes)
- **THEN** its counter reads the live length over the field's own bound, and
  every credential provider row groups under the provider's name while the
  viewport group keeps its `Viewports` legend.

#### Scenario: WEB-FEED-005 Failure

- **WHEN** a counter's `max` disagrees with its field's `maxLength`, or a
  provider row or the viewport group renders without its `fieldset`/`legend`
- **THEN** the requirement fails: the counter never lies about the bound and
  no group loses its name.

#### Scenario: WEB-FEED-005 Boundary

- **WHEN** an existing suite asserts the same rows, fields or legends
- **THEN** it passes unmodified: every existing `data-testid`, `id`, label
  and class survives, and all new testids are additive.
