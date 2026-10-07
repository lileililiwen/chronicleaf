# Proposal: Publisher adapter panels (publisher-adapter-panels)

## Why
Six publisher transports are delivered and archived (`publisher-github-pages-adapter`,
`publisher-devto-adapter`, `publisher-hashnode-adapter`, `publisher-ghost-adapter`,
`publisher-wordpress-adapter`, `publisher-custom-blog-adapter`), and the
`publish-from-variant` flow (`eb58a43`) lets an owner pick a target, preview,
confirm and read the receipt through one shared form over the five delivered
publisher routes. But the form is target-blind: every target gets the same
labels, the same draft+publish mode box even where the registry serves only
one mode, and the export-only entries (`medium`, `linkedin`) are disabled
options that can never surface the server's own refusal. An owner aiming at
Ghost cannot see that the send needs an origin, a version and tags; an owner
aiming at Medium cannot be told in the server's own words that nothing leaves
from here. Status: specified handoff; implementation not started. Stage:
follow-up over delivered routes; depends only on archived packages.

A known defect blocks every adapter surface except one: `publish-from-variant`
(PUBLISH-FROM-VARIANT-004 boundary) reports that the five non-Pages adapters
still carry the identical options-binding 500 the Pages repair fixed — a
positional options record with no binder-visible constructor, so a configured
section registers the target and then every preview and publish against it
500s inside the options factory. Only `github-pages` was repaired. No picker
can route around a transport that 500s.

## What Changes
1. Repair the identical binding defect in the five remaining options records
   (`DevToOptions`, `HashnodeOptions`, `GhostOptions`, `WordPressOptions`,
   `CustomBlogOptions`): one binder-visible parameterless constructor each,
   mirroring the archived `GitHubPagesOptions` repair, with no validation
   change — bound-but-invalid values are still refused per call through
   `Create`, never served.
2. One `Configured_section_registers_target_and_serves_preview` regression
   test per repaired adapter suite, through the production registration path
   (demand gate plus `Configure`), exactly mirroring the archived Pages test —
   no new verify stage needed, the five existing `publisher *-adapter tests`
   stages select them.
3. A per-adapter target surface behind the target picker in the one shared
   `PublishForm` (both `publication-` and `variant-publish-` namespaces):
   for the picked target, its classification served field-for-field from
   `GET /api/publisher-targets` (draft/publish support, reconcile support,
   export-only), adapter-specific guidance naming what the target sends and
   needs, a mode box offering only the modes the registry serves for that
   target, the preview's adapter target line shown as the bound configuration
   the confirm would authorize, and the shared confirm → receipt unchanged.
4. Export-only targets (`medium`, `linkedin`) become pickable instead of
   disabled: picking one shows the hand-carry guidance, and the preview
   attempt surfaces the server's `publisher_target_export_only` (422)
   verbatim — never a fake send, never a client-side invention of the refusal.
5. A new Playwright suite (`tests/publisher-adapter-panels.spec.ts`, wired as
   its own database-isolated block in `scripts/e2e.sh` with all six transports
   configured and the dispatch loop silenced) proving per adapter: pick target
   → adapter surface visible → preview names the bound configuration →
   confirm → 202 → receipt names the server's answer; both export-only
   refusals verbatim; nothing auto-fires.
6. No new API route, no envelope change, no credential flow change. Backend
   edits are the five constructors (each covered by its own spec requirement
   and regression test) plus test-host `StartConfigured` hooks mirroring the
   archived Pages host.

## Package Boundary and Split Assessment
Single outcome: an owner can publish to each of the six delivered transports
by clicking through adapter-aware surfaces, and a configured transport never
500s on binding. Included: five constructors, five regression tests, the
adapter surface component, export-only enablement with verbatim refusal, the
browser suite with its seed and e2e.sh block. Excluded: new transports,
credential flows, journey projection changes, attempt history (no list route
serves past attempts; the attempt lookup stays hand-typed — reported as named
future package `publication-history`, already named by the publish-from-variant
boundary, never invented), per-adapter credential configuration UI (operator
config file/env owns target values; the UI reads them only through the
preview's target line — never a second source of truth). Depends on (all
archived): publisher-contract, all six publisher adapters, publish-from-variant
(picker/receipt conventions, target route), content-asset-variants and
markdown-review-export (approval binding), project-area-ia (Publish area
placement). Split signals considered (defect fix vs UI vs suite): one owner,
one lifecycle, one acceptance oracle (the browser suite fails without the
constructors, since five of six previews would 500), so separating them would
create an unusable intermediate state. This is the smallest independently
verifiable unit: the browser suite proves the click path end to end.

## Sibling and Shared Architecture Reconnaissance

