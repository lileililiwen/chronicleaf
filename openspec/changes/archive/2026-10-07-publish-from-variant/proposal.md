# Proposal: Publish from variant (publish-from-variant)

## Why
A UI-vs-spec audit found a P0 publishing dead-end. The product owns six
transport publisher adapters plus the reviewed-publisher contract, and
approved content variants exist — but the only publish UI
(`PublicationPanel`) requires hand-typing an asset id and a target id the
owner has no way to discover, and `JourneyPanel` requires hand-typed
asset ids. An approved variant therefore cannot be published by clicking:
the contract is reachable only by callers who already know its
identifiers. Status: Specified handoff; implementation not started.
Stage: POST-MVP (demand-gated publishing follow-up over delivered
adapters; depends only on archived packages).

## What Changes
1. A `Publish` action on each approved content-variant row
(`ContentVariantPanel`) that opens a target picker, then preview →
confirm → receipt, reusing the existing `PublicationPanel` confirm flow
and envelope verbatim behavior.
2. The hand-typed asset/target ids in `PublicationPanel` and the
hand-typed asset ids in `JourneyPanel` become pickers over delivered
list routes: milestones of the project, the milestone draft plus its
variants, and publisher targets.
3. One genuinely missing read is added: `GET /api/publisher-targets`
serving the registry's classified descriptors (target id, draft/publish
support, reconcile support, export-only flag). One genuine defect is
fixed: a configured GitHub Pages section registers the target but every
preview and publish against it 500s inside the options factory, because
the positional options record offers no constructor the configuration
binder can build through — the package's own browser proof needs one
working transport, so the binder-visible constructor lands here with its
own requirement and test. No other backend change: preview, publish,
receipt, reconcile, review, variant, draft, milestone
and journey routes stay byte-identical.
4. A migrated/new Playwright suite proving pick variant → pick target →
preview → confirm → receipt shows the server answer, refusals verbatim,
and nothing auto-fires.

## Package Boundary and Split Assessment
Single outcome: an owner can publish an approved variant by clicking,
through pickers, with the server's answer shown. Included: the variant
publish action, the two picker migrations, the one missing target-list
route with its own requirement and backend tests, and the browser suite.
Excluded: new transports, credential flows (the vault UI already stores
the `publisher` credential the contract leases), journey projection
changes, per-attempt publish history (no list route exists; the attempt
lookup stays hand-typed and is reported as a named future package, never
invented). Split signals considered: the target-list route could stand
alone, but alone it serves no owner task — it is accepted only with the
pickers that read it, so it ships in this package under its own
requirement. Depends on (all archived): publisher-contract (routes and
envelope), content-asset-variants (rows and list route),
markdown-review-export (approval binding), public-project-journey
(binding routes), project-area-ia (Publish area placement). This is the
smallest independently verifiable unit: the browser suite proves the
click path end to end.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Publisher target classification, the publish confirm
flow and the journey binding picker are Chronicleaf-product
presentation over Chronicleaf-owned routes; no matching recurring
implementation is evidenced in sibling platform contracts. Evidence
inspected: dotnet-platform-libs/README.md lists Platform.Jobs and
Platform.Storage families but no publisher registry, publish panel or
journey picker surface. The delivered registry (`IPublisherRegistry`)
is consumed, not duplicated: the new route serves exactly what each
descriptor names. No shared `/lib` added and no sibling edit
authorized.

| Candidate | Evidence path/symbol | Reusable code/config/architecture | Compatibility gap | Owner and release boundary | Decision |
|---|---|---|---|---|---|
| Platform.Jobs / Platform.Storage | dotnet-platform-libs/README.md | job dispatch, media store | no publisher registry, no publish UI, no target list | platform libs, versioned feed | keep local, consume delivered contracts only |
| Chronicleaf `IPublisherRegistry` | src/Chronicleaf.Application/Publishing/PublisherContracts.cs | `Targets`, `Find`, descriptors | read-only in-memory; no HTTP list route serves it | Chronicleaf publisher-contract, archived | adopt: new route serves the registry verbatim |

