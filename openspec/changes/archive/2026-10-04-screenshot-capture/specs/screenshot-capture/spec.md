## ADDED Requirements

### Requirement: SCREENSHOT-CAPTURE-001 Bounded screenshot candidates
Capture jobs SHALL produce actual bounded desktop or mobile PNG candidates with source provenance.

#### Scenario: SCREENSHOT-CAPTURE-001 Success
- **WHEN** a permitted public fixture URL loads
- **THEN** the candidate identifies final URL, capture time and viewport with real PNG bytes

#### Scenario: SCREENSHOT-CAPTURE-001 Failure
- **WHEN** the page exceeds the time limit
- **THEN** the job fails capture_timeout without a successful candidate

#### Scenario: SCREENSHOT-CAPTURE-001 Boundary
- **WHEN** one of multiple pages fails
- **THEN** successful candidates and explicit failed-page results are distinguishable

### Requirement: SCREENSHOT-CAPTURE-002 Capture network isolation
Capture SHALL enforce network policy across navigation and every browser-originated connection.

#### Scenario: SCREENSHOT-CAPTURE-002 Success
- **WHEN** an owner authorizes an exact local preview origin in Community
- **THEN** only that origin and allowed resolved address can be captured during its validity

#### Scenario: SCREENSHOT-CAPTURE-002 Failure
- **WHEN** a redirect or subresource targets metadata or unallowed private address
- **THEN** traffic is denied and never reaches that destination

#### Scenario: SCREENSHOT-CAPTURE-002 Boundary
- **WHEN** the same request runs in Hosted mode
- **THEN** Community local-network exemptions are refused

