# publisher-ghost-adapter Specification

## Purpose
TBD - created by archiving change publisher-ghost-adapter. Update Purpose after archive.
## Requirements
### Requirement: PUBLISHER-GHOST-ADAPTER-001 Ghost explicit target conversion
The Ghost adapter SHALL convert only the approved snapshot for an explicitly configured target and confirmed mode.

#### Scenario: PUBLISHER-GHOST-ADAPTER-001 Success
- **WHEN** an owner confirms a current target preview
- **THEN** the exact transformed snapshot is sent to the declared target

#### Scenario: PUBLISHER-GHOST-ADAPTER-001 Failure
- **WHEN** the target credentials or permissions are invalid
- **THEN** dispatch fails without exposing secrets or changing source approval

#### Scenario: PUBLISHER-GHOST-ADAPTER-001 Boundary
- **WHEN** the target configuration changes after preview
- **THEN** the previous preview cannot authorize a write

### Requirement: PUBLISHER-GHOST-ADAPTER-002 Ghost failure receipts
The Ghost adapter SHALL preserve duplicate-safe receipts and avoid blind retries of ambiguous upstream writes.

#### Scenario: PUBLISHER-GHOST-ADAPTER-002 Success
- **WHEN** the upstream returns a remote id
- **THEN** the receipt records the id and exact content hash

#### Scenario: PUBLISHER-GHOST-ADAPTER-002 Failure
- **WHEN** the upstream may have committed before timeout
- **THEN** the receipt is unknown until supported or manual reconciliation

#### Scenario: PUBLISHER-GHOST-ADAPTER-002 Boundary
- **WHEN** the same local operation is submitted again
- **THEN** the original receipt is returned without another upstream write

