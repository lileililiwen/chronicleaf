# Proposal: Content pack panels (content-pack-panels)

## Why
Three content-pack backends are delivered and archived with passing .NET
suites and no Web UI: `linkedin-copy-package` (`637a1fc`), `medium-import-package`
(`780b6fd`) and `xiaohongshu-content-package` (`75a8fba`). Their routes exist —
adapt POSTs, `GET /api/assets/{id}/export?format=linkedin|medium|xiaohongshu`,
and publish-refusal POSTs answering `publisher_unsupported` — but no panel
reads them. An owner with an approved story cannot derive copy-ready LinkedIn
text, hand-carry Medium import material, or assemble a Xiaohongshu pack
without leaving the product for `curl`. Status: specified handoff;
implementation not started. Stage: follow-up over delivered routes; depends
only on archived packages.

## What Changes
1. One Web feature, `content-pack-panels`, mounted in the Content area under
   the variant panel: three read/export panels (LinkedIn copy, Medium import,
   Xiaohongshu pack), each with a picker over delivered routes, an inert
   preview, a per-pack derive control where the contract has one
   (LinkedIn, Xiaohongshu), an exact-byte download, and the contract's
   no-send boundary stated on screen rather than merely absent.
2. Native-publish refusal surfaced verbatim where reachable: each panel offers
   an explicit publish-attempt control that POSTs the delivered refusal route
   and shows the server's own `publisher_unsupported` code and message; Medium
   additionally reports `publisher_unsupported` without any upstream call per
   its contract. No account login, no browser posting, no auto-post anywhere.
3. One Playwright suite, `tests/content-pack-panels.spec.ts`, wired as a new
   database-isolated block in `scripts/e2e.sh`: derive → preview → download
   byte-equal to the server, refusal/code verbatim, honest empty states, and
   an armed request counter proving nothing posts anywhere.

## Package Boundary and Split Assessment
Single outcome, one actor (the owner), one lifecycle (derive → review →
export, never send), one acceptance oracle (the new browser suite plus
byte-equality). Split signals considered: three packs could be three
changes, but they share the panel host, the api client, the seed shape, the
e2e block and the CRUD matrix; separating them would triple the scaffolding
for no independent value. No backend change is in scope unless verification
finds a delivered route genuinely missing/defective — each such repair gets
its own spec requirement plus tests, or is deferred with a named reason. No
other change's files are touched.

## Sibling and Shared Architecture Reconnaissance

| Candidate | Evidence path/symbol | Reusable code/config/architecture | Compatibility gap | Owner and release boundary | Decision |
|---|---|---|---|---|---|
| Variant/publish panels | `features/content-asset-variants/ContentVariantPanel.tsx`, `VariantPublishSection.tsx`, `features/publications/PublishForm.tsx`, `AdapterTargetPanel.tsx` | Picker-over-routes, verbatim server codes, per-entity CRUD matrix, inert preview, explicit-click-only mutations | Pack routes differ (adapt/export/refusal, no publish receipt) | Archived `publish-from-variant`, `publisher-adapter-panels`; same repo/release | **adopt** patterns, new local feature dir |
| Review client | `features/markdown-review-export/api.ts` (readReview/approve/edit/exportAsset) | Approval binding and byte download over delivered routes | Export formats differ (`linkedin`/`medium`/`xiaohongshu` query values) | Archived `markdown-review-export`; same repo/release | **adopt** via import, extend format union locally |
| Static blog export UI | `features/static-blog-export/ExportPanel.tsx` | Picker honesty, download-only-on-ready, verbatim codes | Job-watch loop not needed (pack exports are synchronous attachments) | Archived `static-blog-export-ui`; same repo/release | **adopt** conventions only |
| Platform libs | `dotnet-platform-libs/README.md` (Platform.Jobs/Storage families) | Jobs/storage contracts only | No milestone/narrative/voice/content/publisher workflow | Sibling platform repo; versioned feed | **keep local**, reuse nothing new |

No shared `/lib` added; no sibling edit authorized. Chronicleaf
domain/workflow lifecycle and user review are product-specific.

## User Experience and Interface Impact
Actor: the single-user owner, on the project Content area, after the story is
approved. Entry: milestone row → Content area → variant panel → the three pack
panels below it. Primary flow per pack: pick source/derived pack → derive (or
select for Medium) → inert preview with voice attribution and owned media →
approve through the delivered review control → download exact bytes. Alternate
flows: honest empty states (no story, never approved, no pack yet); verbatim
refusals (`approval_required`, `approval_stale`, `publisher_unsupported`);
validation errors naming the field. No POST fires on render or picker change:
derive, publish-attempt and download each leave only from their own explicit
control, and the suite arms a request counter to prove it. Responsive/keyboard:
native selects/buttons, focus order follows the flow, errors use `role=alert`.
No destructive action exists (exports are downloads; there is no delete).

## BFS Impact Map
- Capabilities: new `CONTENT-PACK-PANELS-001..003`; consumes `linkedin-copy-package`,
  `medium-import-package`, `xiaohongshu-content-package`, `markdown-review-export`
  (review routes), `content-asset-variants` (list), `grounded-markdown-drafts`
  (evidence head), `voice-profiles` (voice revision). No capability redefined.
- Users/flows: owner Content area gains three panels; no other surface changes.
- Contracts/data/persistence: no new routes, no migration, no envelope change;
  reads `GET /api/milestones/{id}/assets`, `GET /api/assets/{id}`,
  `GET /api/assets/{id}/review`, `GET .../export?format=`, POST adapt and
  publish-refusal routes exactly as delivered.
- Integrations/config: none; no credential leased, no socket to any pack host.
- Callers: `MilestoneRows.tsx` gains one import block; all other callers untouched.
- Failure/boundary: verbatim `publisher_unsupported` (422), `approval_required`/
  `approval_stale` (409), validation 422s; empty states honest.
- Tests: new Playwright suite + e2e.sh block; no .NET stage added (no backend change
  expected); full pre-existing browser set must stay green.
- Compatibility/privacy/security: same-origin session cookie, per-call
  antiforgery, owner-scoped reads; no pack bytes leave the browser except as a
  file the owner chose to save; no secrets in UI.
- Unaffected (explicit): publisher transports, journey, static blog, evolution,
  agency/Cloud packages, migration chain.

## Capabilities
- `content-pack-panels`: the three read/export panels with derive/select,
  preview, approval, download and verbatim refusal over delivered routes.
- E2E proof: derive → preview → byte-equal download; refusal/code verbatim;
  empty states; nothing posts anywhere.

## Non-goals
- Any backend route, migration, envelope or publisher transport change (unless
  verification proves one missing/defective, then with its own requirement).
- Native sending, scheduling, account login, browser automation, OAuth.
- Editing media bytes, deleting assets, publication history (Delete holes are
  reported as named future packages, not built here).
