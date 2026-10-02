## ADDED Requirements

### Requirement: COMMUNITY-ACCESS-001 Private single-user access
Private Community APIs SHALL require the configured single-user owner session.

#### Scenario: COMMUNITY-ACCESS-001 Success
- **WHEN** the owner submits a valid setup token
- **THEN** a bounded owner session is created and private routes work

#### Scenario: COMMUNITY-ACCESS-001 Failure
- **WHEN** an anonymous caller requests private data
- **THEN** the API returns 401 without data

#### Scenario: COMMUNITY-ACCESS-001 Boundary
- **WHEN** the session expires or is revoked
- **THEN** subsequent access returns 401

### Requirement: COMMUNITY-ACCESS-002 Mutation request integrity
Session mutations SHALL reject untrusted origins and invalid antiforgery tokens.

#### Scenario: COMMUNITY-ACCESS-002 Success
- **WHEN** a same-origin owner sends a valid antiforgery token
- **THEN** the authorized mutation executes

#### Scenario: COMMUNITY-ACCESS-002 Failure
- **WHEN** a cross-origin mutation is attempted
- **THEN** the API returns 403 without a write

#### Scenario: COMMUNITY-ACCESS-002 Boundary
- **WHEN** the fifth invalid login occurs inside 15 minutes
- **THEN** further login attempts return 429

