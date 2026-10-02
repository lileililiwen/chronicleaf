## ADDED Requirements

### Requirement: LLM-OPENROUTER-ADAPTER-001 OpenRouter transport mapping
The OpenRouter adapter SHALL implement the shared generation contract with its documented wire format.

#### Scenario: LLM-OPENROUTER-ADAPTER-001 Success
- **WHEN** a valid nonstreaming provider response arrives
- **THEN** text, finish state and available usage map into GenerationResult

#### Scenario: LLM-OPENROUTER-ADAPTER-001 Failure
- **WHEN** the provider returns refusal or incomplete output
- **THEN** the adapter never marks it as a complete usable draft

#### Scenario: LLM-OPENROUTER-ADAPTER-001 Boundary
- **WHEN** usage fields are absent
- **THEN** usage is unknown rather than fabricated

### Requirement: LLM-OPENROUTER-ADAPTER-002 OpenRouter safe failure handling
The OpenRouter adapter SHALL enforce origin, cancellation and safe error classification.

#### Scenario: LLM-OPENROUTER-ADAPTER-002 Success
- **WHEN** the selected endpoint and credential configuration are valid
- **THEN** only the consented payload is sent

#### Scenario: LLM-OPENROUTER-ADAPTER-002 Failure
- **WHEN** credentials or provider permissions are rejected
- **THEN** credential_rejected is returned without exposing secret headers

#### Scenario: LLM-OPENROUTER-ADAPTER-002 Boundary
- **WHEN** a cancelled or oversized response arrives
- **THEN** no successful result or automatic alternate-provider call is produced

