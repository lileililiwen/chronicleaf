## ADDED Requirements

### Requirement: BACKGROUND-JOBS-001 Durable operation execution
Operations SHALL persist queue and result state and prevent duplicate result commits after restart.

#### Scenario: BACKGROUND-JOBS-001 Success
- **WHEN** a job is enqueued and the process restarts
- **THEN** the operation reaches one committed result

#### Scenario: BACKGROUND-JOBS-001 Failure
- **WHEN** a handler fails permanently
- **THEN** the operation records failed with a redacted code after one attempt

#### Scenario: BACKGROUND-JOBS-001 Boundary
- **WHEN** two workers receive the same operation
- **THEN** only one commits a result

### Requirement: BACKGROUND-JOBS-002 Bounded retry and cancellation
Operations SHALL honor cancellation, input revision and bounded retries.

#### Scenario: BACKGROUND-JOBS-002 Success
- **WHEN** an explicitly transient handler failure occurs
- **THEN** retry obeys the three-attempt bound

#### Scenario: BACKGROUND-JOBS-002 Failure
- **WHEN** input revision has changed before result commit
- **THEN** the operation is superseded and no stale result attaches

#### Scenario: BACKGROUND-JOBS-002 Boundary
- **WHEN** an owner cancels an in-flight task
- **THEN** terminal state is cancelled and any late result is discarded

