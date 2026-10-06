## ADDED Requirements

### Requirement: EVOLUTION-GIF-EXPORT-001 Bounded chronological GIF
GIF export SHALL encode selected sourced version screenshots in bounded reviewed frame order.

#### Scenario: EVOLUTION-GIF-EXPORT-001 Success
- **WHEN** a valid selected sequence is encoded
- **THEN** decoded frames and durations match its manifest

#### Scenario: EVOLUTION-GIF-EXPORT-001 Failure
- **WHEN** a selected screenshot is missing
- **THEN** encoding fails without partial completed GIF

#### Scenario: EVOLUTION-GIF-EXPORT-001 Boundary
- **WHEN** requested duration or size exceeds the cap
- **THEN** validation rejects the request

### Requirement: EVOLUTION-GIF-EXPORT-002 Motion-aware reviewed export
GIF previews SHALL allow static viewing and only approved current artifacts may export.

#### Scenario: EVOLUTION-GIF-EXPORT-002 Success
- **WHEN** reduced motion is active
- **THEN** preview shows a static frame until explicit playback

#### Scenario: EVOLUTION-GIF-EXPORT-002 Failure
- **WHEN** the frame order is edited after approval
- **THEN** export requires renewed approval

#### Scenario: EVOLUTION-GIF-EXPORT-002 Boundary
- **WHEN** the GIF encoder is cancelled
- **THEN** the operation stays cancelled and no partial artifact exports

