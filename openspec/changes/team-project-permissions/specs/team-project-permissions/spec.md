## ADDED Requirements

### Requirement: TEAM-PROJECT-PERMISSIONS-001 Explicit project permissions
Team permissions SHALL distinguish reading, editing, approving and publishing without bypassing human review.

#### Scenario: TEAM-PROJECT-PERMISSIONS-001 Success
- **WHEN** an editor edits an assigned project
- **THEN** the change is saved but approval is refused unless separately granted

#### Scenario: TEAM-PROJECT-PERMISSIONS-001 Failure
- **WHEN** a publisher attempts to publish an unapproved draft
- **THEN** publication is refused

#### Scenario: TEAM-PROJECT-PERMISSIONS-001 Boundary
- **WHEN** a reader targets another unassigned project
- **THEN** no project data or write access is granted

### Requirement: TEAM-PROJECT-PERMISSIONS-002 Safe invitation and revocation lifecycle
Team membership SHALL enforce bound single-use invitations, owner continuity and prompt revocation.

#### Scenario: TEAM-PROJECT-PERMISSIONS-002 Success
- **WHEN** the intended verified user accepts a live invitation
- **THEN** one membership is created

#### Scenario: TEAM-PROJECT-PERMISSIONS-002 Failure
- **WHEN** an expired or replayed invitation is used
- **THEN** no new membership is created

#### Scenario: TEAM-PROJECT-PERMISSIONS-002 Boundary
- **WHEN** the last owner is removed or a user is revoked
- **THEN** last-owner removal is rejected and revoked-user operations are denied

