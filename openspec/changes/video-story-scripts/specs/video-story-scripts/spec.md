## ADDED Requirements

### Requirement: VIDEO-STORY-SCRIPTS-001 Sourced bounded storyboard
Video Story SHALL produce complete 15, 30 or 60-second scripts using selected screenshots and attributed narration.

#### Scenario: VIDEO-STORY-SCRIPTS-001 Success
- **WHEN** a valid storyboard is generated
- **THEN** shots cover the requested duration exactly without gaps or overlaps

#### Scenario: VIDEO-STORY-SCRIPTS-001 Failure
- **WHEN** a shot references nonexistent screenshot or unsupported feature
- **THEN** the structured output fails validation

#### Scenario: VIDEO-STORY-SCRIPTS-001 Boundary
- **WHEN** a requested duration is outside 15/30/60
- **THEN** the API rejects it

### Requirement: VIDEO-STORY-SCRIPTS-002 Reviewed scripts before synthesis
Video Story export SHALL require independent approval and provide scripts rather than claim a synthesized video.

#### Scenario: VIDEO-STORY-SCRIPTS-002 Success
- **WHEN** the owner approves a valid story
- **THEN** JSON, narration and SRT with selected images export

#### Scenario: VIDEO-STORY-SCRIPTS-002 Failure
- **WHEN** captions or shot order change after approval
- **THEN** export requires new approval

#### Scenario: VIDEO-STORY-SCRIPTS-002 Boundary
- **WHEN** a story has been generated
- **THEN** no full video, fabricated footage or automatic publication is reported

