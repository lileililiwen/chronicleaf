# Spec: Static blog export UI (static-blog-export-ui)

## ADDED Requirements

### Requirement: STATIC-BLOG-EXPORT-UI-001 Picker selection with included/excluded preview
The Web client SHALL offer static blog export selection through pickers over delivered list routes — milestones of the project, then the milestone's draft plus its variants — showing per-asset review state as included (live approval) versus excluded (any other status, named with the server's own word), with no hand-typed ids and nothing firing on render except the reads that fill the pickers.

#### Scenario: STATIC-BLOG-EXPORT-UI-001 Success
- **WHEN** the owner opens the export surface on a project with a milestone holding an approved story
- **THEN** the milestone picker lists that milestone, the asset options list the story labelled by kind and revision, and the checked story shows as included with its live approval

#### Scenario: STATIC-BLOG-EXPORT-UI-001 Failure
- **WHEN** the server refuses a picker read or a review read for a picked asset
- **THEN** the panel shows the envelope code and message verbatim and offers no export built from an unread selection

#### Scenario: STATIC-BLOG-EXPORT-UI-001 Boundary
- **WHEN** the project holds no milestone, or the picked milestone holds no draft and no variants, or no export has been requested yet
- **THEN** the panel shows the honest empty state for exactly that case and offers no phantom option and no phantom status

### Requirement: STATIC-BLOG-EXPORT-UI-002 Explicit export, watched job, download only when ready
The Web client SHALL send the export POST only from an explicit Export press carrying a fresh idempotency key, watch the resulting job through the delivered status route reporting its status word verbatim with no invented progress, and offer the portable ZIP download only when the server reports `succeeded`; any refusal (`approval_required`, `approval_stale`, `media_unavailable`, `static_blog_superseded`, validation codes) is shown verbatim with no download offered.

#### Scenario: STATIC-BLOG-EXPORT-UI-002 Success
- **WHEN** the owner checks approved assets and presses Export
- **THEN** the POST answers 202 with the watched operation, the panel reports the terminal `succeeded` state with the server's post, image and digest counts, and the offered download serves bytes byte-equal to the delivered download route

#### Scenario: STATIC-BLOG-EXPORT-UI-002 Failure
- **WHEN** the server refuses the export (unapproved selection, stale approval, missing media, superseded job, invalid base path or selection)
- **THEN** the panel shows the envelope code and message verbatim, records no export, and offers no download

#### Scenario: STATIC-BLOG-EXPORT-UI-002 Boundary
- **WHEN** the panel merely renders, derives, or holds a selection without the Export press
- **THEN** no export POST fires, no job is watched, and no download is offered; an identical resubmission answers the original job rather than starting a second one
