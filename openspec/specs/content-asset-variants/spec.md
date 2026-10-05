# content-asset-variants Specification

## Purpose
TBD - created by archiving change content-asset-variants. Update Purpose after archive.
## Requirements
### Requirement: CONTENT-ASSET-VARIANTS-001 Format-specific factual assets
Content formats SHALL produce structured assets from selected evidence with user voice precedence.

#### Scenario: CONTENT-ASSET-VARIANTS-001 Success
- **WHEN** a verified milestone creates a technical blog or changelog
- **THEN** the format contains its defined sections and attributed facts

#### Scenario: CONTENT-ASSET-VARIANTS-001 Failure
- **WHEN** a metric or outcome has no source evidence
- **THEN** the format does not invent it

#### Scenario: CONTENT-ASSET-VARIANTS-001 Boundary
- **WHEN** the selected format is readme-update
- **THEN** a content proposal is created without changing a repository

### Requirement: CONTENT-ASSET-VARIANTS-002 Independent derivative review
Each adapted asset SHALL have its own revision and human approval.

#### Scenario: CONTENT-ASSET-VARIANTS-002 Success
- **WHEN** an approved source is adapted to another format
- **THEN** the new asset is draft

#### Scenario: CONTENT-ASSET-VARIANTS-002 Failure
- **WHEN** a preset conflicts with a project voice setting
- **THEN** the project setting wins

#### Scenario: CONTENT-ASSET-VARIANTS-002 Boundary
- **WHEN** the source evidence changes during adaptation
- **THEN** the result cannot become an approved current asset

