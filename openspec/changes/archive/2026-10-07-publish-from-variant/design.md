# Design: Publish from variant (publish-from-variant)

## Context and implementation boundary
Repository Chronicleaf only. Inspect and change exactly:
`src/Chronicleaf.Web/src/features/publications/` (`PublicationPanel.tsx`,
`api.ts`, `types.ts`, plus one new `PublishForm.tsx` shared confirm
flow), `src/Chronicleaf.Web/src/features/content-asset-variants/`
(`ContentVariantPanel.tsx` plus one new `VariantPublishSection.tsx`),
`src/Chronicleaf.Web/src/features/project-areas/ProjectPublish.tsx`
(pass `projectId`), `src/Chronicleaf.Web/src/features/journey-publish/`
(`JourneyPanel.tsx` picker only),
`src/Chronicleaf.Api/Publishing/PublisherContractEndpoints.cs` (one new
GET beside the four delivered mappings),
`tests/Chronicleaf.Integration.Tests/PublisherContractTests.cs` (new
route tests in the same class), `src/Chronicleaf.Web/tests/` (one new
`publish-from-variant.spec.ts`), `scripts/e2e.sh` (one new isolated
suite + seed). Change nothing else: no migration, no envelope, no
approval binding, no evidence rule, no adapter, no vault, no journey
projection. Preserve forge.yaml, .standard receipts, unrelated files,
ROADMAP.md, .gitignore and `current_spec` (`publish-from-variant`).

## Language, ownership and release
- Web: React + TypeScript + Vite (`src/Chronicleaf.Web`); product-shell-ux
tokens and conventions kept; per-feature `api.ts` client convention
kept (the new `listTargets` lives in the publications client, the only
client of the publisher routes).
- API: C# .NET 10 minimal API in `Chronicleaf.Api`, same
`RequireOwnerSession()` + `OwnerScopeAsync` pattern as the four sibling
mappings in the same file.
- Tests: xUnit in `Chronicleaf.Integration.Tests` (same class and stage
as the contract tests, so `scripts/verify.sh` needs no gate change);
Playwright TypeScript in `src/Chronicleaf.Web/tests` via `scripts/e2e.sh`.

**Keep local** (per proposal reconnaissance): the pickers and the
confirm flow are Chronicleaf-product presentation; the target list
serves the delivered `IPublisherRegistry` verbatim. No shared `/lib`
added, no sibling edit authorized.

## Contract and compatibility decisions
New route `GET /api/publisher-targets`, private (`RequireOwnerSession()`),
no antiforgery read (GET), no Hosted project-grant check (classification
is global — same posture as `GET /api/ai/providers`):
- Response `{ "targets": [{ "targetId", "supportsDraft",
"supportsPublish", "supportsReconcile", "exportOnly" }] }` in registry
order. Serves `IPublisherRegistry.Targets` field for field; no
credential, no adapter, no content.
- Anonymous answers 401 through the delivered boundary; the shape never
carries a body, a picture or a credential, like every sibling route.
- In-scope adapter enablement: `GitHubPagesOptions` gains the explicit
parameterless constructor the configuration binder builds through (the
positional values stay the validated source; `CurrentTarget`
re-validates on every call, so a bound-but-invalid section is still
refused rather than served). The five sibling adapters keep their
defect → future package `publisher-options-binding`.
- All other contracts consumed byte-identical: preview/publish/receipt/
reconcile (`features/publications/api.ts`), review read
(`readReview` carries `approval.approvalId`), milestone list
(`listMilestones`), milestone draft read (`readDraft`, 200-with-null
when absent — the empty option, not a failure), variant list
(`listVariants`), journey preview/publish (`previewJourney`,
`publishJourney`).

