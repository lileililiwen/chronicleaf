## ADDED Requirements

### Requirement: HOSTED-MANAGED-AI-001 Explicit independent AI account modes
Hosted AI SHALL distinguish BYOK from explicitly consented Managed AI without implicit mode switching.

#### Scenario: HOSTED-MANAGED-AI-001 Success
- **WHEN** a tenant elects configured Managed AI
- **THEN** calls use the permitted operator-owned provider through scoped execution

#### Scenario: HOSTED-MANAGED-AI-001 Failure
- **WHEN** activation policy or operator credential is missing
- **THEN** Managed AI is unavailable without charging or switching keys

#### Scenario: HOSTED-MANAGED-AI-001 Boundary
- **WHEN** the tenant uses BYOK
- **THEN** managed quota accounting remains unchanged

### Requirement: HOSTED-MANAGED-AI-002 Atomic bounded usage accounting
Managed AI SHALL reserve and settle bounded per-tenant usage with unknown outcomes kept explicit.

#### Scenario: HOSTED-MANAGED-AI-002 Success
- **WHEN** concurrent managed calls reserve budget
- **THEN** the configured cap cannot be exceeded

#### Scenario: HOSTED-MANAGED-AI-002 Failure
- **WHEN** a provider outcome has unknown billed usage
- **THEN** the reservation stays pending rather than false zero cost

#### Scenario: HOSTED-MANAGED-AI-002 Boundary
- **WHEN** an operation settlement is replayed
- **THEN** usage is accounted once

