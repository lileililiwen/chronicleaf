## ADDED Requirements

### Requirement: LLM-OPENAI-ADAPTER-001 OpenAI transport mapping
The OpenAI adapter SHALL implement the shared generation contract with its documented wire format.

#### Scenario: LLM-OPENAI-ADAPTER-001 Success
- **WHEN** a valid nonstreaming provider response arrives
- **THEN** text, finish state and available usage map into GenerationResult

#### Scenario: LLM-OPENAI-ADAPTER-001 Failure
- **WHEN** the provider returns refusal or incomplete output
- **THEN** the adapter never marks it as a complete usable draft

#### Scenario: LLM-OPENAI-ADAPTER-001 Boundary
- **WHEN** usage fields are absent
- **THEN** usage is unknown rather than fabricated

### Requirement: LLM-OPENAI-ADAPTER-002 OpenAI safe failure handling
The OpenAI adapter SHALL enforce origin, cancellation and safe error classification.

#### Scenario: LLM-OPENAI-ADAPTER-002 Success
- **WHEN** the selected endpoint and credential configuration are valid
- **THEN** only the consented payload is sent

#### Scenario: LLM-OPENAI-ADAPTER-002 Failure
- **WHEN** credentials or provider permissions are rejected
- **THEN** credential_rejected is returned without exposing secret headers

#### Scenario: LLM-OPENAI-ADAPTER-002 Boundary
- **WHEN** a cancelled or oversized response arrives
- **THEN** no successful result or automatic alternate-provider call is produced

