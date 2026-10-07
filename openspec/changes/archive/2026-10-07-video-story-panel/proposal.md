# Proposal: Video story read/export Web UI (video-story-panel)

## Why
`video-story-scripts` (`cfaacc1`) is delivered and archived with passing .NET
scenarios and no Web UI: its routes exist — generate POST, `GET
/api/assets/{id}/export?format=video-story`, and the publish-refusal POST
answering `publisher_unsupported` — but no panel reads them. An owner with an
approved story and selected screenshots cannot script a 15/30/60-second
storyboard, preview its shot order, approve it, or download the handoff
scripts without leaving the product for `curl`. requirement.md §7 names Video
Story as the second phase (shot order, screenshots, subtitles, narration,
transition suggestions; script first, synthesis later), and the contract
forbids automatic synthesis, fabricated footage and automatic publication —
so the UI must show that boundary, not just omit it. Status: specified
handoff; implementation not started. Stage: follow-up over delivered routes;
depends only on archived packages (`video-story-scripts`,
`markdown-review-export`, `screenshot-curation`, `grounded-markdown-drafts`,
`voice-profiles`).

## What Changes
1. One Web feature, `video-story-panel`, mounted on Evolution-area milestone
   rows under the capture panel: a storyboard surface with a duration select
   (15/30/60s), a source-story picker and screenshot/evidence pickers over
   delivered routes (no hand-typed IDs), a shot composer (screenshot,
   caption, narration, transition per shot with server-derived contiguous
   timings), an inert shot-order preview with screenshots, subtitles,
   narration and transitions, a press-only approval through the delivered
   review routes, and an approval-gated download that hands the server's exact
   ZIP bytes (storyboard.json + narration.md + caption.srt + depicted
   pictures + manifest) to a file. The no-synthesis boundary is stated on
   screen: scripts only, no video synthesized, no footage fabricated, nothing
   published automatically.
2. Native-video publish refusal surfaced verbatim: an explicit publish-attempt
   control POSTs the delivered refusal route and shows the server's own
   `publisher_unsupported` code and message. No renderer, no encoder, no
   account, no transport anywhere.
3. One Playwright suite, `tests/video-story-panel.spec.ts`, wired as a new
   database-isolated block in `scripts/e2e.sh`: select → preview → approve →
   export byte-equal to the server; refusal codes verbatim
   (`approval_required`, `approval_stale`, `publisher_unsupported`); honest
   empty states; armed request counters proving nothing fires before its
   explicit control and no request ever aims at a video host.
4. Two genuinely missing/defective backend pieces, each with its own spec
   requirement plus tests (or deferred with a named reason — neither is):
   a milestone-scoped script discovery read (`GET
   `/api/milestones/{id}/video-story``) so the panel can find the one
   video-story asset a reload must show, and a refusal of raw body edits on
   video-story assets (a PATCH stores non-canonical JSON whose export is a
   500; shots are immutable and re-scripting is the update path).

## Package Boundary and Split Assessment
Single outcome, one actor (the owner), one lifecycle (script → review →
export, never synthesize, never send), one acceptance oracle (the new browser
suite plus byte-equality plus the two backend facts). Split signals
considered: the UI surface could ship without the two backend pieces, but
then a reload would lose the script the owner just generated (no discovery
read) and the delivered generic edit route would corrupt the export it gates
— an unusable intermediate state, so they stay in this package with their own
requirements (VIDEO-STORY-PANEL-003/004) and tests. No other change's files
are touched.

## Sibling and Shared Architecture Reconnaissance

| Candidate | Evidence path/symbol | Reusable code/config/architecture | Compatibility gap | Owner and release boundary | Decision |
|---|---|---|---|---|---|
| Pack panels + shared review section | `features/content-pack-panels/LinkedInPackPanel.tsx`, `PackReviewSection.tsx` | Picker-over-routes, verbatim server codes, per-entity CRUD matrix, inert preview, explicit-click-only mutations, armed POST counters | Pack routes differ (adapt/export/refusal; packs allow PATCH edit, storyboards forbid it) | Archived `content-pack-panels`; same repo/release | **adopt** patterns, new local feature dir |
| Review client | `features/markdown-review-export/api.ts` (readReview/approveAsset/exportAsset) | Approval binding and byte download over delivered routes | Export format differs (`video-story` query value); edit client deliberately unused | Archived `markdown-review-export`; same repo/release | **adopt** via import, extend format use locally |
| Variant panel | `features/content-asset-variants/ContentVariantPanel.tsx` | Derive-then-approve flow, head re-read at click time | Variants list route excludes video-story by design; derive is template render, scripting is owner-authored shots | Archived `content-asset-variants`; same repo/release | **adopt** conventions only |
| Curation picker + media serving | `features/screenshot-curation/ScreenshotPicker.tsx`, `api.ts` (`mediaBaseUrl`) | Selected-screenshot list with served thumbnails, no invented pictures | Curation owns selection; this panel only depicts | Archived `screenshot-curation`; same repo/release | **adopt** via import, depict only |
| Platform libs | `dotnet-platform-libs/README.md` (Platform.Jobs/Storage families) | Jobs/storage contracts only | No milestone/narrative/voice/content/video workflow | Sibling platform repo; versioned feed | **keep local**, reuse nothing new |

