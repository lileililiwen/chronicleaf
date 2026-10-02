## ADDED Requirements

### Requirement: PUBLISHER-DEVTO-ADAPTER-001 Dev.to explicit target conversion
The Dev.to adapter SHALL convert only the approved snapshot for an explicitly configured target and confirmed mode.

#### Scenario: PUBLISHER-DEVTO-ADAPTER-001 Success
- **WHEN** an owner confirms a current target preview
- **THEN** the exact transformed snapshot is sent to the declared target

#### Scenario: PUBLISHER-DEVTO-ADAPTER-001 Failure
- **WHEN** the target credentials or permissions are invalid
- **THEN** dispatch fails without exposing secrets or changing source approval

#### Scenario: PUBLISHER-DEVTO-ADAPTER-001 Boundary
- **WHEN** the target configuration changes after preview
- **THEN** the previous preview cannot authorize a write

### Requirement: PUBLISHER-DEVTO-ADAPTER-002 Dev.to failure receipts
The Dev.to adapter SHALL preserve duplicate-safe receipts and avoid blind retries of ambiguous upstream writes.

#### Scenario: PUBLISHER-DEVTO-ADAPTER-002 Success
- **WHEN** the upstream returns a remote id
- **THEN** the receipt records the id and exact content hash

#### Scenario: PUBLISHER-DEVTO-ADAPTER-002 Failure
- **WHEN** the upstream may have committed before timeout
- **THEN** the receipt is unknown until supported or manual reconciliation

#### Scenario: PUBLISHER-DEVTO-ADAPTER-002 Boundary
- **WHEN** the same local operation is submitted again
- **THEN** the original receipt is returned without another upstream write

