# agency-weekly-reports Specification

## Purpose
TBD - created by archiving change agency-weekly-reports. Update Purpose after archive.
## Requirements
### Requirement: AGENCY-WEEKLY-REPORTS-001 Scoped factual weekly reporting
Agency reports SHALL summarize selected client projects into completed, resolved, in-progress, next-week and screenshot sections from evidence.

#### Scenario: AGENCY-WEEKLY-REPORTS-001 Success
- **WHEN** a client has selected confirmed work in the week
- **THEN** the report attributes each factual section to those projects and sources

#### Scenario: AGENCY-WEEKLY-REPORTS-001 Failure
- **WHEN** a project belongs to another client
- **THEN** generation rejects it before model transfer

#### Scenario: AGENCY-WEEKLY-REPORTS-001 Boundary
- **WHEN** the selected week has no recorded completion
- **THEN** the report says no recorded completed milestones without inventing progress

### Requirement: AGENCY-WEEKLY-REPORTS-002 Calendar and approval consistency
Reports SHALL use explicit local calendar windows and current client/source approval bindings.

#### Scenario: AGENCY-WEEKLY-REPORTS-002 Success
- **WHEN** a week crosses a daylight-saving transition
- **THEN** the UTC bounds still represent exactly seven local calendar dates

#### Scenario: AGENCY-WEEKLY-REPORTS-002 Failure
- **WHEN** project attribution changes during generation
- **THEN** the result is superseded

#### Scenario: AGENCY-WEEKLY-REPORTS-002 Boundary
- **WHEN** source evidence changes after report approval
- **THEN** export requires renewed review

