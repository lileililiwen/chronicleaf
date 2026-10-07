# Design: Static blog export UI

## Context and implementation boundary
Repository Chronicleaf only. Primary boundary
`src/Chronicleaf.Web/src/features/static-blog-export/` (`api.ts`,
`ExportPanel.tsx`); one placement edit in
`src/Chronicleaf.Web/src/features/project-areas/ProjectPublish.tsx`;
one new suite `src/Chronicleaf.Web/tests/static-blog-export-ui.spec.ts`
plus one isolated block in `scripts/e2e.sh`. No C# file touched, no
migration, no route added or altered. Dependencies (all archived):
static-blog-export, content-asset-variants, markdown-review-export,
grounded-markdown-drafts, projects-and-milestones, project-area-ia,
publish-from-variant. Each dependency owns its contracts; do not
redefine them. Preserve forge.yaml, .standard receipts, unrelated
files and current_spec.

## Language, ownership and release
React + TypeScript + Vite for the Web client; the API stays
byte-identical. Test isolation follows `scripts/e2e.sh`: the new
suite gets its own disposable PostgreSQL, its own published
application on its own port, and its own seed — with jobs **enabled**
(the default `start_suite` path, no `Chronicleaf__Operations__Enabled=false`),
because the claim under test is that the worker really builds the
archive the panel offers for download.

**Keep local.** Same finding as the archived static-blog-export
proposal: no sibling platform contract offers export selection,
approval-bound preview or ZIP download; the delivered export routes
are observed, never duplicated.

## Contract and compatibility decisions
The three delivered routes are read field for field, never wrapped
in a second vocabulary:

- `POST /api/projects/{projectId}/exports/static-blog`
  `{approvedAssetIds, basePath, expectedSelectionHash, idempotencyKey}`
  → 202 `{operationId, created}`. The panel always sends
  `expectedSelectionHash: null`: no route serves the selection hash
  to pin against, so a client-computed one would be an invented
  checksum, not a binding. `basePath` defaults to empty (root); the
  server normalizes and refuses traversal with 422 in its own words.
- `GET /api/projects/{projectId}/exports/static-blog/{operationId}`
  → `{operationId, status, errorCode, contentSha256, postCount, imageCount}`
  with `status` in `queued | running | succeeded | failed |
  cancelled | superseded`. The panel polls this route (1s, up to
  ~120s) and renders the status word verbatim; there is no progress
  bar, no percentage, no invented state.
- `GET .../{operationId}/download` → `application/zip` attachment,
  rendered as a plain same-origin anchor shown **only** when the
  status route answers `succeeded`. Any other terminal status shows
  the server's `errorCode` verbatim and offers no download.

Pickers over delivered list routes (publish-from-variant
convention): milestones from `GET /api/projects/{id}/milestones`,
assets per milestone from the draft read plus the variant list,
each option labelled by kind/type and revision. Per-asset
`readReview` splits the checked selection into included
(`reviewStatus === 'approved'`) versus excluded (any other status,
shown with that word). No hand-typed ids: the POST body is built
exclusively from picked values.

Reads on render are limited to the picker fills (milestones, draft,
variants, review states). The POST fires only from the Export
button; the suite arms a request counter before any click to prove
it. Every mutation carries a fresh antiforgery value, used once,
exactly as the delivered clients do. Failures render
`failure.message` — the envelope message verbatim — and the code is
asserted to be the server's (`approval_required`,
`approval_stale`, `media_unavailable`,
`static_blog_superseded`, validation codes).

## Behavioral model and invariants
- Included means a live approval describes the head; excluded
  means anything else, named with the server's own status word.
  Checking an excluded asset does not block the export: the server
  is the authority and refuses by name when the selection cannot
  build — the panel never second-guesses it client-side.
- One Export press carries one fresh idempotency key; an identical
  resubmission answers the original job (`created: false`) and the
  panel watches that job rather than starting another.
- The download anchor appears only on `succeeded`; the bytes are the
  server's attachment, never re-rendered client-side.
- Empty states are honest: no milestones → "nothing to select";
  milestone with no draft and no variants → "nothing to select
  from it"; no export yet → "no export requested", not a status.

## Failure, boundary and rollback policy
Every refusal below arrives through the delivered envelope and is
shown verbatim; no export state is stored client-side beyond the
watched operation id:

- Unapproved selection → `approval_required` 409, no job, no
  download offer.
- Moved approval / revoked after completion → `approval_stale`
  409 (request, status-error or download), no archive served.
- Missing picture bytes → `media_unavailable` 409.
- Revocation mid-build → job `static_blog_superseded`, withheld
  archive, no download offer.
- Traversal base path / empty selection / bad key → 422 with the
  server's field message.
- Rollback is removal of the panel placement: no stored state, no
  migration, no route to disable; past exports remain readable
  through the delivered routes.

## Verification oracle
New suite `tests/static-blog-export-ui.spec.ts` against the real
published app and a real disposable PostgreSQL seeded with one
project, two confirmed milestones (one holding an approved story,
one holding a never-approved story), one evidence bundle each, and
directly inserted content rows mirroring the publish-from-variant
seed technique (body hashed, rows read back, seed refuses to
continue otherwise):

1. Happy path: pick the approved milestone, check its story,
   counter proves no export POST before the click, press Export,
   poll to `succeeded`, assert the panel names the server's
   `postCount`/`imageCount`/`contentSha256`, click the offered
   download, and assert the downloaded bytes are byte-equal to the
   same route fetched directly (plus `PK` magic).
2. Refusal verbatim: pick the unapproved milestone, check its
   story, press Export, assert the panel shows
   `approval_required` in the server's own words and offers no
   download; the direct route agrees.
3. Empty state honest: a milestone with no draft and no variants
   (created live in-test) shows the honest empty text and no
   Export control for it.
4. Nothing auto-fires is asserted inside (1) by the armed counter.

Commands: `npm --prefix src/Chronicleaf.Web ci`,
`npm --prefix src/Chronicleaf.Web run build`
(`tsc && vite build`), the new suite through `scripts/e2e.sh`,
then the full `bash scripts/e2e.sh` green. No mock-only runtime
claims: jobs stay enabled so the worker really builds.

## Decision ledger and activation gates
Phase: POST-MVP. No unresolved product-policy decision in this
scoped contract. No backend change, so no migration-readiness or
provider question arises. The `expectedSelectionHash: null`
decision is recorded above (no hash route exists to pin against).

## Requirement traceability
| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| STATIC-BLOG-EXPORT-UI-001 | Approved static blog export by clicking / Picker selection with included/excluded preview | Pickers over delivered routes; review-state split | `features/static-blog-export/` + `ProjectPublish.tsx` | UI-001 Success/Failure/Boundary | 2.1, 3.1, 4.1 | static-blog-export-ui suite: pickers list seeded rows; empty states honest |
| STATIC-BLOG-EXPORT-UI-002 | Approved static blog export by clicking / Explicit export, watched job, download only when ready | POST on click only; poll status verbatim; anchor on succeeded | `features/static-blog-export/api.ts` + `ExportPanel.tsx` | UI-002 Success/Failure/Boundary | 2.2, 3.1, 4.1 | static-blog-export-ui suite: bytes match server; refusal verbatim; counter proves nothing auto-fires |
