## ADDED Requirements

### Requirement: LOCAL-GIT-EVIDENCE-001 Tracked local evidence
Local repository collection SHALL import only selected committed revisions with provenance.

#### Scenario: LOCAL-GIT-EVIDENCE-001 Success
- **WHEN** an allowed repository has two selected commits
- **THEN** the bundle identifies their SHAs and bounded tracked differences

#### Scenario: LOCAL-GIT-EVIDENCE-001 Failure
- **WHEN** an untracked or dirty secret file exists
- **THEN** it is not included in evidence

#### Scenario: LOCAL-GIT-EVIDENCE-001 Boundary
- **WHEN** the repository head changes during collection
- **THEN** the operation reports superseded rather than mixed revisions

### Requirement: LOCAL-GIT-EVIDENCE-002 Local filesystem authority
Local Git access SHALL require operator-registered roots and never mutate source repositories.

#### Scenario: LOCAL-GIT-EVIDENCE-002 Success
- **WHEN** an owner selects a configured root alias
- **THEN** read-only Git collection works within that root

#### Scenario: LOCAL-GIT-EVIDENCE-002 Failure
- **WHEN** a relative path escapes through traversal or symlink
- **THEN** collection is rejected before Git reads it

#### Scenario: LOCAL-GIT-EVIDENCE-002 Boundary
- **WHEN** Hosted requests local Git access
- **THEN** the API refuses local filesystem discovery