No shared `/lib` added; no sibling edit authorized. Chronicleaf
domain/workflow lifecycle and user review are product-specific.

## User Experience and Interface Impact
Actor: the single-user owner, on the project Evolution area, after the story
is approved and screenshots are selected. Entry: milestone row → Evolution
area → capture panel → the video-story panel below it. Primary flow: pick
duration → compose shots (screenshot from the selected-pictures select,
caption, single-line narration, cut/fade, evidence citations checked over the
delivered bundle) → Generate → inert shot-order preview with served
thumbnails, timings, subtitles, narration and transitions → approve through
the delivered review control → download exact ZIP bytes. Alternate flows:
honest empty states (unconfirmed milestone, no story, story never approved,
no selected screenshot, no script yet); verbatim refusals
(`invalid_story_schema`, `approval_required`, `approval_stale`,
`revision_conflict`, `publisher_unsupported`); validation errors naming the
field. No POST fires on render or picker change: generate, approve, download
(attachment GET) and publish-attempt each leave only from their own explicit
control, and the suite arms a request counter to prove it. Responsive/keyboard:
native selects/inputs/buttons/checkboxes, focus order follows the flow,
errors use `role="alert"`. No destructive action exists (exports are
downloads; regeneration appends a revision; there is no delete).

## BFS Impact Map
- Capabilities: new `VIDEO-STORY-PANEL-001..004`; consumes
  `video-story-scripts` (generate/export/refusal), `markdown-review-export`
  (asset read, review, approve, unapprove routes), `screenshot-curation`
  (selected pictures + served bytes), `grounded-markdown-drafts` (story read,
  evidence head), `voice-profiles` (voice revision). No capability redefined.
- Users/flows: owner Evolution area gains one panel per milestone row; no
  other surface changes.
- Contracts/data/persistence: one additive GET read
  (`/api/milestones/{id}/video-story`, null asset when none, 404 for
  foreign/absent milestone) plus one additive guard (PATCH on a video-story
  asset refused 422 naming `bodyMarkdown`); no migration, no envelope change,
  no new error code.
- Integrations/config: none; no provider, no credential lease, no renderer,
  no encoder, no socket to any video host.
- Callers: `MilestoneRows.tsx` gains one import block (evolution detail);
  `ContentReviewService.EditAsync` gains a type guard; `VideoStoryEndpoints`
  gains the discovery read; all other callers untouched.
- Failure/boundary: verbatim `invalid_story_schema` (422),
  `approval_required`/`approval_stale` (409), `revision_conflict` (409),
  `publisher_unsupported` (422), validation 422s; empty states honest.
- Tests: new Playwright suite + e2e.sh block; .NET facts added to the
  existing `VideoStoryScriptsTests` stage (no new stage, no verify.sh edit);
  full pre-existing browser set must stay green.
- Compatibility/privacy/security: same-origin session cookie, per-call
  antiforgery, owner-scoped reads; no script bytes leave the browser except
  as a file the owner chose to save; no secrets in UI.
- Unaffected (explicit): publisher transports, journey, static blog,
  evolution images/GIFs, agency/Cloud packages, migration chain.

## Capabilities
- `video-story-panel`: the storyboard read/export panel with duration +
  source pickers, shot composer, inert preview, approval, exact-byte
  download and verbatim publish refusal over delivered routes, plus the two
  backend pieces (discovery read, edit refusal) with their own requirements.
- E2E proof: select → preview → approve → byte-equal download;
  refusal/code verbatim; empty states; nothing synthesising, nothing sent.

## Non-goals
- Any video synthesis, rendering, encoding, screen recording, footage upload,
  or publishing transport (the contract forbids them; the UI states the
  boundary instead).
- Any other backend route, migration, envelope or publisher transport change
  (unless verification proves one missing/defective, then with its own
  requirement).
- Asset/revision/approval deletion routes (named future package
  `content-asset-retirement`, inherited hole, UI offers none).
