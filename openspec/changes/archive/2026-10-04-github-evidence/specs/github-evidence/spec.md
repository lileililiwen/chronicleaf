## ADDED Requirements

### Requirement: GITHUB-EVIDENCE-001 GitHub source provenance
GitHub collection SHALL resolve selected refs to immutable revisions and append bounded factual evidence.

#### Scenario: GITHUB-EVIDENCE-001 Success
- **WHEN** a public repository compare is collected
- **THEN** the bundle records resolved SHAs and selected commit/diff facts

#### Scenario: GITHUB-EVIDENCE-001 Failure
- **WHEN** a selected repository is inaccessible
- **THEN** the operation fails with repository_unavailable without invented material

#### Scenario: GITHUB-EVIDENCE-001 Boundary
- **WHEN** binary or excluded files occur
- **THEN** the result explicitly marks missing or redacted diff evidence

### Requirement: GITHUB-EVIDENCE-002 Rate and evidence boundaries
GitHub import SHALL enforce limits, credential privacy and stale-input suppression.

#### Scenario: GITHUB-EVIDENCE-002 Success
- **WHEN** rate limiting supplies a bounded retry time
- **THEN** the operation waits according to the job policy

#### Scenario: GITHUB-EVIDENCE-002 Failure
- **WHEN** the target bundle changes during import
- **THEN** the operation becomes superseded without overwriting it

#### Scenario: GITHUB-EVIDENCE-002 Boundary
- **WHEN** the compare exceeds the documented cap
- **THEN** the bundle marks truncation and stays within all caps

