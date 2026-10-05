# html-content-export Specification

## Purpose
TBD - created by archiving change html-content-export. Update Purpose after archive.
## Requirements
### Requirement: HTML-CONTENT-EXPORT-001 Sanitized approved HTML
HTML export SHALL render only the approved revision using safe local assets.

#### Scenario: HTML-CONTENT-EXPORT-001 Success
- **WHEN** an approved Markdown asset is exported
- **THEN** HTML preserves its text and allowlisted image content

#### Scenario: HTML-CONTENT-EXPORT-001 Failure
- **WHEN** source Markdown contains script or remote executable markup
- **THEN** the export contains no executable user code

#### Scenario: HTML-CONTENT-EXPORT-001 Boundary
- **WHEN** an approved image is missing
- **THEN** export fails media_unavailable rather than silently claiming a complete artifact

### Requirement: HTML-CONTENT-EXPORT-002 Portable export artifact
HTML artifacts SHALL use generated relative paths and checksum manifests without publishing.

#### Scenario: HTML-CONTENT-EXPORT-002 Success
- **WHEN** an HTML archive is opened offline
- **THEN** text and included images render with matching checksums

#### Scenario: HTML-CONTENT-EXPORT-002 Failure
- **WHEN** an export is cancelled
- **THEN** no completed partial archive is returned

#### Scenario: HTML-CONTENT-EXPORT-002 Boundary
- **WHEN** a source path includes traversal text
- **THEN** no archive entry can escape its output root