## BFS Impact Map
- Actor/flow: the signed-in single-user owner (community-access session
gate stays the entry guard); anonymous and Hosted permission behavior
unchanged — the new list route carries `RequireOwnerSession()` like its
four siblings, with no project grant check because classification is
global, exactly like `GET /api/ai/providers`.
- Boundary/callers: `PublicationPanel.tsx` (asset/target inputs become
pickers; read/reconcile untouched), `ProjectPublish.tsx` (passes
`projectId`), `ContentVariantPanel.tsx` (publish section on approved
rows), `JourneyPanel.tsx` (asset-ids input becomes milestone + asset
pickers; preview/publish/revoke untouched),
`features/publications/api.ts` (one `listTargets` client added beside
the four delivered callers), `PublisherContractEndpoints.cs` (one
`MapGet("/api/publisher-targets")` beside the four delivered routes),
`PublisherContractTests.cs` (new-route tests in the same class, same
verify.sh stage — no gate change), `scripts/e2e.sh` (new
`publish-from-variant.spec.ts` as a new database-isolated suite; the
publications and journey-publish suites keep passing unmigrated where
possible, migrated in-package where named in design §BFS).
- Data/integration: no migration, no envelope change, no approval
binding change, no evidence rule change; the target list serves
in-memory registry order with no persistence.
- CRUD lifecycle (mandatory map, filled before implementing):
  - Publication attempt/receipt — Create `POST /api/assets/{id}/publish`
  (UI: confirm control); Read `GET /api/publications/{attemptId}` (UI:
  Read status); Update `POST .../reconcile` (UI: Reconcile; forward
  terminal resolution only, never a rewrite); Delete: none by design
  (rows are never deleted; terminal states are immutable). Hole: no
  per-attempt list route exists, so the attempt lookup stays hand-typed
  → named future package `publication-history`, reported not dropped.
  - Publisher target — Create/Update/Delete: none by design (registry
  is deployment-time DI classification, not rows); Read: NEW
  `GET /api/publisher-targets` (UI: all three pickers). Export-only
  targets (medium, linkedin) are listed with `exportOnly: true` and
  shown disabled with their hand-carry caption; the refusal stays
  server-side (`publisher_target_export_only`).
  - Journey asset binding — Create `PUT .../journey` with null revision
  (UI: Publish); Read `GET .../journey` plus the anonymous projection
  (UI: state + public link); Update `PUT .../journey` republish (UI:
  Publish with revision); Delete: revoke via `PUT enabled: false`, no
  row delete (UI: Revoke). Asset ids inside the binding are picked over
  the milestone draft read plus the variant list (both delivered); a
  milestone's non-draft story assets beyond the single draft read have
  no list route → named future package `asset-inventory`, reported not
  dropped.
  - Publisher options binding (defect found by verification, fixed
  in-scope for one adapter): `GitHubPagesOptions` is a positional record
  with no binder-visible constructor, so `Configure` builds it through
  `Activator.CreateInstance` and fails — a configured section registers
  the target, then every preview/publish against it 500s. The other five
  adapters (devto, hashnode, ghost, wordpress, custom-blog) carry the
  identical shape → named future package `publisher-options-binding`,
  reported not dropped.
- Verification: `npm ci`, `tsc` + `vite build`, the new browser suite
PLUS the full pre-existing browser set (`bash scripts/e2e.sh` green),
`git diff --check`, `openspec validate --all --strict --no-interactive`
(never `--skip-specs`), archive without `--skip-specs`.
- Privacy/compatibility: fixtures show empty/generic states plus one
seeded approved story and one derived variant — no credential, token,
PAT, or signed-in session value in any committed file, fixture, or
capture. The seeded `publisher` credential value is a fake test key,
never a real secret.

## Capabilities
### New Capabilities
- `publish-from-variant`: Publish an approved content variant by
clicking — target picker over the delivered classification, preview →
explicit confirm → server receipt, with refusals verbatim and nothing
auto-firing.

## Non-goals
New transports or target registrations; credential create/edit flows;
journey projection or approval semantics; per-attempt publish history
(no route; future package `publication-history`); a global asset
inventory beyond draft-plus-variants per milestone (future package
`asset-inventory`); the identical options-binding defect in the other
five publisher adapters (future package `publisher-options-binding`);
auto-posting or auto-publishing anywhere; any
envelope, migration, or evidence-rule change.
