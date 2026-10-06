## ADDED Requirements

### Requirement: PUBLISHER-CONTRACT-001 Separate publisher authorization
Publication SHALL require a current approved asset, target preview and explicit per-target confirmation.

#### Scenario: PUBLISHER-CONTRACT-001 Success
- **WHEN** an owner confirms a current preview and approval
- **THEN** one target-specific dispatch is recorded

#### Scenario: PUBLISHER-CONTRACT-001 Failure
- **WHEN** a draft or stale preview is submitted
- **THEN** publication is refused before upstream write

#### Scenario: PUBLISHER-CONTRACT-001 Boundary
- **WHEN** a draft is merely generated or approved
- **THEN** no publisher dispatch occurs automatically

### Requirement: PUBLISHER-CONTRACT-002 Duplicate-safe publication receipts
Publisher operations SHALL preserve receipts and treat ambiguous writes as unknown rather than retrying blindly.

#### Scenario: PUBLISHER-CONTRACT-002 Success
- **WHEN** an identical idempotency key is resubmitted
- **THEN** the original operation receipt is returned

#### Scenario: PUBLISHER-CONTRACT-002 Failure
- **WHEN** the provider may have written before a timeout
- **THEN** the operation becomes unknown and is not automatically reissued

#### Scenario: PUBLISHER-CONTRACT-002 Boundary
- **WHEN** the same key is reused with different content
- **THEN** the API returns 409 without a second write

