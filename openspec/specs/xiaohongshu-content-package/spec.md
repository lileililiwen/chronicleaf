# xiaohongshu-content-package Specification

## Purpose
TBD - created by archiving change xiaohongshu-content-package. Update Purpose after archive.
## Requirements
### Requirement: XIAOHONGSHU-CONTENT-PACKAGE-001 Complete copy-ready pack
Xiaohongshu adaptation SHALL provide editable titles, body, ordered images, cover text, tags and CTA grounded in source evidence.

#### Scenario: XIAOHONGSHU-CONTENT-PACKAGE-001 Success
- **WHEN** a source asset and selected screenshots are adapted
- **THEN** the pack contains all editable fields and image provenance

#### Scenario: XIAOHONGSHU-CONTENT-PACKAGE-001 Failure
- **WHEN** the voice profile disables emoji or marketing wording
- **THEN** the preset cannot override that setting

#### Scenario: XIAOHONGSHU-CONTENT-PACKAGE-001 Boundary
- **WHEN** no CTA was requested
- **THEN** the pack permits an empty CTA without fabricated engagement claims

### Requirement: XIAOHONGSHU-CONTENT-PACKAGE-002 User-owned publication
Xiaohongshu packages SHALL require human derivative review and leave publication to the user.

#### Scenario: XIAOHONGSHU-CONTENT-PACKAGE-002 Success
- **WHEN** the owner approves the completed pack
- **THEN** copy-ready text and owned images can export

#### Scenario: XIAOHONGSHU-CONTENT-PACKAGE-002 Failure
- **WHEN** a title or image order changes after approval
- **THEN** export requires renewed approval

#### Scenario: XIAOHONGSHU-CONTENT-PACKAGE-002 Boundary
- **WHEN** the pack is generated or exported
- **THEN** no account login or automatic posting occurs

