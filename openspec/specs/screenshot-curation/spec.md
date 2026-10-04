# screenshot-curation Specification

## Purpose
TBD - created by archiving change screenshot-curation. Update Purpose after archive.
## Requirements
### Requirement: SCREENSHOT-CURATION-001 Explicit screenshot selection
Only human-selected candidates SHALL enter selected screenshot evidence.

#### Scenario: SCREENSHOT-CURATION-001 Success
- **WHEN** an owner selects a screenshot and caption
- **THEN** a new evidence revision references that media and caption

#### Scenario: SCREENSHOT-CURATION-001 Failure
- **WHEN** a newly captured candidate is not selected
- **THEN** it stays outside selected evidence

#### Scenario: SCREENSHOT-CURATION-001 Boundary
- **WHEN** capture is rerun
- **THEN** prior human selection remains unchanged

### Requirement: SCREENSHOT-CURATION-002 Primary and ordering consistency
Screenshot curation SHALL maintain one selected primary and scoped complete ordering.

#### Scenario: SCREENSHOT-CURATION-002 Success
- **WHEN** the owner changes primary
- **THEN** the old primary clears in the same transaction

#### Scenario: SCREENSHOT-CURATION-002 Failure
- **WHEN** a rejected or foreign candidate is made primary
- **THEN** the API rejects the request without changing selection

#### Scenario: SCREENSHOT-CURATION-002 Boundary
- **WHEN** concurrent curation uses the same revision
- **THEN** one update wins and the other returns 409

