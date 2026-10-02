## ADDED Requirements

### Requirement: MILESTONE-TIMELINE-001 Chronological confirmed timeline
The private timeline SHALL display persisted confirmed milestones in stable chronology.

#### Scenario: MILESTONE-TIMELINE-001 Success
- **WHEN** confirmed milestones span repeated timestamps
- **THEN** paging yields deterministic order without duplicate ids

#### Scenario: MILESTONE-TIMELINE-001 Failure
- **WHEN** the project contains no confirmed milestones
- **THEN** the timeline shows an empty state

#### Scenario: MILESTONE-TIMELINE-001 Boundary
- **WHEN** candidate visibility is enabled
- **THEN** candidates appear labeled and never as confirmed

### Requirement: MILESTONE-TIMELINE-002 Private timeline navigation
Timeline paging and browser navigation SHALL remain scoped to the selected project.

#### Scenario: MILESTONE-TIMELINE-002 Success
- **WHEN** an owner selects a timeline record
- **THEN** the corresponding milestone editor opens

#### Scenario: MILESTONE-TIMELINE-002 Failure
- **WHEN** a cursor from another project is reused
- **THEN** the API rejects it without foreign records

#### Scenario: MILESTONE-TIMELINE-002 Boundary
- **WHEN** a milestone is backdated
- **THEN** a refreshed timeline places it at the new chronological position

