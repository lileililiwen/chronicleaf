# github-evidence Specification

## Purpose
Bounded GitHub repository evidence import: a connected repository is named by owner and repository
rather than by a URL, every selected ref is resolved to an immutable SHA before a single fact is read,
and the collected commits, diffs, releases, issues and pull requests are appended to a milestone through
the evidence contract that already owns append-only storage, so provenance is the resolved revision
rather than a name that can move. Collection is a durable background job with idempotent submission,
bounded retry against a rate limit's own reset time, and no GitHub write of any kind. A private or
absent repository, a binary patch, a missing file and an over-cap compare are each reported as
exactly what was and was not collected, a credential stays a vault reference and never reaches a row,
a log or another host after a redirect, and a bundle that moved during the import is superseded rather
than overwritten. Delivered by github-evidence.
## Requirements
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