| Candidate | Evidence path/symbol | Reusable code/config/architecture | Compatibility gap | Owner and release boundary | Decision |
|---|---|---|---|---|---|
| Shared publish consent flow | `src/Chronicleaf.Web/src/features/publications/PublishForm.tsx` | Target picker over `GET /api/publisher-targets`, preview → confirm → receipt, verbatim refusals, `namespace` testid scoping | Target-blind: same copy and mode box for all targets | Chronicleaf Web/publications, same release | **adopt**: host the adapter surface inside `PublishForm`, reuse the flow untouched |
| Pages binding repair | `src/Chronicleaf.Publishers/GitHubPages/GitHubPagesOptions.cs` (parameterless ctor), `PublisherGithubPagesApiHost.StartConfigured` + `Configured_section_registers_target_and_serves_preview` | Exact repair shape and exact regression-test shape | Five records and five hosts lack it | Chronicleaf Publishers + Integration.Tests, same release | **adopt**: mirror constructor and host hook per adapter |
| Static-blog export UI suite wiring | `scripts/e2e.sh` suite 23, `tests/static-blog-export-ui.spec.ts` | Database-isolated suite block, seed-then-run pattern, `CHRONICLEAF_*_BASE_URL` ports | New suite needs all six transports configured + silenced dispatch | scripts/e2e.sh, same release | **adopt**: suite 24 follows the suite 22/23 block shape |
| Platform libs / manager projects | `/home/paul/code/dotnet-platform-libs` (Platform.* 0.1.0 via `packages/platform-feed`) | None relevant: no shared publisher-panel or options-binder capability exists there | N/A | External versioned feed, not edited | **keep local**: no reusable capability; no sibling edit |

No `common`/`manager` project in this repository owns publisher UI or options
binding; the change stays project-local by evidence above.

## User Experience and Interface Impact
Actors: the single-user Community owner publishing an approved asset. Entry
points: unchanged — the Publish project area (`PublicationPanel`) and the
approved variant row's publish section (`VariantPublishSection`); both render
the one shared `PublishForm`. Primary flow: pick target → the adapter surface
appears under the picker naming the transport, what it carries (text-only vs
HTML, draft/publish, reconcile support) and what configuration the preview
bound → preview → confirm → receipt, all through the delivered routes. The
mode box now lists only registry-supported modes for the picked target
(`github-pages` offers `publish` only). Alternate flow: pick `medium` or
`linkedin` → hand-carry guidance appears, preview is refused by the server and
the panel shows `publisher_target_export_only` verbatim with no send control
offered past it. States: loading targets, empty registry, per-adapter
guidance, preview-bound configuration, unconfirmed/missing-key validation,
verbatim server refusal, accepted-operation notice, receipt, identical-resubmit
receipt — all existing, only the adapter block is new. Responsive/keyboard/
contrast behavior is the delivered form's; the adapter block is static text
plus the existing controls, keyboard-operable in the existing tab order with
no focus theft. Browser evidence: the new Playwright suite.

## BFS Impact Map
- Capabilities: `publish-from-variant` (picker/receipt conventions reused, not
  redefined), six `publisher-*-adapter` specs (behavior unchanged; binding
  repaired), `publisher-contract` (routes/envelope untouched).
- Users/flows: owner publish flows in two hosts gain one adapter block;
  existing selectors keep their testids (`{namespace}-*`); export-only options
  become selectable (no suite asserts disabled — verified by grep).
- Contracts/data/persistence: none — no route, envelope, table or migration.
- Integrations/configuration: none at runtime — the five constructors only let
  the existing `Configure<T>` binding build the existing records; e2e.sh gains
  one suite block with six transport sections plus silenced dispatch.
- Callers: `PublishForm` (extended), `PublicationPanel`/`VariantPublishSection`
  (untouched, they host the form). No other caller of the options records
  changes shape (positional construction sites keep working).
- Failure/boundary: configured-but-unreachable transports still refuse at the
  run, not at preview; present-but-invalid sections still fail startup;
  absent sections still answer `publisher_unknown_target`; silenced dispatch
  keeps receipts at acceptance in the browser suite (the .NET suites own the
  run half).
- Tests: five .NET regression tests (existing stages), one Playwright suite
  (new e2e.sh block), full `bash scripts/e2e.sh` regression afterwards.
- Compatibility/privacy/security: no credential, body or picture reaches the
  new UI beyond what the delivered preview already serves (the target line);
  no secret in any assertion.

## Capabilities
- PUBLISHER-ADAPTER-PANELS-001 — Configured sections bind through production registration (defect repair + regression tests).
- PUBLISHER-ADAPTER-PANELS-002 — Per-adapter target surfaces behind the target picker.
- PUBLISHER-ADAPTER-PANELS-003 — Mode selection honors the registry classification; export-only refusals surface verbatim.

## Non-goals
- New transports, LinkedIn native send, Medium native API (both refused/handed
  per archived contract; surfaced, never sent).
- Attempt history listing (`publication-history` stays a named future package).
- Operator target editing in the UI (config file/env owns values).
- Journey, export-ZIP, credential, or team surfaces.
