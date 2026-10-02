## ADDED Requirements

### Requirement: GITHUB-RELEASE-CANDIDATES-001 Human-reviewed GitHub candidates
Supported GitHub events and tags SHALL produce attributed candidates without automatic confirmation.

#### Scenario: GITHUB-RELEASE-CANDIDATES-001 Success
- **WHEN** a verified release event arrives
- **THEN** one candidate identifies its release source

#### Scenario: GITHUB-RELEASE-CANDIDATES-001 Failure
- **WHEN** the signature is invalid
- **THEN** the API returns 401 and creates no candidate

#### Scenario: GITHUB-RELEASE-CANDIDATES-001 Boundary
- **WHEN** the same delivery is retried
- **THEN** the same receipt is returned without duplicate candidates

### Requirement: GITHUB-RELEASE-CANDIDATES-002 Bounded candidate event routing
Candidate ingestion SHALL process only selected event actions and explicit thresholds.

#### Scenario: GITHUB-RELEASE-CANDIDATES-002 Success
- **WHEN** a star count first crosses 100 or 1000
- **THEN** the corresponding threshold candidate is recorded once

#### Scenario: GITHUB-RELEASE-CANDIDATES-002 Failure
- **WHEN** an unsupported verified action arrives
- **THEN** an ignored receipt is recorded without milestone mutation

#### Scenario: GITHUB-RELEASE-CANDIDATES-002 Boundary
- **WHEN** a connection is archived or disconnected
- **THEN** delivery is acknowledged as ignored without new candidates

