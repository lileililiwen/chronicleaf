## ADDED Requirements

### Requirement: COMMUNITY-PERSISTENCE-001 Transactional durable state
The installation SHALL persist database state and atomically commit domain, audit and outbox writes.

#### Scenario: COMMUNITY-PERSISTENCE-001 Success
- **WHEN** a migration and transaction commit followed by restart
- **THEN** the same installation id and exactly one outbox event remain

#### Scenario: COMMUNITY-PERSISTENCE-001 Failure
- **WHEN** an exception occurs before commit
- **THEN** all transaction writes roll back

#### Scenario: COMMUNITY-PERSISTENCE-001 Boundary
- **WHEN** migration runs a second time
- **THEN** existing rows remain unchanged

### Requirement: COMMUNITY-PERSISTENCE-002 Honest dependency readiness
Readiness SHALL distinguish missing schema and unavailable database from process liveness.

#### Scenario: COMMUNITY-PERSISTENCE-002 Success
- **WHEN** the expected schema is reachable
- **THEN** GET /api/ready returns 200 status=ready

#### Scenario: COMMUNITY-PERSISTENCE-002 Failure
- **WHEN** PostgreSQL is unavailable
- **THEN** GET /api/ready returns 503 code=database_unavailable

#### Scenario: COMMUNITY-PERSISTENCE-002 Boundary
- **WHEN** the schema is older than required
- **THEN** GET /api/ready returns 503 code=schema_mismatch and liveness still succeeds

