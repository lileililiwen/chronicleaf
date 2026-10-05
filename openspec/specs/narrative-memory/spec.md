# narrative-memory Specification

## Purpose
TBD - created by archiving change narrative-memory. Update Purpose after archive.
## Requirements
### Requirement: NARRATIVE-MEMORY-001 Attributed narrative continuity
Project narrative context SHALL combine owned historical milestones, decisions and articles with source attribution.

#### Scenario: NARRATIVE-MEMORY-001 Success
- **WHEN** SQLite and PostgreSQL history are recorded before a new draft
- **THEN** the context includes the attributed transition without reintroducing the project as new

#### Scenario: NARRATIVE-MEMORY-001 Failure
- **WHEN** no feedback history exists
- **THEN** the context reports unknown feedback rather than fabricated users

#### Scenario: NARRATIVE-MEMORY-001 Boundary
- **WHEN** a retrospective cutoff precedes a milestone
- **THEN** the later milestone is excluded

### Requirement: NARRATIVE-MEMORY-002 Revision-aware memory
Narrative summaries SHALL reflect source revisions and preserve privacy selections.

#### Scenario: NARRATIVE-MEMORY-002 Success
- **WHEN** a source decision is corrected
- **THEN** the prior summary becomes stale until regeneration

#### Scenario: NARRATIVE-MEMORY-002 Failure
- **WHEN** summary regeneration fails
- **THEN** the prior summary remains available labeled stale

#### Scenario: NARRATIVE-MEMORY-002 Boundary
- **WHEN** private feedback was not selected for model consent or public Journey
- **THEN** it is absent from those outputs

