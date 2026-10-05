# github-graphql-enrichment Specification

## Purpose
TBD - created by archiving change github-graphql-enrichment. Update Purpose after archive.
## Requirements
### Requirement: GITHUB-GRAPHQL-ENRICHMENT-001 Attributed relationship enrichment
Optional GraphQL enrichment SHALL add bounded selected Issue and PR relationships to existing evidence.

#### Scenario: GITHUB-GRAPHQL-ENRICHMENT-001 Success
- **WHEN** selected commits have readable associated PRs
- **THEN** the new bundle includes their ids, observed states and source URLs

#### Scenario: GITHUB-GRAPHQL-ENRICHMENT-001 Failure
- **WHEN** some requested fields return GraphQL errors
- **THEN** those fields are marked unavailable and REST evidence remains

#### Scenario: GITHUB-GRAPHQL-ENRICHMENT-001 Boundary
- **WHEN** pagination repeats a provider node
- **THEN** only one observed relation entry is appended

### Requirement: GITHUB-GRAPHQL-ENRICHMENT-002 Read-only bounded query execution
Enrichment SHALL use fixed queries and scope while preserving human control.

#### Scenario: GITHUB-GRAPHQL-ENRICHMENT-002 Success
- **WHEN** an owner requests selected relationship enrichment
- **THEN** only the declared repository is queried

#### Scenario: GITHUB-GRAPHQL-ENRICHMENT-002 Failure
- **WHEN** permissions are missing
- **THEN** the operation reports permission_denied without fabricated relation facts

#### Scenario: GITHUB-GRAPHQL-ENRICHMENT-002 Boundary
- **WHEN** input bundle changes during the query
- **THEN** the result becomes superseded rather than overwrite evidence

