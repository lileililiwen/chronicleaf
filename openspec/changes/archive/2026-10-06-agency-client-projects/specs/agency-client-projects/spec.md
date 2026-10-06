## ADDED Requirements

### Requirement: AGENCY-CLIENT-PROJECTS-001 Scoped client hierarchy
Agency organization SHALL associate owned projects with one optional owned client.

#### Scenario: AGENCY-CLIENT-PROJECTS-001 Success
- **WHEN** an owner assigns two projects to Client A
- **THEN** the hierarchy lists those projects under Client A

#### Scenario: AGENCY-CLIENT-PROJECTS-001 Failure
- **WHEN** a foreign-scope client is supplied
- **THEN** the assignment is rejected without exposing that client

#### Scenario: AGENCY-CLIENT-PROJECTS-001 Boundary
- **WHEN** a project has no client
- **THEN** it remains a personal unassigned project

### Requirement: AGENCY-CLIENT-PROJECTS-002 Stable history and context
Client reassignment and archive SHALL preserve history while invalidating stale client report context.

#### Scenario: AGENCY-CLIENT-PROJECTS-002 Success
- **WHEN** a project moves from Client A to Client B
- **THEN** new report context follows Client B and stale unexported reports are invalidated

#### Scenario: AGENCY-CLIENT-PROJECTS-002 Failure
- **WHEN** a client is archived
- **THEN** new assignments are refused but past records remain

#### Scenario: AGENCY-CLIENT-PROJECTS-002 Boundary
- **WHEN** a client is selected for report generation
- **THEN** unrelated client projects are not included