Shared confirm flow (`PublishForm.tsx`, the one reuse point):
- Props: fixed `assetId`, fixed `approvalId` (prefilled; still shown,
never silently substituted — a moved approval is the server's
`approval_stale`, not a client retry), optional `initialTargetId`.
- Reads targets once on mount through `listTargets`; renders a target
`<select>` (`publish-target-picker`) with export-only entries disabled
and captioned as hand-carried; mode select kept (`draft`/`publish`).
- Preview button → `previewPublish` → preview facts shown (hash,
content revision, warnings). Publish requires a preview read in this
session, a non-empty idempotency key, and the separate confirm
checkbox — exactly the delivered `PublicationPanel` discipline, moved
not weakened. Refusals render the envelope `code: message` verbatim.
- 202 answers the accepted notice with the operation id and stores the
attempt id; identical resubmission answers 200 with the original
receipt and the caption says so. Receipt facts (state, target, mode,
error code, reconcile hint) render from the same receipt shape.
- Nothing fires on render except the target list read and (in hosts)
the reads their panels already do: preview and publish stay behind
explicit owner acts.

`PublicationPanel` keeps its read/reconcile section untouched (attempt
input stays hand-typed — no list route exists; future package
`publication-history`) and hosts `PublishForm` for the send half with
an asset picker above it: milestone `<select>`
(`publication-milestone-picker`, from `listMilestones`) then asset
`<select>` (`publication-asset-picker`: the milestone draft when
present plus `listVariants`, labelled by kind/type + revision). The old
`publication-asset-input` / `publication-target-input` testids are
removed with their inputs; `publication-preview`,
`publication-publish`, `publication-confirm`, `publication-receipt`,
`publication-error`, `publication-notice` keep their contract.

`VariantPublishSection` (new, beside `ContentVariantPanel`): rendered
only for the selected variant when `reviewStatus === 'approved'`;
hosts `PublishForm` with the variant's `assetId` and the live
`approval.approvalId`. Draft/invalidated variants show the honest
caption that publishing needs a live approval — no publish control.
Row entry: a `Publish` button per variant row
(`variant-publish-{type}`) selects that row and reveals the section;
the suite proves pick variant → pick target → preview → confirm →
receipt.

`JourneyPanel` keeps preview/publish/revoke untouched and replaces the
`journey-assets-input` text field with a milestone `<select>`
(`journey-milestone-picker`, from `listMilestones`) plus a checkbox
list of that milestone's pickable assets (`journey-asset-option`, value
= asset id: draft when present plus variants, labelled by kind/type +
revision). Checked ids in listed order are the selection the existing
preview/publish calls already send. Empty selection previews exactly
as today (the server refuses; the existing
`previewing an empty selection` suite keeps passing unmigrated).

## BFS impact map (every suite asserting a touched selector)
`publications.spec.ts` fills only `publication-attempt-input` and
clicks read/reconcile — untouched section, no migration. Its project
setup clicks `project-area-publish`; `ProjectPublish` keeps rendering
`PublicationPanel` first, so the suite passes unmigrated.
`journey-publish.spec.ts` clicks `journey-preview` with nothing
selected and asserts the refusal — empty checkbox selection sends the
same empty array, so it passes unmigrated. `content-asset-variants`
suite asserts derive/approve rows — the publish section renders below
the approved preview and touches no existing testid. The new
`publish-from-variant.spec.ts` runs as its own database-isolated suite
(port 5102, token `access-token-publish-variant`) because it seeds one
approved story plus a `publisher` credential and derives/approves/
publishes, none of which can be undone for a shared database.

## Behavioral model and invariants
| Input | Rule |
|---|---|
| Target list | Registry order, served verbatim; export-only disabled client-side, refused server-side. |
| Asset options | Draft (when the read returns one) + variants in list order; labelled, never bare GUIDs alone. |
| Preview | Authorizes nothing; hash shown; publish without one is the client-side `preview_required` guard, same as today. |
| Publish | Requires preview + idempotency key + confirm checkbox + live approval; 202 accepted (watch the operation) or 200 identical-resubmit (original receipt shown). |
| Receipt | Read from the server; state/target/mode/error shown; unknown stays unknown (reconcile is the delivered manual route, offered where the panel already offers it). |
| Nothing auto-fires | Mount reads (targets, milestones, draft, variants) are GETs only; the suite asserts no POST to preview/publish before the owner acts. |

