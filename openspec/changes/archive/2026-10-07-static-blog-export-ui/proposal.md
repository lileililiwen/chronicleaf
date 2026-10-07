# Proposal: Static blog export UI (static-blog-export-ui)

## Why
The `static-blog-export` backend is delivered and archived (`066e153`):
three private routes (POST request, GET status, GET download), frozen
per-asset bindings, deterministic ZIP (`index.html`, `posts/`,
`manifest.json`), verbatim refusals (`approval_required`,
`approval_stale`, `media_unavailable`, `static_blog_superseded`). But no
Web UI reaches it: the archived design's own line 14 names a "React
ExportPreview" that was never built, and the Publish project area
(`ProjectPublish`) mounts only `PublicationPanel` and `JourneyPanel`.
An approved static site therefore cannot be exported by clicking — the
contract is reachable only by callers who already know asset ids. Status:
Specified handoff; implementation not started. Stage: POST-MVP
follow-up over delivered routes; depends only on archived packages.

## What Changes
1. An `ExportPanel` (with the design's `ExportPreview` surface) in the
   Publish project area beside `PublicationPanel`/`JourneyPanel`:
   milestone picker over the delivered project-milestones route, asset
   checkboxes from the delivered draft read plus the variant list (each
   labelled by kind/type and revision, never a bare id), per-asset
   review reads splitting the selection into included (live approval)
   versus excluded (draft/invalidated, with the server's own status
   word), an optional base-path field, an idempotency key, an explicit
   Export button, operation watching through the delivered status
   route, and the portable ZIP download offered only when the server
   reports `succeeded`.
2. Server refusals shown verbatim from the delivered envelope
   (`approval_required`, `approval_stale`, `media_unavailable`,
   `static_blog_superseded`, validation/traversal codes); nothing
   fires on render except the reads that fill the pickers; no
   invented progress — the panel reports the status route's status
   word, counts and digest only.
3. A new Playwright suite (`tests/static-blog-export-ui.spec.ts`,
   wired as its own database-isolated block in `scripts/e2e.sh`)
   proving: select → export → watch → download bytes match the
   server; refusal verbatim; empty state honest; nothing auto-fires.
4. No backend change: POST, status and download routes stay
   byte-identical. No hand-typed ids anywhere in the new surface.

## Package Boundary and Split Assessment
Single outcome: an owner can export an approved static site by
clicking, through pickers, and download exactly the bytes the server
built. Included: the panel, its API client, Publish-area placement,
and the browser suite with its seed. Excluded: new transports,
credential flows, journey projection changes, export history
(no list route serves past exports; each export is watched by the
operation id its own request answered — reported as named future
package `export-history`, never invented). Depends on (all archived):
static-blog-export (routes and envelope), content-asset-variants
(rows and list route), markdown-review-export (approval binding),
grounded-markdown-drafts (draft read), projects-and-milestones (list
route), project-area-ia (Publish area placement),
publish-from-variant (picker/receipt conventions). This is the
smallest independently verifiable unit: the browser suite proves the
click path end to end.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Export selection, approval-bound preview display and
the ZIP download are Chronicleaf-product workflow; no sibling
platform contract offers them. Evidence inspected:
dotnet-platform-libs/README.md lists Platform.Jobs and
Platform.Storage families but no milestone/narrative/voice/content/
publisher workflow (same finding as the archived static-blog-export
proposal). Reused as-is: the delivered export routes, the review
read, the draft/variant reads, the milestone list, the operations
status vocabulary. No shared `/lib` added and no sibling edit
authorized.

## BFS Impact Map
- Actor/flow: authenticated project owner; anonymous reaches
  nothing (all three export routes carry `RequireOwnerSession()`).
- Boundary/callers: new `src/Chronicleaf.Web/src/features/static-blog-export/`
  (`api.ts`, `ExportPanel.tsx`); one placement edit in
  `ProjectPublish.tsx`; new `tests/static-blog-export-ui.spec.ts`
  plus one `scripts/e2e.sh` block. No other Web file touched.
- Data/integration: no schema, no migration, no new route; the POST
  freezes bindings, the worker builds, the download re-checks — all
  delivered behavior, observed rather than changed.
- Verification: new browser suite (own disposable PostgreSQL, own
  published app, jobs enabled so the worker really builds); `npm ci`,
  `tsc` + `vite build`; full `bash scripts/e2e.sh` green;
  `git diff --check`; strict validate; archive without `--skip-specs`.
- Privacy/compatibility: no credential, body text or picture in any
  new read except the owner's own approved bytes flowing to their
  own download; refusals carry the envelope only.

## Capabilities
### New Capabilities
- `static-blog-export-ui`: Approved static blog export by clicking.

## Non-goals
Other roadmap capabilities; replacing shared infrastructure;
automatic growth/Star/comment/DM activity; unreviewed publishing;
no-runtime completion claims. No implementation, archive, commit,
push or deployment in this authoring work.
