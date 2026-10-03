# credential-vault Specification

## Purpose
Encrypted provider credentials with no way to read one back: a value is sealed under AES-256-GCM with a
key the operator keeps in a file outside the application, bound to its scope, provider, id and key
version so a moved or downgraded row cannot be opened, stored as ciphertext with no plaintext column and
no route that returns one, reachable only as metadata and as a reference a queued job resolves for
itself, and replaced and removed under an optimistic revision that leaves the current credential
untouched when a caller's view of it is stale. Delivered by credential-vault.
## Requirements
### Requirement: CREDENTIAL-VAULT-001 Encrypted secret references
Credentials SHALL be encrypted at rest and exposed only through metadata and scoped secret references.

#### Scenario: CREDENTIAL-VAULT-001 Success
- **WHEN** an owner saves a credential
- **THEN** metadata is readable but the plaintext is absent from DB rows and responses

#### Scenario: CREDENTIAL-VAULT-001 Failure
- **WHEN** the encryption key is unavailable
- **THEN** credential use fails closed without upstream calls

#### Scenario: CREDENTIAL-VAULT-001 Boundary
- **WHEN** a saved credential is removed before a queued job runs
- **THEN** the job fails credential_missing without retaining plaintext

### Requirement: CREDENTIAL-VAULT-002 Credential scope and rotation
Credential updates SHALL enforce scope and revision without leaking values.

#### Scenario: CREDENTIAL-VAULT-002 Success
- **WHEN** the current revision is replaced
- **THEN** new jobs use the replacement credential

#### Scenario: CREDENTIAL-VAULT-002 Failure
- **WHEN** a stale revision is replaced
- **THEN** the API returns 409 and preserves the current credential

#### Scenario: CREDENTIAL-VAULT-002 Boundary
- **WHEN** logs and error responses are captured during provider failure
- **THEN** no sentinel credential or Authorization header appears

