# medium-import-package Specification

## Purpose
TBD - created by archiving change medium-import-package. Update Purpose after archive.
## Requirements
### Requirement: MEDIUM-IMPORT-PACKAGE-001 Manual Medium output
Medium support SHALL provide approved manual import materials without claiming a native API integration.

#### Scenario: MEDIUM-IMPORT-PACKAGE-001 Success
- **WHEN** the owner exports an approved article for Medium
- **THEN** HTML/Markdown and selected images with manifest are provided

#### Scenario: MEDIUM-IMPORT-PACKAGE-001 Failure
- **WHEN** native Medium publication is requested
- **THEN** the app reports publisher_unsupported without any upstream call

#### Scenario: MEDIUM-IMPORT-PACKAGE-001 Boundary
- **WHEN** the artifact is exported
- **THEN** no remote Medium URL or successful post is fabricated

### Requirement: MEDIUM-IMPORT-PACKAGE-002 Reviewed platform material
Medium-specific edits SHALL remain revisioned and reviewed before export.

#### Scenario: MEDIUM-IMPORT-PACKAGE-002 Success
- **WHEN** the source is adapted without edits
- **THEN** the export uses the approved snapshot

#### Scenario: MEDIUM-IMPORT-PACKAGE-002 Failure
- **WHEN** a derivative title or body is edited
- **THEN** the derivative requires its own approval

#### Scenario: MEDIUM-IMPORT-PACKAGE-002 Boundary
- **WHEN** a source image is unavailable
- **THEN** a complete image archive is not falsely returned

