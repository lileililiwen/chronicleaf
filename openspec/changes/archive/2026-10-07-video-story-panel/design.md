# Design: Video story read/export Web UI (video-story-panel)

## 1. Implementation boundary
Repository Chronicleaf only. Primary boundary
`src/Chronicleaf.Web/src/features/video-story-panel/` (new `api.ts`,
`types.ts`, `VideoStoryPanel.tsx`, `VideoStoryReviewSection.tsx`); mount in
`src/Chronicleaf.Web/src/features/project-areas/MilestoneRows.tsx` evolution
detail under `ScreenshotCapturePanel`; e2e seed + suite-27 block in
`scripts/e2e.sh` plus `src/Chronicleaf.Web/tests/video-story-panel.spec.ts`.
Backend, only the two named pieces: `GET
/api/milestones/{milestoneId}/video-story` on `VideoStoryEndpoints` served by
a new `IVideoStoryService.ReadForMilestoneAsync`, and a video-story type
guard in `ContentReviewService.EditAsync`. Dependencies own their contracts
(`video-story-scripts`, `markdown-review-export`, `screenshot-curation`,
`grounded-markdown-drafts`, `voice-profiles`); do not redefine them.
Preserve unrelated files and exactly one `current_spec` while working.

## 2. Language and runtime
C# net10.0 / ASP.NET Core 10 for the two backend pieces; React + TypeScript +
Vite for the panel. PostgreSQL is the single store; no migration (the
discovery read projects the delivered `content_assets`/`content_revisions`
rows; the edit guard changes no schema). .NET facts extend the existing
`VideoStoryScriptsTests` class so `scripts/verify.sh` needs no new stage.
Browser suite runs on its own disposable PostgreSQL + published app at port
5107 behind `scripts/e2e.sh`.

