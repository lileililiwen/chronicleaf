## ADDED Requirements

### Requirement: SEMANTIC-NARRATIVE-RETRIEVAL-001 Source-preserving semantic retrieval
Enabled semantic retrieval SHALL return owned source entries with revisions while enforcing project and cutoff filters.

#### Scenario: SEMANTIC-NARRATIVE-RETRIEVAL-001 Success
- **WHEN** a verified compatible index is ready
- **THEN** search returns ranked owned entries and source ids

#### Scenario: SEMANTIC-NARRATIVE-RETRIEVAL-001 Failure
- **WHEN** an embedding model or dimension is incompatible
- **THEN** search falls back to structured chronology with explicit reason

#### Scenario: SEMANTIC-NARRATIVE-RETRIEVAL-001 Boundary
- **WHEN** another tenant contains the closest vector
- **THEN** that entry is never returned

### Requirement: SEMANTIC-NARRATIVE-RETRIEVAL-002 Explicit optional activation
Semantic retrieval SHALL remain disabled until its demand and provider decisions are resolved.

#### Scenario: SEMANTIC-NARRATIVE-RETRIEVAL-002 Success
- **WHEN** the extension is not explicitly enabled
- **THEN** normal narrative generation uses structured PostgreSQL history

#### Scenario: SEMANTIC-NARRATIVE-RETRIEVAL-002 Failure
- **WHEN** provider/model/dimension decisions are unresolved
- **THEN** implementation remains blocked and no embedding request is made

#### Scenario: SEMANTIC-NARRATIVE-RETRIEVAL-002 Boundary
- **WHEN** a replacement index is partially built
- **THEN** the last complete compatible index or structured fallback remains active

