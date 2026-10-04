# llm-openai-adapter Specification

## Purpose
The first real BYOK transport: an owner-authorized, consented, bounded call to OpenAI's Responses API,
speaking one provider's dialect and nothing else. A nonstreaming POST to the fixed origin carries the
model the owner named, the trusted instruction as a system message, the authorized evidence as a
labelled data section, the output ceiling, `store: false` and no member asking for tools or for a stored
conversation; a key leased from the delivered vault for the length of the call becomes one bearer header
and is returned before the call returns. The answer is mapped strictly: text, finish state, the
provider's own request id and whatever usage the provider actually reported, with absent or nonsensical
token counts reported unknown rather than fabricated. A refusal, a content-filtered answer, a tool call
in a text-only contract, an empty answer, a body that is not the documented shape and a response over
the 2 MiB cap are each distinguishable and none of them is ever presented as a draft; a truncation is
reported as a short draft with the `length` reason so a caller can refuse half a story. Failures are
safe by construction: 401 and 403 are one `credential_rejected` answer, 429 and 5xx are two different
codes, a 3xx is refused rather than followed, a cancelled call throws and stores nothing, no provider
prose or key reaches an envelope or a log line, no model is ever substituted, and no other provider is
tried in its place. The accepted-model list is deployment configuration and is empty unless an operator
filled it in, so an untouched deployment refuses every model before a credential is leased - and
removing the registration is the only lever that un-installs the transport. Delivered by
llm-openai-adapter; no claim is made here that OpenAI works, that a named model exists, or that a real
response has any shape beyond the published reference the fixtures were transcribed from.
## Requirements
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

