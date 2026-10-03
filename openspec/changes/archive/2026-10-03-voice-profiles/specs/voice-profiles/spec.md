## ADDED Requirements

### Requirement: VOICE-PROFILES-001 Voice override precedence
Explicit user Voice Profile values SHALL override every preset value and be snapshotted for generation.

#### Scenario: VOICE-PROFILES-001 Success
- **WHEN** a preset requests emoji but the owner disables it
- **THEN** the effective profile has emoji=false

#### Scenario: VOICE-PROFILES-001 Failure
- **WHEN** an unknown preset is requested
- **THEN** the API rejects it with validation_failed

#### Scenario: VOICE-PROFILES-001 Boundary
- **WHEN** the owner changes voice after draft creation
- **THEN** the existing draft keeps its prior voice snapshot

### Requirement: VOICE-PROFILES-002 Visible voice constraint findings
Forbidden terms and configured structural constraints SHALL be reported with actionable text spans.

#### Scenario: VOICE-PROFILES-002 Success
- **WHEN** a generated title and text satisfy the voice rules
- **THEN** no deterministic voice violation is reported

#### Scenario: VOICE-PROFILES-002 Failure
- **WHEN** a case-variant forbidden term occurs
- **THEN** a normalized forbidden-term finding is returned

#### Scenario: VOICE-PROFILES-002 Boundary
- **WHEN** a user edits an existing draft to include forbidden wording
- **THEN** approval requires correction and text is not silently altered

