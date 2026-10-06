# public-project-journey Specification

## Purpose
TBD - created by archiving change public-project-journey. Update Purpose after archive.
## Requirements
### Requirement: PUBLIC-PROJECT-JOURNEY-001 Opt-in public history
A Project Journey SHALL publish only explicitly reviewed selected public snapshots.

#### Scenario: PUBLIC-PROJECT-JOURNEY-001 Success
- **WHEN** the owner enables a previewed approved selection
- **THEN** anonymous readers see exactly its selected milestones and stories

#### Scenario: PUBLIC-PROJECT-JOURNEY-001 Failure
- **WHEN** the project has never been enabled
- **THEN** anonymous Journey routes return 404

#### Scenario: PUBLIC-PROJECT-JOURNEY-001 Boundary
- **WHEN** private feedback or candidate milestones exist
- **THEN** they remain absent from the public projection

### Requirement: PUBLIC-PROJECT-JOURNEY-002 Revocable current projection
Journey publication SHALL enforce revision checks and stop serving revoked or invalidated snapshots.

#### Scenario: PUBLIC-PROJECT-JOURNEY-002 Success
- **WHEN** the owner revokes the Journey
- **THEN** new app requests return 404

#### Scenario: PUBLIC-PROJECT-JOURNEY-002 Failure
- **WHEN** a stale preview hash is submitted
- **THEN** publication returns 409 without replacing the snapshot

#### Scenario: PUBLIC-PROJECT-JOURNEY-002 Boundary
- **WHEN** an included approval becomes stale
- **THEN** the affected public snapshot is suspended until reviewed again

