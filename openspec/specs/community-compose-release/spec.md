# community-compose-release Specification

## Purpose
TBD - created by archiving change community-compose-release. Update Purpose after archive.
## Requirements
### Requirement: COMMUNITY-COMPOSE-RELEASE-001 Two-service reproducible Community install
Community deployment SHALL run the complete MVP using one application and one PostgreSQL database with retained private media.

#### Scenario: COMMUNITY-COMPOSE-RELEASE-001 Success
- **WHEN** a clean install receives valid configuration
- **THEN** two-service deployment completes the milestone-to-approved-Markdown flow

#### Scenario: COMMUNITY-COMPOSE-RELEASE-001 Failure
- **WHEN** Playwright browser or required secret is missing
- **THEN** the corresponding operation fails visibly without fabricated success

#### Scenario: COMMUNITY-COMPOSE-RELEASE-001 Boundary
- **WHEN** application and database restart with retained volumes
- **THEN** project evidence and media remain available

### Requirement: COMMUNITY-COMPOSE-RELEASE-002 Honest release and recovery evidence
Community release SHALL verify backup recovery and distinguish runtime readiness from publication.

#### Scenario: COMMUNITY-COMPOSE-RELEASE-002 Success
- **WHEN** backup is restored into fresh volumes with the encryption key
- **THEN** approved assets and provider metadata remain consistent

#### Scenario: COMMUNITY-COMPOSE-RELEASE-002 Failure
- **WHEN** runtime checks are unavailable or fail
- **THEN** deployable status remains false with exact failure recorded

#### Scenario: COMMUNITY-COMPOSE-RELEASE-002 Boundary
- **WHEN** GitHub visibility is undecided
- **THEN** no repository publication or push is performed

