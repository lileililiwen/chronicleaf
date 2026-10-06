# github-app-connection Specification

## Purpose
TBD - created by archiving change github-app-connection. Update Purpose after archive.
## Requirements
### Requirement: GITHUB-APP-CONNECTION-001 Verified installation ownership
GitHub App connections SHALL bind verified installations and selected repositories to the requesting tenant.

#### Scenario: GITHUB-APP-CONNECTION-001 Success
- **WHEN** a valid owner completes a single-use callback for an allowed repository
- **THEN** the scoped installation connection is created

#### Scenario: GITHUB-APP-CONNECTION-001 Failure
- **WHEN** another tenant or account supplies its installation id
- **THEN** attachment is refused

#### Scenario: GITHUB-APP-CONNECTION-001 Boundary
- **WHEN** the callback state is replayed or expired
- **THEN** no new connection is created

### Requirement: GITHUB-APP-CONNECTION-002 Read-scoped revocable tokens
App evidence collection SHALL use bounded read-scoped tokens and preserve independent PAT access.

#### Scenario: GITHUB-APP-CONNECTION-002 Success
- **WHEN** a valid installation token approaches expiry
- **THEN** the provider renews it for the selected repository

#### Scenario: GITHUB-APP-CONNECTION-002 Failure
- **WHEN** the installation is removed
- **THEN** new and queued reads fail without publishing or secret leakage

#### Scenario: GITHUB-APP-CONNECTION-002 Boundary
- **WHEN** Community uses its configured PAT
- **THEN** App rollout does not alter its working connection

