# markdown-review-export Specification

## Purpose
TBD - created by archiving change markdown-review-export. Update Purpose after archive.
## Requirements
### Requirement: MARKDOWN-REVIEW-EXPORT-001 Explicit human approval
Content SHALL require explicit approval of the current body and evidence revisions before export.

#### Scenario: MARKDOWN-REVIEW-EXPORT-001 Success
- **WHEN** an owner reviews and approves the current draft
- **THEN** an approval binds actor, content hash and evidence revision

#### Scenario: MARKDOWN-REVIEW-EXPORT-001 Failure
- **WHEN** export is requested without approval
- **THEN** the API returns 409 approval_required

#### Scenario: MARKDOWN-REVIEW-EXPORT-001 Boundary
- **WHEN** content or evidence is edited after approval
- **THEN** export returns approval_stale until new approval

### Requirement: MARKDOWN-REVIEW-EXPORT-002 Safe exact Markdown export
The editor SHALL safely preview and export the exact approved Markdown without private metadata.

#### Scenario: MARKDOWN-REVIEW-EXPORT-002 Success
- **WHEN** the owner downloads a valid approved asset
- **THEN** the attachment bytes equal the approved revision and no upstream publish occurs

#### Scenario: MARKDOWN-REVIEW-EXPORT-002 Failure
- **WHEN** Markdown contains script or unsafe URL markup
- **THEN** preview never executes the markup

#### Scenario: MARKDOWN-REVIEW-EXPORT-002 Boundary
- **WHEN** approval and editing occur concurrently
- **THEN** no unreviewed revision is exportable

