# llm-ollama-adapter Specification

## Purpose
TBD - created by archiving change llm-ollama-adapter. Update Purpose after archive.
## Requirements
### Requirement: LLM-OLLAMA-ADAPTER-001 Ollama transport mapping
The Ollama adapter SHALL implement the shared generation contract with its documented wire format.

#### Scenario: LLM-OLLAMA-ADAPTER-001 Success
- **WHEN** a valid nonstreaming provider response arrives
- **THEN** text, finish state and available usage map into GenerationResult

#### Scenario: LLM-OLLAMA-ADAPTER-001 Failure
- **WHEN** the provider returns refusal or incomplete output
- **THEN** the adapter never marks it as a complete usable draft

#### Scenario: LLM-OLLAMA-ADAPTER-001 Boundary
- **WHEN** usage fields are absent
- **THEN** usage is unknown rather than fabricated

### Requirement: LLM-OLLAMA-ADAPTER-002 Ollama safe failure handling
The Ollama adapter SHALL enforce origin, cancellation and safe error classification.

#### Scenario: LLM-OLLAMA-ADAPTER-002 Success
- **WHEN** the selected endpoint and credential configuration are valid
- **THEN** only the consented payload is sent

#### Scenario: LLM-OLLAMA-ADAPTER-002 Failure
- **WHEN** credentials or provider permissions are rejected
- **THEN** credential_rejected is returned without exposing secret headers

#### Scenario: LLM-OLLAMA-ADAPTER-002 Boundary
- **WHEN** a cancelled or oversized response arrives
- **THEN** no successful result or automatic alternate-provider call is produced

