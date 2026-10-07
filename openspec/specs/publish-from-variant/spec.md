# publish-from-variant Specification

## Purpose
TBD - created by archiving change publish-from-variant. Update Purpose after archive.
## Requirements
### Requirement: PUBLISH-FROM-VARIANT-001 Target list over the delivered registry
The API SHALL serve the publisher registry's classified targets over a
private read route, field for field, with no credential, body or picture.

#### Scenario: PUBLISH-FROM-VARIANT-001 Success
- **WHEN** the signed-in owner reads `GET /api/publisher-targets`
- **THEN** the route answers 200 with the registry's targets in order,
each carrying its target id, draft/publish support, reconcile support
and export-only flag, naming the six transport targets plus the
export-only medium and linkedin entries.

#### Scenario: PUBLISH-FROM-VARIANT-001 Failure
- **WHEN** the route is read without a session
- **THEN** the delivered boundary answers 401 and serves no target.

#### Scenario: PUBLISH-FROM-VARIANT-001 Boundary
- **WHEN** the owner reads the list on an installation with no
credential stored and no content recorded
- **THEN** the route still answers the full classification, because a
registry describes what targets mean, not what this installation can
send today.

### Requirement: PUBLISH-FROM-VARIANT-002 Publish action on an approved variant row
The Web client SHALL offer a Publish action on each approved
content-variant row that opens a target picker and runs preview →
explicit confirm → receipt through the delivered publisher routes,
showing the server's answer verbatim.

#### Scenario: PUBLISH-FROM-VARIANT-002 Success
- **WHEN** the owner picks an approved variant, picks a target, previews,
confirms with an idempotency key, and publishes
- **THEN** the preview authorizes nothing, the 202 answers the watched
operation with its attempt id, an identical resubmission answers the
original receipt, and the receipt names the sent revision, target, mode
and the remote side's answer.

#### Scenario: PUBLISH-FROM-VARIANT-002 Failure
- **WHEN** the server refuses (unconfirmed, unknown target, export-only
target, unapproved or stale approval, stale preview, missing credential,
validation)
- **THEN** the panel shows the envelope code and message verbatim and
records no dispatch; a draft or invalidated variant offers no publish
control at all.

#### Scenario: PUBLISH-FROM-VARIANT-002 Boundary
- **WHEN** the variant panel merely renders, derives, or approves
- **THEN** no preview and no publish is issued: both stay behind
explicit owner acts, and the suite proves no such POST fired before
the clicks.

### Requirement: PUBLISH-FROM-VARIANT-003 Pickers replace hand-typed publishing ids
The Web client SHALL replace the hand-typed asset/target ids in
`PublicationPanel` and the hand-typed asset ids in `JourneyPanel` with
pickers over delivered list routes, keeping every existing trigger and
refusal identical.

#### Scenario: PUBLISH-FROM-VARIANT-003 Success
- **WHEN** the owner opens the Publish area or the journey selection
- **THEN** milestones list from the project route, assets list per
milestone from the draft read plus the variant list, and targets list
from the target route; each option is labelled by kind/type and
revision, never a bare identifier alone.

#### Scenario: PUBLISH-FROM-VARIANT-003 Failure
- **WHEN** the server refuses a picked preview, publish, or journey
selection
- **THEN** the panel shows the envelope code and message verbatim,
exactly as the hand-typed controls did — the migrated publications and
journey-publish suites assert the same expectations without weakening.

#### Scenario: PUBLISH-FROM-VARIANT-003 Boundary
- **WHEN** a milestone holds no draft and no variants, or no milestone
exists yet
- **THEN** the picker shows the honest empty state and offers no phantom
option; the publication attempt lookup stays hand-typed because no list
route serves attempts (future package `publication-history`).

### Requirement: PUBLISH-FROM-VARIANT-004 Configured GitHub Pages target binds and previews
A GitHub Pages section the operator configured SHALL register its target
and serve previews against the bound values, because the browser proof
needs one working transport and a section that registers but 500s is a
publishing dead-end no picker can route around.

#### Scenario: PUBLISH-FROM-VARIANT-004 Success
- **WHEN** the operator configures a valid GitHub Pages repository, branch
and subdirectory
- **THEN** the target list names `github-pages` with its capabilities and
a preview against it renders CPU-only without reaching any remote side.

#### Scenario: PUBLISH-FROM-VARIANT-004 Failure
- **WHEN** the configured section stops validating on reload
- **THEN** the adapter refuses rather than sending under a half-read
target, through the existing per-call re-validation.

#### Scenario: PUBLISH-FROM-VARIANT-004 Boundary
- **WHEN** no GitHub Pages section is configured
- **THEN** nothing registers and the slot answers `publisher_unknown_target`,
exactly as before — and the identical binding defect in the five other
adapters stays out of scope (future package `publisher-options-binding`).

