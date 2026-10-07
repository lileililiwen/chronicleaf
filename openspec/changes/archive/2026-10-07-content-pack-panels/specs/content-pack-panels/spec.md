# Spec: Content pack panels (content-pack-panels)

## ADDED Requirements

### Requirement: CONTENT-PACK-PANELS-001 LinkedIn copy read/export panel
The Web client SHALL give the delivered LinkedIn copy backend a read/export
panel over its delivered routes: derive/select a copy from the approved story,
show the copy-ready text with owned media and voice attribution, offer
exact-byte download/ZIP export, and surface the native-publish refusal
`publisher_unsupported` verbatim where reachable. The panel SHALL send
nothing and post nothing.

#### Scenario: CONTENT-PACK-PANELS-001 Success
- **WHEN** the owner derives a copy from the approved story and approves the copy
- **THEN** the panel previews the server's text with media and voice, and the
  download matches the server's export bytes exactly.

#### Scenario: CONTENT-PACK-PANELS-001 Failure
- **WHEN** native LinkedIn publication is attempted
- **THEN** the panel shows the server's own `publisher_unsupported` code and
  message verbatim with no account action.

#### Scenario: CONTENT-PACK-PANELS-001 Boundary
- **WHEN** the story was never approved or the copy approval moved
- **THEN** the panel states so honestly and offers no download, naming the
  server's `approval_required`/`approval_stale` answer.

### Requirement: CONTENT-PACK-PANELS-002 Medium import read/export panel
The Web client SHALL give the delivered Medium import backend a manual-material
panel over its delivered routes: the approved article's HTML/Markdown, images
and manifest shown for copy/download, the export-only boundary stated, and
`publisher_unsupported` reported without any upstream call per contract. The
panel SHALL send nothing and post nothing.

#### Scenario: CONTENT-PACK-PANELS-002 Success
- **WHEN** the owner selects an approved article and downloads the Medium export
- **THEN** the bytes match the server's export exactly and no remote Medium URL
  or successful post is fabricated.

#### Scenario: CONTENT-PACK-PANELS-002 Failure
- **WHEN** native Medium publication is attempted
- **THEN** the panel shows the server's own `publisher_unsupported` code and
  message verbatim with no upstream call.

#### Scenario: CONTENT-PACK-PANELS-002 Boundary
- **WHEN** a source image is unavailable
- **THEN** no complete image archive is falsely offered; the server's
  `media_unavailable` answer is shown verbatim.

### Requirement: CONTENT-PACK-PANELS-003 Xiaohongshu pack read/export panel
The Web client SHALL give the delivered Xiaohongshu pack backend an editable
read/export panel over its delivered routes: titles, body, images, cover, tags
and CTA editable for human external publishing, exact-byte export behind a
live approval (re-approval required after edits per contract), and the
no-account-login / no-automatic-posting boundary stated on screen. The panel
SHALL send nothing and post nothing.

#### Scenario: CONTENT-PACK-PANELS-003 Success
- **WHEN** the owner derives a pack, edits its fields and approves the revision
- **THEN** the panel previews the edited pack and the download matches the
  server's export bytes exactly.

#### Scenario: CONTENT-PACK-PANELS-003 Failure
- **WHEN** a title or image order changes after approval
- **THEN** export requires renewed approval and the panel names
  `approval_stale` verbatim.

#### Scenario: CONTENT-PACK-PANELS-003 Boundary
- **WHEN** the pack is generated or exported
- **THEN** no account login and no automatic posting occur, and the panel
  states that publication happens externally by the human.

### Requirement: CONTENT-PACK-PANELS-004 Milestone asset list serves pack kinds
The delivered milestone asset list SHALL include the derived LinkedIn copy
and Xiaohongshu pack kinds when asked with `?kinds=packs`, so the panels can
select a previously derived pack over a delivered route. The default read
stays exactly the variant formats older callers rely on; any other kinds
value is refused by name. The story itself stays out in both modes (it has
its own draft read); no other list behavior changes.

#### Scenario: CONTENT-PACK-PANELS-004 Success
- **WHEN** a copy or pack is derived from the approved story and the list is read with `?kinds=packs`
- **THEN** the list names it with its type, revision and digests.

#### Scenario: CONTENT-PACK-PANELS-004 Failure
- **WHEN** no copy or pack was ever derived
- **THEN** both reads answer exactly what the default read answered before, with no pack entry invented.

#### Scenario: CONTENT-PACK-PANELS-004 Boundary
- **WHEN** a milestone holds variants and packs but the list is read without the query
- **THEN** the list names only the variants, unchanged from before; and an unknown kinds value is 422.
