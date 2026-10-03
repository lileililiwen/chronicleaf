# milestone-evidence Specification

## Purpose
Immutable, revisioned provenance snapshots for a milestone: every append writes a complete new bundle that identifies the source revision and the hash of what was collected, carries earlier evidence forward rather than replacing it, records an unavailable or partial source as such instead of claiming a collected fact, refuses a stale revision or an out-of-scope media reference before anything is written, and keeps referenced media undeletable. Delivered by milestone-evidence.
## Requirements
### Requirement: MILESTONE-EVIDENCE-001 Immutable provenance snapshots
Evidence Bundles SHALL preserve revisioned source provenance and explicit unavailable or partial evidence.

#### Scenario: MILESTONE-EVIDENCE-001 Success
- **WHEN** source material is appended
- **THEN** a new immutable bundle identifies source revision and hash

#### Scenario: MILESTONE-EVIDENCE-001 Failure
- **WHEN** a source is unavailable
- **THEN** the bundle records unavailable rather than claiming collected facts

#### Scenario: MILESTONE-EVIDENCE-001 Boundary
- **WHEN** the same source revision and hash are appended twice
- **THEN** the bundle revision does not change

### Requirement: MILESTONE-EVIDENCE-002 Bounded and scoped evidence
Evidence updates SHALL enforce revision, scope and bounded payloads.

#### Scenario: MILESTONE-EVIDENCE-002 Success
- **WHEN** an owner appends valid manual limitations
- **THEN** the next revision retains previous evidence and the limitations

#### Scenario: MILESTONE-EVIDENCE-002 Failure
- **WHEN** foreign-project media is referenced
- **THEN** the append fails without a new bundle

#### Scenario: MILESTONE-EVIDENCE-002 Boundary
- **WHEN** two updates target the same revision
- **THEN** one commits and the other returns 409

