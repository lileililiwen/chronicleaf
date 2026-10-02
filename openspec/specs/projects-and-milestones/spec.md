# projects-and-milestones Specification

## Purpose
TBD - created by archiving change projects-and-milestones. Update Purpose after archive.
## Requirements
### Requirement: PROJECTS-AND-MILESTONES-001 Human-controlled Milestones
Projects SHALL persist typed Milestones with explicit human confirmation of imported candidates.

#### Scenario: PROJECTS-AND-MILESTONES-001 Success
- **WHEN** an owner creates a manual milestone
- **THEN** a confirmed milestone appears under its project

#### Scenario: PROJECTS-AND-MILESTONES-001 Failure
- **WHEN** an import creates a candidate
- **THEN** it cannot be treated as confirmed until the owner confirms

#### Scenario: PROJECTS-AND-MILESTONES-001 Boundary
- **WHEN** the same source candidate is imported twice
- **THEN** only one candidate id exists

### Requirement: PROJECTS-AND-MILESTONES-002 Safe project editing
Project and Milestone edits SHALL use scoped lookup and optimistic revision checks.

#### Scenario: PROJECTS-AND-MILESTONES-002 Success
- **WHEN** a current revision is edited
- **THEN** the values persist with an incremented revision

#### Scenario: PROJECTS-AND-MILESTONES-002 Failure
- **WHEN** a stale revision is submitted
- **THEN** the API returns 409 without losing the newer edit

#### Scenario: PROJECTS-AND-MILESTONES-002 Boundary
- **WHEN** an archived project is modified
- **THEN** the API rejects modification and keeps its historical records

