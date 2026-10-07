# video-story-panel Specification

## Purpose
Give the delivered `video-story-scripts` backend a real Web UI as a
read/export panel over its delivered routes: a storyboard surface with
duration and source-material pickers, an inert shot-order preview, an
approval-gated exact-byte download, and the contract's no-synthesis boundary
stated on screen — plus the two backend pieces the surface genuinely needs
(script discovery read, story-body edit refusal).

## ADDED Requirements
### Requirement: VIDEO-STORY-PANEL-001 Storyboard composer surface
The Evolution area SHALL offer a storyboard surface where the owner selects a
duration (15/30/60s) and source material over delivered routes, composes
shots with screenshots, captions, narration and transitions, and previews
the shot order — with no hand-typed IDs, nothing auto-firing, and the
no-synthesis boundary visible in the UI copy.

#### Scenario: VIDEO-STORY-PANEL-001 Success
- **WHEN** the owner picks a duration, an approved story, selected
  screenshots and evidence citations, and presses Generate
- **THEN** the panel POSTs the delivered generate route once and previews
  the stored shot order with screenshots, subtitles, narration and
  transitions

#### Scenario: VIDEO-STORY-PANEL-001 Failure
- **WHEN** the generate route refuses the shot list
- **THEN** the panel shows the server's code and message verbatim and stores
  nothing client-side

#### Scenario: VIDEO-STORY-PANEL-001 Boundary
- **WHEN** the milestone is unconfirmed, has no story, has no approved
  story, has no selected screenshot, or holds no script yet
- **THEN** the panel states the honest empty case with no acting control,
  names that no video is synthesized and no footage is fabricated, and fires
  no request before an explicit control is pressed

### Requirement: VIDEO-STORY-PANEL-002 Approval-gated export and verbatim refusals
Script download SHALL require a live approval and serve bytes byte-equal to
the server, and every server refusal SHALL be shown verbatim.

#### Scenario: VIDEO-STORY-PANEL-002 Success
- **WHEN** the owner approves the head revision and downloads
- **THEN** the saved ZIP is byte-equal to `GET
  /api/assets/{id}/export?format=video-story` and carries storyboard.json,
  narration.md, caption.srt, the depicted pictures and their manifest

#### Scenario: VIDEO-STORY-PANEL-002 Failure
- **WHEN** the export is attempted without a live approval, after a
  regenerate, or against missing picture bytes
- **THEN** the panel shows `approval_required`, `approval_stale` or
  `media_unavailable` verbatim with no file, and a native-publish attempt
  shows `publisher_unsupported` verbatim with no account action

#### Scenario: VIDEO-STORY-PANEL-002 Boundary
- **WHEN** the owner regenerates after approving
- **THEN** the new revision starts unapproved, the download refuses
  verbatim, and re-approval re-opens it

### Requirement: VIDEO-STORY-PANEL-003 Script discovery read
The API SHALL serve the milestone's video-story asset for discovery, so a
panel (and a reload) can find the script without a hand-typed asset id.

#### Scenario: VIDEO-STORY-PANEL-003 Success
- **WHEN** the owner reads `GET /api/milestones/{id}/video-story` on a
  milestone holding a script
- **THEN** the route answers the asset id, head revision, body hash,
  duration, shot count and binding revisions

#### Scenario: VIDEO-STORY-PANEL-003 Failure
- **WHEN** the milestone is absent or owned by another scope
- **THEN** the route answers the delivered 404 with no data, and a
  sessionless read answers 401

#### Scenario: VIDEO-STORY-PANEL-003 Boundary
- **WHEN** the milestone holds no script
- **THEN** the route answers a null asset rather than 404, so empty is
  distinguishable from absent

### Requirement: VIDEO-STORY-PANEL-004 Story-body immutability
Raw body edits on video-story assets SHALL be refused, because shots are
immutable and re-scripting is the update path; an edit would store bytes the
export answers 500 for.

#### Scenario: VIDEO-STORY-PANEL-004 Success
- **WHEN** the owner re-scripts through the generate route
- **THEN** a new revision is appended and the export serves it once approved

#### Scenario: VIDEO-STORY-PANEL-004 Failure
- **WHEN** a PATCH edit targets a video-story asset
- **THEN** the route answers 422 naming `bodyMarkdown` with the re-script
  remedy, stores nothing, and the approved export still serves identical
  bytes

#### Scenario: VIDEO-STORY-PANEL-004 Boundary
- **WHEN** a PATCH edit targets any other asset type
- **THEN** the delivered edit behavior is unchanged
