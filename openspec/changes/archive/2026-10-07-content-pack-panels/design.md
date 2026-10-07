# Design: Content pack panels (content-pack-panels)

## 1. Implementation boundary
Repository Chronicleaf only, on `main`. New Web code only:

- `src/Chronicleaf.Web/src/features/content-pack-panels/api.ts` — pack client
  (adapt LinkedIn/Xiaohongshu, publish-attempt refusals, byte download for all
  three formats). Imports `readReview`/`approveAsset`/`editAsset` from the
  delivered `markdown-review-export/api.ts`, `listVariants`/`readVariant` from
  `content-asset-variants/api.ts`, `readEvidenceHead` from
  `grounded-markdown-drafts/api.ts`, `readVoiceProfile` from
  `voice-profiles/api.ts`, `renderSafeMarkdown` from
  `markdown-review-export/sanitize`. No other Web file is read for reuse.
- `src/Chronicleaf.Web/src/features/content-pack-panels/types.ts` —
  `PackApiError` (envelope code + status, verbatim) and pack shapes.
- `src/Chronicleaf.Web/src/features/content-pack-panels/LinkedInPackPanel.tsx`,
  `MediumPackPanel.tsx`, `XiaohongshuPackPanel.tsx` — the three panels plus a
  shared `PackReviewSection.tsx` (preview + approve/edit + download + refusal)
  used by all three with per-pack copy.
- `src/Chronicleaf.Web/src/features/project-areas/MilestoneRows.tsx` — mount
  the three panels in the `content` area directly under `ContentVariantPanel`,
  with the same `archived`/`confirmed` gating. No other existing file changes.
- `src/Chronicleaf.Web/tests/content-pack-panels.spec.ts` — the new suite.
- `scripts/e2e.sh` — one new database-isolated block (own port, own seed
  `seed_content_pack_panels`), following `seed_publish_variant` shape.
- Backend (CONTENT-PACK-PANELS-004 only, genuinely missing list coverage):
  `src/Chronicleaf.Application/ContentVariants/ContentVariantService.cs`
  (`ListAsync` gains the two pack kinds) and
  `ContentVariantContracts.cs` (documents it); tests in
  `tests/Chronicleaf.Integration.Tests/LinkedinCopyPackageTests.cs` and
  `XiaohongshuContentPackageTests.cs` (one list fact each).

Must not change: any `src/Chronicleaf.*` backend file, any other Web feature,
any other change's files, `nuget.config`, migrations, envelopes.

## 2. Language and runtime
TypeScript + React (Vite) for the panels, bash + Playwright TypeScript for the
suite — the existing Web stack, no new dependency. Commands:
`npm --prefix src/Chronicleaf.Web ci`, `npx tsc --noEmit`,
`npm --prefix src/Chronicleaf.Web run build`, new suite via
`npm run test:e2e -- tests/content-pack-panels.spec.ts`, full set via
`bash scripts/e2e.sh`, names via `node scripts/check-openspec-change-names.mjs`,
`openspec validate --all --strict --no-interactive`.

## 3. Ownership and shared code
Project-local feature dir; no sibling/common extraction (decision table in
proposal). The api client is the only client of the three adapt/refusal
routes; approval/edit/download reuse the delivered review client's functions
by import, never by copy. Dependency direction: content-pack-panels →
markdown-review-export, content-asset-variants, grounded-markdown-drafts,
voice-profiles. No backend owns anything new.

## 4. User experience and interface
Actors: owner. Entry: project → milestone row → Content area → below the
variant panel. Per panel, top to bottom:

1. About line naming the no-send boundary in words (LinkedIn: copy-ready text
   carried by hand, native publish refused; Medium: manual import material,
   export-only, no upstream call; Xiaohongshu: human external publishing, no
   account login, no automatic posting, export needs live approval).
2. Source state: the milestone story's revision and live-approval state as the
   server reports it (honest empty: no story / never approved).
3. Picker over delivered routes: existing derived packs of this panel's type
   (from `GET /api/milestones/{id}/assets` filtered by asset type), plus the
   derive form (LinkedIn: CTA + hashtags; Xiaohongshu: title + tags + cover +
   CTA; pictures intentionally unselected — empty selection is contract-valid
   and keeps this change from re-implementing curation).
4. Inert preview: copy/body text as characters (LinkedIn/Xiaohongshu) or the
   story body plus manifest note (Medium); owned media ids; voice attribution
   (voice revision/fingerprint as read); revision + body hash.
5. Delivered review control: edit (PATCH, Xiaohongshu titles/body and LinkedIn
   body via the review edit), approve button (press-only), review status.
6. Download control, enabled only with a live approval; bytes handed to a file,
   never rendered as a document.
7. Publish-attempt control: POSTs the delivered refusal route, shows
   `publisher_unsupported` + server message verbatim. Labels say attempting
   sends nothing — the refusal is the answer.

States: loading / read-failure / empty (no story; story never approved; no
pack derived yet) / deriving / derive-failure verbatim / preview / approving /
approve-failure verbatim / export-failure verbatim / refusal verbatim. Nothing
fires on render except the reads that fill pickers; derive/publish-attempt/
download leave only from explicit controls. Archived milestones: no derive,
no approve, no publish-attempt — preview and download of already-approved
packs remain. Keyboard: native controls; focus stays on the acting control;
failures announce via `role=alert`.

## 5. Behavioral model

