# static-blog-export Specification

## Purpose
TBD - created by archiving change static-blog-export. Update Purpose after archive.
## Requirements
### Requirement: STATIC-BLOG-EXPORT-001 Selected approved static site
Static export SHALL include only explicitly selected approved assets and corresponding public-safe timeline entries.

#### Scenario: STATIC-BLOG-EXPORT-001 Success
- **WHEN** an owner confirms a valid public export selection
- **THEN** the archive contains exactly the selected posts and local images

#### Scenario: STATIC-BLOG-EXPORT-001 Failure
- **WHEN** selection includes an unapproved asset
- **THEN** the job refuses export

#### Scenario: STATIC-BLOG-EXPORT-001 Boundary
- **WHEN** the project has private candidates or narrative notes
- **THEN** those records never enter the archive

### Requirement: STATIC-BLOG-EXPORT-002 Atomic portable site archive
Static site generation SHALL validate paths and withhold incomplete or stale archives.

#### Scenario: STATIC-BLOG-EXPORT-002 Success
- **WHEN** a nonroot basePath is configured
- **THEN** offline relative links resolve correctly

#### Scenario: STATIC-BLOG-EXPORT-002 Failure
- **WHEN** approval revokes during assembly
- **THEN** the job becomes superseded and returns no completed archive

#### Scenario: STATIC-BLOG-EXPORT-002 Boundary
- **WHEN** the same immutable selection is exported twice
- **THEN** content checksums are reproducible

