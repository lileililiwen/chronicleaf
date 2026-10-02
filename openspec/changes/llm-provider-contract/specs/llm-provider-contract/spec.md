## ADDED Requirements

### Requirement: LLM-PROVIDER-CONTRACT-001 Neutral generation results
Every LLM adapter SHALL map bounded requests and provider outcomes through one provider-neutral contract.

#### Scenario: LLM-PROVIDER-CONTRACT-001 Success
- **WHEN** a configured provider completes
- **THEN** the result records text, finish state and available usage without modifying project state

#### Scenario: LLM-PROVIDER-CONTRACT-001 Failure
- **WHEN** the provider refuses or returns malformed content
- **THEN** the result distinguishes refusal from invalid_response

#### Scenario: LLM-PROVIDER-CONTRACT-001 Boundary
- **WHEN** a provider lacks native schema support
- **THEN** strict local validation remains required and capability reports no native support

### Requirement: LLM-PROVIDER-CONTRACT-002 Evidence consent and bounded calls
Generation SHALL use explicit evidence/provider consent and enforce request limits and cancellation.

#### Scenario: LLM-PROVIDER-CONTRACT-002 Success
- **WHEN** the owner authorizes the exact redacted input snapshot
- **THEN** only that snapshot is sent to the selected provider

#### Scenario: LLM-PROVIDER-CONTRACT-002 Failure
- **WHEN** the evidence or provider configuration changes after consent
- **THEN** transport is not invoked and renewed consent is required

#### Scenario: LLM-PROVIDER-CONTRACT-002 Boundary
- **WHEN** a request is cancelled or has an ambiguous timeout
- **THEN** no automatic approval or silent paid retry occurs

