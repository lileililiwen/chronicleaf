## ADDED Requirements

### Requirement: SCHEDULED-PROJECT-SYNC-001 Opt-in durable sync schedule
Scheduled sync SHALL run only explicitly enabled project schedules with bounded durable execution.

#### Scenario: SCHEDULED-PROJECT-SYNC-001 Success
- **WHEN** an enabled schedule becomes due
- **THEN** one repository read operation is created

#### Scenario: SCHEDULED-PROJECT-SYNC-001 Failure
- **WHEN** a schedule is disabled or project disconnected
- **THEN** new operations are not dispatched

#### Scenario: SCHEDULED-PROJECT-SYNC-001 Boundary
- **WHEN** multiple ticks or missed intervals occur
- **THEN** the system does not create duplicate or catch-up burst operations

### Requirement: SCHEDULED-PROJECT-SYNC-002 Read-only scheduled outcomes
Recurring synchronization SHALL enforce tenant quotas and never auto-approve or publish.

#### Scenario: SCHEDULED-PROJECT-SYNC-002 Success
- **WHEN** repository events arrive during a scheduled sync
- **THEN** they become attributed candidates only

#### Scenario: SCHEDULED-PROJECT-SYNC-002 Failure
- **WHEN** a schedule exceeds the configured quota
- **THEN** activation is refused with schedule_quota

#### Scenario: SCHEDULED-PROJECT-SYNC-002 Boundary
- **WHEN** the provider fails
- **THEN** the settings show the failed outcome and next bounded attempt without fabricated progress

