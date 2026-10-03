# platform-jobs-adoption Specification

## Purpose
TBD - created by archiving change platform-jobs-adoption. Update Purpose after archive.
## Requirements
### Requirement: PLATFORM-JOBS-ADOPTION-001 Platform-owned dispatch
Background operation dispatch SHALL use the published Platform.Jobs and Platform.Jobs.Hangfire packages from the committed repository feed, and Chronicleaf SHALL hold no private dispatcher contract.

#### Scenario: PLATFORM-JOBS-ADOPTION-001 Success
- **WHEN** an operation is enqueued and executed through the platform dispatcher and payload handler
- **THEN** the job completes with exactly one committed result and no Chronicleaf-defined dispatch contract remains

#### Scenario: PLATFORM-JOBS-ADOPTION-001 Failure
- **WHEN** the platform packages are absent from the committed feed
- **THEN** the build and the feed check fail, and no fallback dispatcher is used

#### Scenario: PLATFORM-JOBS-ADOPTION-001 Boundary
- **WHEN** the queue payload is inspected
- **THEN** it carries only the operation id and never a credential or evidence

### Requirement: PLATFORM-JOBS-ADOPTION-002 Chronicleaf-owned operation state
Operation state, its guarantees, and readiness SHALL remain Chronicleaf-owned and observably unchanged by the platform adoption.

#### Scenario: PLATFORM-JOBS-ADOPTION-002 Success
- **WHEN** the two-worker, retry-bound, lease-recovery, cancellation and supersede scenarios are run
- **THEN** every result matches the pre-adoption behaviour with no test assertion modified

#### Scenario: PLATFORM-JOBS-ADOPTION-002 Failure
- **WHEN** the durable background engine cannot be reached
- **THEN** process liveness and database readiness still report as they did before the adoption

#### Scenario: PLATFORM-JOBS-ADOPTION-002 Boundary
- **WHEN** the engine's management surface is requested
- **THEN** it is not reachable, and it is not a reason to report the installation unready

### Requirement: PLATFORM-JOBS-ADOPTION-003 Reproducible platform dependency
The platform packages SHALL be consumed by exact version from a repository-committed relative feed, without a sibling checkout, an absolute path or an environment-variable feed.

#### Scenario: PLATFORM-JOBS-ADOPTION-003 Success
- **WHEN** a fresh checkout with an empty package cache builds the solution
- **THEN** it builds without warnings and without the sibling present

#### Scenario: PLATFORM-JOBS-ADOPTION-003 Failure
- **WHEN** the committed feed does not match what the build resolves
- **THEN** the verification gate fails and the build cannot silently fall back

#### Scenario: PLATFORM-JOBS-ADOPTION-003 Boundary
- **WHEN** a consumer project outside the designated infrastructure project is inspected
- **THEN** it does not reference a Platform package

