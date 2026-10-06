## ADDED Requirements

### Requirement: LINKEDIN-COPY-PACKAGE-001 Evidence-based LinkedIn copy
LinkedIn output SHALL produce copy-ready text and owned media respecting project voice.

#### Scenario: LINKEDIN-COPY-PACKAGE-001 Success
- **WHEN** an owner adapts a source milestone story
- **THEN** the draft contains supported facts and selected media descriptions

#### Scenario: LINKEDIN-COPY-PACKAGE-001 Failure
- **WHEN** a preset asks for unsupported excitement or emoji
- **THEN** explicit project restrictions win

#### Scenario: LINKEDIN-COPY-PACKAGE-001 Boundary
- **WHEN** no CTA is provided
- **THEN** the post can remain informational without invented growth promises

### Requirement: LINKEDIN-COPY-PACKAGE-002 Human LinkedIn publication
LinkedIn copy packages SHALL require derivative approval and leave account publication to the user.

#### Scenario: LINKEDIN-COPY-PACKAGE-002 Success
- **WHEN** the owner approves the derivative
- **THEN** copy-ready text and images export

#### Scenario: LINKEDIN-COPY-PACKAGE-002 Failure
- **WHEN** the derivative is edited after approval
- **THEN** export requires renewed approval

#### Scenario: LINKEDIN-COPY-PACKAGE-002 Boundary
- **WHEN** native publish is requested
- **THEN** the app returns publisher_unsupported with no account action

