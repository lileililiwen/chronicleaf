# cloud-tenant-boundary Specification

## Purpose
TBD - created by archiving change cloud-tenant-boundary. Update Purpose after archive.
## Requirements
### Requirement: CLOUD-TENANT-BOUNDARY-001 Site-local Hosted identity
Hosted access SHALL use verified site-local end-user identity with explicit tenant membership.

#### Scenario: CLOUD-TENANT-BOUNDARY-001 Success
- **WHEN** a verified user creates or selects an owned tenant
- **THEN** private requests resolve the server-derived tenant scope

#### Scenario: CLOUD-TENANT-BOUNDARY-001 Failure
- **WHEN** an OIDC issuer/state/nonce or operator token is invalid
- **THEN** no Hosted end-user session is issued

#### Scenario: CLOUD-TENANT-BOUNDARY-001 Boundary
- **WHEN** a membership is revoked
- **THEN** subsequent access to that tenant is denied

### Requirement: CLOUD-TENANT-BOUNDARY-002 Cross-surface tenant isolation
Hosted tenant boundaries SHALL apply to database queries, jobs, media and credential resolution.

#### Scenario: CLOUD-TENANT-BOUNDARY-002 Success
- **WHEN** two tenants reuse a pooled database connection
- **THEN** each sees only its own scoped rows

#### Scenario: CLOUD-TENANT-BOUNDARY-002 Failure
- **WHEN** a tenant guesses another tenant media or credential id
- **THEN** the request returns no foreign data

#### Scenario: CLOUD-TENANT-BOUNDARY-002 Boundary
- **WHEN** a Community database is opened in Hosted mode without explicit import
- **THEN** startup or import validation rejects silent reassignment

