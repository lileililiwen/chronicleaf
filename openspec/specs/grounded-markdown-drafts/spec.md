# grounded-markdown-drafts Specification

## Purpose
TBD - created by archiving change grounded-markdown-drafts. Update Purpose after archive.
## Requirements
### Requirement: GROUNDED-MARKDOWN-DRAFTS-001 Structured grounded content
Draft generation SHALL validate structured intermediate content against a frozen confirmed-milestone evidence snapshot before creating Markdown.

#### Scenario: GROUNDED-MARKDOWN-DRAFTS-001 Success
- **WHEN** a provider returns valid facts referencing selected evidence
- **THEN** a draft revision records validated summary, evidence and voice snapshots

#### Scenario: GROUNDED-MARKDOWN-DRAFTS-001 Failure
- **WHEN** a claim references unknown evidence or fabricated metrics
- **THEN** generation fails grounding validation without usable draft

#### Scenario: GROUNDED-MARKDOWN-DRAFTS-001 Boundary
- **WHEN** summary JSON fails twice including one repair
- **THEN** the job fails invalid_summary with no additional repair calls

### Requirement: GROUNDED-MARKDOWN-DRAFTS-002 Draft-only versioned generation
Generation SHALL preserve prior revisions and leave human approval to the review workflow.

#### Scenario: GROUNDED-MARKDOWN-DRAFTS-002 Success
- **WHEN** generation completes for unchanged input
- **THEN** a new asset revision is draft and contains known limitations

#### Scenario: GROUNDED-MARKDOWN-DRAFTS-002 Failure
- **WHEN** evidence changes before commit
- **THEN** the operation is superseded without replacing current content

#### Scenario: GROUNDED-MARKDOWN-DRAFTS-002 Boundary
- **WHEN** imported text contains instructions to auto-publish
- **THEN** those instructions are treated as data and never trigger state or provider writes