## 3. Ownership and shared code
**Adopt** the pack-panel patterns (`PackReviewSection`, picker-over-routes,
verbatim codes, armed POST counters, inert preview) into a new local feature
dir; **adopt** the review/curation/draft/voice API clients via import (extend
no shared module except the edit guard's home service); **keep local** all
panel state (no shared `/lib`). The discovery read lives in the video-story
package's own endpoints/service — not in the variants list route, whose
contract explicitly excludes the video-story kind ("video-story selection is
that package's own surface"). No sibling edit authorized.

## 4. User experience and interface
`VideoStoryPanel({projectId, milestoneId, milestoneTitle, archived,
confirmed})` on every Evolution-area milestone row:

- About copy states the boundary first: scripts only — no video is
  synthesized, no footage is fabricated, nothing is published automatically;
  the download is a handoff package a human acts on.
- Load reads (delivered routes only): story via `GET
  /api/milestones/{id}/drafts`, story approval via review read, script via
  the new discovery read, script body via `GET /api/assets/{id}`,
  script approval via review read, evidence head via `GET
  /api/milestones/{id}/evidence` (items carry `mediaId`), selected pictures
  via `GET /api/milestones/{id}/screenshots` (with `mediaBaseUrl`), voice
  revision for attribution.
- Empty states, each honest and each with no control that could act:
  unconfirmed milestone (confirm first); no story; story never approved
  (approve the story first); no selected screenshot (select or omit — never
  invented); no script yet.
- Composer (archived milestones: hidden): duration select 15/30/60; shot list
  with per-shot screenshot select (selected pictures only, captioned
  options), caption input (1..1000), narration input (single line, 1..2000,
  no ` --> `), transition select (cut/fade), evidence checkboxes (default:
  the evidence items whose `mediaId` matches the chosen screenshot; at least
  one required). Timings are derived, never typed: N shots split the runtime
  evenly (startMs/endMs shown per shot); add-shot disabled once the split
  would drop below 500ms; remove-shot per row. Generate POSTs the delivered
  generate route with heads re-read at click time (stale heads → 409, never a
  script against unseen provenance).
- Stored-script preview (`VideoStoryReviewSection`, keyed by asset id):
  revision, duration, status, body hash, voice attribution; shot order with
  served thumbnails (`{mediaBaseUrl}/{mediaId}` `<img>`, never a
  client-rendered picture), per-shot timing range, caption (subtitle),
  narration (spoken line), transition, cited evidence ids; known limitations
  carried from the document. Preview renders owner prose as text, never as
  markup.
- Approval is a press-only button through the delivered approve route bound
  to the head revision + evidence head; failures verbatim. Download is always
  offered once a script exists: success hands the server's ZIP to a file;
  `approval_required`/`approval_stale`/`media_unavailable` render verbatim as
  `code: message` with no file. Regenerating appends a revision (shots are
  immutable; re-scripting is the update path) and returns the script to draft
  until re-approved — the suite asserts this round-trip.
- Publish-attempt button POSTs the delivered refusal route; the 422
  `publisher_unsupported` code + message render verbatim; no socket opens, no
  account action, no audit row.
- Nothing fires on render or picker change: generate, approve, download and
  publish-attempt each leave from their own explicit control only.

## 5. Behavioral model

| Step | Route | Method | Idempotency / precondition |
|---|---|---|---|
| Discover script (NEW) | `/api/milestones/{id}/video-story` | GET | null asset when none (never 404); foreign/absent milestone → delivered 404 |
| Read story | `/api/milestones/{id}/drafts` | GET | null asset when none |
| Read script body | `/api/assets/{id}` | GET | canonical storyboard JSON, parsed client-side |
| Read heads | evidence head, voice profile, curated screenshots | GET | generate binds what was read |
| Generate | `/api/milestones/{id}/video-stories` | POST + antiforgery | body carries duration + source + evidence/voice revisions + shots; stale → 409; schema failure → 422 `invalid_story_schema`; nothing stored on refusal |
| Edit (REFUSED, changed) | `/api/assets/{id}` | PATCH + If-Match + antiforgery | video-story type → 422 naming `bodyMarkdown` (re-script instead); all other types unchanged |
| Approve | `/api/assets/{id}/approve` | POST + antiforgery | contentRevision + evidenceRevision current; voice/grounding findings block |
| Export | `/api/assets/{id}/export?format=video-story` | GET | live approval re-checked server-side; ZIP byte-deterministic (fixed entry order + timestamp) |
| Publish-attempt | `/api/assets/{id}/video-story/publish` | POST + antiforgery | always 422 `publisher_unsupported` (404 for foreign id) |

Generate answers 201 with the new revision, not a job. Exports are
synchronous attachments. The discovery read answers the milestone's single
video-story asset (unique index on scope/milestone/type makes it one row):
`{assetId, revision, bodyHash, durationSeconds, shotCount, evidenceRevision,
voiceRevision, updatedAt}` or `{assetId: null}`.

## 6. Contract and compatibility
Wire shapes follow the delivered endpoints exactly: generate body
`{durationSeconds, sourceAssetId, evidenceRevision, voiceRevision,
shots:[{screenshotMediaId, caption, narration, transition, startMs, endMs,
sourceEvidenceIds}]}`; error envelope `{code,message,traceId,fieldErrors?}`
shown verbatim as `code: message`. One additive GET, one additive 422 guard
on an existing route, no new error code (`validation_failed` is delivered
vocabulary), no envelope change, no migration. Backward compatible by
construction: additive UI over frozen routes plus a guard that only refuses
writes which previously stored bytes the export answered 500 for.

## 7. Failure and boundary policy
- Unconfirmed / no story / story never approved / no selected screenshot / no
  script → honest empty text, no acting control.
- Generate refusal (`invalid_story_schema`, `approval_required`,
  `approval_stale`, `revision_conflict`, field 422s) → verbatim, nothing
  stored, panel re-reads on retry.
- Raw edit of a script body → 422 naming `bodyMarkdown` with the re-script
  remedy; previously stored bytes keep serving under their live approval.
- Export without live approval → `approval_required`; moved →
  `approval_stale`; missing bytes → `media_unavailable`. All verbatim, no
  file offered.
- Publish-attempt → 422 `publisher_unsupported` verbatim, no account action,
  no socket. Foreign id → delivered 404.
- No delete route exists for assets/revisions/approvals/exports (see §9): the
  UI offers none and says nothing that implies one. Unapprove exists as a
  route but the panel does not offer it (parity with delivered pack panels;
  withdrawal is reachable and regeneration invalidates by staleness).

## 8. Verification oracle
- `npm ci`, `tsc --noEmit`, `vite build` green.
- Extended `VideoStoryScriptsTests` green (existing stage, no verify.sh
  edit): discovery read empty → null asset; after generate → names the
  asset/revision/duration; foreign milestone → 404; sessionless → 401; PATCH
  edit on a script → 422 naming `bodyMarkdown` with the approved export still
  serving identical bytes.
- New suite `tests/video-story-panel.spec.ts` green on its own disposable
  PostgreSQL against the published app (suite 27, port 5107,
  `seed_video_story_panel`): select duration + source material over delivered
  routes → Generate 201 → preview shows shot order with screenshots,
  subtitles, narration, transitions → pre-approval download refused
  `approval_required` verbatim → approve → download ZIP byte-equal to a
  direct server GET (PK magic + SHA-256 shape) → publish-attempt 422
  `publisher_unsupported` verbatim → regenerate returns to draft and
  re-approval re-opens the download; bare milestone states every empty case
  honestly; armed counters prove no POST fired before its control and no
  request ever aimed at a video host (there is none).
- Full `bash scripts/e2e.sh` green (27 suites incl. pre-existing).
- `git diff --check` clean; `openspec validate --all --strict --no-interactive`
  green; archive without `--skip-specs`; canonical spec promoted.

## 9. CRUD lifecycle matrix (mandatory)

| Entity | Create | Read | Update | Delete |
|---|---|---|---|---|
| Video story script (content asset, type `video-story`) | POST generate (201, appends revision 1 or head+1) — in scope | NEW GET milestone discovery read + GET asset (canonical JSON preview) — in scope | Regenerate appends revision (shots immutable; re-scripting is the update path) — in scope; raw PATCH refused 422 — in scope (defect repair) | No route serves asset deletion; UI offers none — **out of scope → named future package `content-asset-retirement`** (inherited hole, same name as `content-pack-panels`) |
| Script revision (`content_revisions` for the script) | Generate appends (CAS on head) — in scope | GET asset/review (head revision, ETag, body hash) — in scope | Rows immutable by trigger; supersede by regeneration — by design, no future package (append-only is the contract) | Same immutability — by design, no future package |
| Approval binding (`content_approvals` for the script) | POST approve (live row bound to revision+hash+evidence+voice+pictures) — in scope | GET review (status + binding shown) — in scope | POST approve of a newer revision (idempotent same-state re-approve) — in scope | POST unapprove exists as the delete-equivalent (row kept as history) but the panel offers no control — route-complete, UI-unoffered by parity with pack panels (reported here, not silent); asset deletion itself → `content-asset-retirement` |
| Exported storyboard package (ZIP: storyboard.json + narration.md + caption.srt + pictures + manifest) | GET export (derived bytes per call, nothing stored) — in scope | Same GET re-checked per call (approval, evidence, media, hashes) — in scope | Re-export after re-approval — in scope | Bytes never stored server-side, nothing to delete — N/A by design, stated on screen |

Out-of-scope holes are named, never silent: `content-asset-retirement`
(asset deletion) and the UI-unoffered unapprove control (route exists,
parity with delivered pack panels). Neither is built here.

## Decision ledger
- Evolution area, not Content: the storyboard depicts selected screenshots in
  shot order beside the capture panel that produced them; it consumes (never
  redefines) the Content area's story and approval.
- Timings derived (even split), never typed: the server demands exact
  contiguous coverage, and a form that could type gaps would only produce
  refusals the panel then has to explain.
- Evidence citations default to the items depicting the chosen screenshot
  (matched by `mediaId` from the delivered evidence read): the common case
  needs no checkbox hunt, and every id still comes from a delivered route.
- Download always offered once a script exists (refusals verbatim) rather
  than hidden until approved: hiding the control would hide the
  `approval_required` answer the contract requires to be visible.
- No edit control for script bodies (the PATCH guard): the canonical JSON is
  the server's to write; owner words change through re-scripting.
