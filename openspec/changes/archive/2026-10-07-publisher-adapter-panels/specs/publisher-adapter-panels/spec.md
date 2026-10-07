# Spec: Publisher adapter panels (publisher-adapter-panels)

## ADDED Requirements

### Requirement: PUBLISHER-ADAPTER-PANELS-001 Configured sections bind through production registration
Each of the five repaired adapters (devto, hashnode, ghost, wordpress,
custom-blog) SHALL register its target from a valid operator section and
serve previews against the bound values, because a section that registers
but 500s is a publishing dead-end no picker can route around.

#### Scenario: PUBLISHER-ADAPTER-PANELS-001 Success
- **WHEN** the operator configures a valid section for one of the five targets
- **THEN** the target list names it with its capabilities and a preview
against it renders CPU-only with a non-empty preview hash.

#### Scenario: PUBLISHER-ADAPTER-PANELS-001 Failure
- **WHEN** the configured section stops validating on reload
- **THEN** the adapter refuses rather than sending under half-read values,
through the existing per-call re-validation.

#### Scenario: PUBLISHER-ADAPTER-PANELS-001 Boundary
- **WHEN** no section is configured for a target
- **THEN** nothing registers and the slot answers `publisher_unknown_target`,
exactly as before.

### Requirement: PUBLISHER-ADAPTER-PANELS-002 Per-adapter target surfaces behind the target picker
The Web client SHALL show an adapter-specific surface for the picked target
inside the shared publish flow: the registry classification served
field-for-field, what the transport sends and needs, and — after preview —
the adapter's own target line as the bound configuration the confirm would
authorize. Preview → explicit confirm → receipt stays the delivered shared
flow over the delivered routes.

#### Scenario: PUBLISHER-ADAPTER-PANELS-002 Success
- **WHEN** the owner picks an approved variant or asset, picks a transport
target, previews, confirms with an idempotency key, and publishes
- **THEN** the adapter surface names the transport and its bound
configuration, the 202 answers the watched operation with its attempt id,
an identical resubmission answers the original receipt, and the receipt
names the sent revision, target, mode and the remote side's answer.

#### Scenario: PUBLISHER-ADAPTER-PANELS-002 Failure
- **WHEN** the server refuses (unconfirmed, unknown target, unapproved or
stale approval, stale preview, missing credential, validation)
- **THEN** the panel shows the envelope code and message verbatim and
records no dispatch; a draft or invalidated variant offers no publish
control at all.

#### Scenario: PUBLISHER-ADAPTER-PANELS-002 Boundary
- **WHEN** the publish surface merely renders
- **THEN** no preview and no publish is issued: both stay behind explicit
owner acts, and the suite proves no such POST fired before the clicks.

### Requirement: PUBLISHER-ADAPTER-PANELS-003 Modes honor the registry; export-only refusals surface verbatim
The mode control SHALL offer only the modes the picked target's registry
entry serves, and the export-only entries (medium, linkedin) SHALL be
pickable so that the server's own refusal is what the owner reads — never a
fake send and never a client-side invention of the refusal.

#### Scenario: PUBLISHER-ADAPTER-PANELS-003 Success
- **WHEN** the owner picks a target serving a single mode
- **THEN** the mode control offers exactly that mode.

#### Scenario: PUBLISHER-ADAPTER-PANELS-003 Failure
- **WHEN** the owner previews against an export-only target
- **THEN** the server refuses `publisher_target_export_only` (422) and the
panel shows the envelope code and message verbatim with no dispatch and no
receipt.

#### Scenario: PUBLISHER-ADAPTER-PANELS-003 Boundary
- **WHEN** the owner picks an export-only target but does not preview
- **THEN** nothing is sent and no refusal is shown: the hand-carry guidance
names the export flow, and the refusal exists only as the server's answer
to an actual preview.