| Step | Route | Method | Idempotency / precondition |
|---|---|---|---|
| List packs | `/api/milestones/{id}/assets?kinds=packs` | GET | variant-only default preserved; unknown kinds → 422 |
| Read pack/story | `/api/assets/{id}` | GET | — |
| Read heads | evidence head, voice profile | GET | derive binds what was read |
| Derive (LI/XHS) | `/api/assets/{storyId}/adaptations/linkedin`, `.../xiaohongshu` | POST + antiforgery | body carries evidence+voice revisions; stale → 409 |
| Edit (LI/XHS) | `/api/assets/{id}` | PATCH + If-Match + antiforgery | expectedRevision must equal If-Match |
| Approve | `/api/assets/{id}/approve` | POST + antiforgery | contentRevision + evidenceRevision current |
| Unapprove | `/api/assets/{id}/unapprove` | POST + antiforgery | keeps row as history |
| Export | `/api/assets/{id}/export?format=linkedin\|medium\|xiaohongshu` | GET | live approval re-checked server-side |
| Publish-attempt | `/api/assets/{id}/adaptations/{linkedin\|medium\|xiaohongshu}/publish` | POST + antiforgery | always 422 `publisher_unsupported` (404 for foreign id) |

Deriving answers 201 with the new revision, not a job. Exports are synchronous
attachments. Medium has no adapt route: the panel selects an approved
story/variant and exports `format=medium` directly.

## 6. Contract and compatibility
Wire shapes follow the delivered endpoints exactly: adapt bodies
`{evidenceRevision, voiceRevision, callToAction?, hashtags?, pictures: []}`
(LinkedIn) and `{evidenceRevision, voiceRevision, title, tags?, coverCopy?,
callToAction?, pictures: []}` (Xiaohongshu); error envelope
`{code,message,traceId,fieldErrors?}` shown verbatim as `code: message`.
No new code, no envelope change, no migration. Backward compatible by
construction: additive UI over frozen routes.

## 7. Failure and boundary policy
- No story / story never approved → honest empty text, no derive control.
- Stale heads at derive → 409 shown verbatim; panel re-reads on retry.
- Edit without If-Match → 428; stale edit → 409; disagreeing body/header → 422.
- Export without live approval → `approval_required`; moved → `approval_stale`;
  missing bytes → `media_unavailable`. All verbatim, no download offered.
- Publish-attempt → 422 `publisher_unsupported` verbatim, no account action, no
  socket. Foreign id → delivered 404.
- No delete route exists for assets/revisions/approvals/exports (see §9): the
  UI offers none and says nothing that implies one.

## 8. Verification oracle
- `npm ci`, `tsc --noEmit`, `vite build` green.
- New suite `tests/content-pack-panels.spec.ts` green on its own disposable
  PostgreSQL against the published app: per pack — derive (LI/XHS) or select
  (Medium) → preview shows server text → approve → download bytes byte-equal
  to a direct server GET → publish-attempt answers 422 `publisher_unsupported`
  verbatim; empty states honest; armed counters prove no POST fired before its
  control and no request left for any linkedin/medium/xiaohongshu host.
- Full `bash scripts/e2e.sh` green (all suites incl. pre-existing).
- `git diff --check` clean; `openspec validate --all --strict --no-interactive`
  green; archive without `--skip-specs`; canonical spec promoted.

## 9. CRUD lifecycle matrix (mandatory)

| Entity | Create | Read | Update | Delete |
|---|---|---|---|---|
| Per-pack derivation (LI/XHS pack asset) | POST adapt (201, new revision) — in scope | GET asset + milestone asset list filtered by type — in scope | PATCH edit appends revision — in scope | No route serves asset deletion; UI offers none — **out of scope → named future package `content-asset-retirement`** |
| Per-pack derivation (Medium: no derived asset) | None by contract (uses approved story/variant as-is) — N/A by design, stated on screen | GET asset + export manifest — in scope | PATCH edit on the source asset via review routes — in scope | Same as above — `content-asset-retirement` |
| Pack revision | PATCH edit (new revision row, append-only by trigger) — in scope | GET asset/review (head revision, ETag) — in scope | PATCH edit (conditional on If-Match + body agreement) — in scope | Revisions immutable by trigger; no delete — by design, no future package (append-only is the contract) |
| Approval binding | POST approve (live row bound to revision+hash+evidence) — in scope | GET review (status + binding shown) — in scope | POST approve of a newer revision (supersedes) — in scope | POST unapprove (the explicit delete-equivalent; row kept as history) — in scope |
| Exported artifact | GET export (derived bytes, nothing stored) — in scope | Same GET re-checked per call — in scope | Re-export after re-approval — in scope | Bytes never stored server-side, nothing to delete — N/A by design, stated on screen |

Out-of-scope holes are named, never silent: `content-asset-retirement`
(asset deletion) and, already named upstream, `publication-history`
(attempt listing). Neither is built here.

## Decision ledger
- Pictures: derive with `pictures: []` (contract-valid; curation owns selection;
  re-implementing a media picker here would duplicate that package).
- Xiaohongshu title: required text input; candidates shown from the adapt
  response after derive; edit via review PATCH renames/re-bodies under the same
  revision rule as every other asset.
- Medium preview: story body in the inert renderer plus the export manifest
  note; the ZIP itself is never unzipped client-side (byte-equality is the
  assertion, proven by download comparison).
- Publish-attempt is a real POST to the real refusal route (not a disabled
  button): the contract forbids sending, and the honest proof is the server's
  own 422, not the absence of a control.
- No backend change anticipated: all routes verified present in
  `LinkedInCopyEndpoints`, `MediumImportEndpoints`, `XiaohongshuPackEndpoints`,
  `ContentReviewEndpoints` before authoring.