## Failure, boundary and rollback policy
Every refusal renders the envelope `code: message` verbatim (unknown
target, export-only, unapproved, stale preview/approval, missing
credential, confirmation required, validation) — the suite asserts the
server and the panel say the same code. Empty option sets (no
milestones, no draft, no variants, no targets) render honest empty
captions, never a disabled lie. Rollback is removal: the change adds
one GET route and presentation-only components; deleting them restores
the hand-typed panels with no migration to unwind. No production string
names a change, spec, or "not in this change" state (bundle lint keeps
guarding). Captures/fixtures show empty/generic states plus seeded
content — no credential, token, PAT, Actor UUID, or session value.

## Verification oracle
- .NET: new `GET /api/publisher-targets` tests inside
`PublisherContractTests` (anonymous 401; owner 200 with the registry's
target ids, capability flags verbatim, export-only medium/linkedin
present with neither mode served; response carries no credential,
body or picture) — same `publisher contract tests` stage, asserted
≥1 test by the gate — plus one `PublisherGithubPagesAdapterTests`
regression test proving a configured section binds through production
registration (target listed with its capabilities, preview renders
CPU-only rather than 500ing).
- Web: `npm ci`, `tsc --noEmit` + `vite build` clean.
- Browser: new `publish-from-variant.spec.ts` green (seeded approved
story → derive → approve → pick variant → pick target → preview →
confirm → 202 + receipt with matching ids and a known state; refusal
cases verbatim: unconfirmed, unknown target, unapproved asset;
request-interception proof that no preview/publish POST fires before
the clicks; journey picker lists the seeded asset and its preview
computes a hash; publication pickers list the seeded milestone/asset
and the registry targets) PLUS full `bash scripts/e2e.sh` green.
- `git diff --check` clean; `openspec validate --all --strict
--no-interactive` green (never `--skip-specs`); archive without
`--skip-specs`; exactly two commits, no push.

## Decision ledger
- Target list as a new GET route (not a client hard-code): the registry
is owned by adapter packages that register at composition; a hard-coded
client vocabulary would drift from the server and re-create the audit's
dead-end. Deferred alternative (client enum) rejected for that reason.
- Shared `PublishForm` (not a deep-link to the Publish area): the
action starts on the variant row where the approval lives; one confirm
flow in one component keeps the discipline identical in both hosts.
- Attempt lookup stays hand-typed: no list route exists and inventing
history from the operations list would mislabel scope-wide jobs as the
owner's publishes → future package `publication-history`.
- Story assets beyond the per-milestone draft read have no list route
(the variant list covers derivatives only) → future package
`asset-inventory`.
- The identical options-binding defect in the five other publisher
adapters (devto, hashnode, ghost, wordpress, custom-blog: positional
options records with no binder-visible constructor, so a configured
section registers the target and then 500s on first use) → future
package `publisher-options-binding`. Only github-pages is repaired here,
because only it is this package's verification transport.
- e2e publish target is `github-pages` in `publish` mode with a seeded
fake `publisher` credential: preview is pure (no lease), the 202
acceptance is fully local, and the queued run's remote outcome is NOT
asserted (no network claim on this host) — only the receipt's stored
answer (matching ids, known state).
- Verification found two real defects, both fixed in-package with their
own tests rather than worked around. (1) A configured transport section
registers its target and then 500s every preview/publish inside the
options factory, because the positional options record offers no
constructor the configuration binder can build through: github-pages
gains the binder-visible constructor (PUBLISH-FROM-VARIANT-004); the
five siblings keep the defect and are reported as future package
`publisher-options-binding`. (2) The delivered confirm flow told 202
from 200 by `'operationId' in answered`, but both wire shapes carry
`operationId` — an identical resubmission read as a fresh acceptance.
The shared form discriminates on `created` (true only on the 202),
which repairs the misread for both hosts by construction.
