## ADDED Requirements

### Requirement: SCREENSHOT-EVOLUTION-IMAGES-001 Evidence-preserving evolution images
Evolution images SHALL depict selected version screenshots with explicit chronology and source manifests.

#### Scenario: SCREENSHOT-EVOLUTION-IMAGES-001 Success
- **WHEN** valid same-viewport screenshots are selected
- **THEN** before-after or timeline PNG preserves their visual content and dates

#### Scenario: SCREENSHOT-EVOLUTION-IMAGES-001 Failure
- **WHEN** an earlier version has no screenshot
- **THEN** the interface reports the gap without fabricating an image

#### Scenario: SCREENSHOT-EVOLUTION-IMAGES-001 Boundary
- **WHEN** gallery mode includes different viewports
- **THEN** the manifest labels each viewport rather than implying identical comparison

### Requirement: SCREENSHOT-EVOLUTION-IMAGES-002 Reviewed visual artifacts
Evolution images SHALL remain draft until human review and respect source and output limits.

#### Scenario: SCREENSHOT-EVOLUTION-IMAGES-002 Success
- **WHEN** the rendered output is reviewed and approved
- **THEN** the exact artifact hash is exportable

#### Scenario: SCREENSHOT-EVOLUTION-IMAGES-002 Failure
- **WHEN** a caption or selected source changes before completion
- **THEN** the operation is superseded

#### Scenario: SCREENSHOT-EVOLUTION-IMAGES-002 Boundary
- **WHEN** rendered output exceeds the pixel or byte cap
- **THEN** generation fails output_too_large without a usable partial image

