# Design: Product shell and experience (product-shell-ux)

## Context and implementation boundary
Repository Chronicleaf only. Primary boundary `src/Chronicleaf.Web/src/App.tsx`
(shell + nav), `src/Chronicleaf.Web/src/styles.css` plus one new tokens module,
all 17 folders under `src/Chronicleaf.Web/src/features/` as style consumers,
`SessionGate` (reduced to sign-in/out), `ProjectArea` route map (client-side
paths reused; `/settings` and `/timeline` added as client-side views over
existing APIs only), `scripts/verify.sh` (one new lint stage), and the
Playwright suites plus new DELTA captures. Inspect the explicit symbols below;
create only modules needed for this outcome. Depends on no backend package and
changes none: every API contract rendered here is delivered and stays
byte-identical. Preserve forge.yaml, .standard receipts, unrelated files,
HANDOFF.md, ROADMAP.md, .gitignore and current_spec (none active).

## Language, ownership and release
React + TypeScript + Vite for `src/Chronicleaf.Web`; no C#, no migration, no
API edit. Component and token contracts live in the Web project; feature
panels consume tokens and never define local colors, type ramps, or spacing
outside them. Native dependencies pinned in lockfiles during implementation;
no package install in this authoring task.

**Keep local.** Shell, navigation and tokens are Chronicleaf-product
presentation; no matching recurring implementation is evidenced in sibling
platform contracts. Evidence inspected: dotnet-platform-libs/README.md lists
Platform.Jobs and Platform.Storage families but no Web shell or token system.
No shared `/lib` added and no sibling edit authorized.

## Contract and compatibility decisions
Consume the delivered API contracts exactly as served (owner session,
projects/milestones, operations, credentials, timeline, drafts, review):
same paths, payloads, envelope `{code,message,traceId,fieldErrors?}`,
antiforgery/session handling, and revision/If-Match discipline. No new API
route, no envelope change, no approval-binding change, no evidence-rule
change. Client-side paths are reused; two client-side views are added over
existing APIs only:
- `/settings` — session internals (actor label without raw UUID dump, idle
  timeout, lifetime, sign-out) moved out of the global panel; links on to
  `/settings/credentials`. `SessionGate` keeps the sign-in/out form only.
- `/timeline` — project picker over the existing project list plus the
  existing `ProjectTimeline` for the chosen project (today it renders only
  inside `ProjectDetail`; the component is reused, not forked).
Navigation order follows the core journey: Projects → Timeline → Operations →
Credentials → Settings. Brand mark in the nav links to `/`; per-page headers
(`h1` per view) replace the repeated global `Chronicleaf` heading. The
`later-packages` / `setup-pending` block ("Not in this change", "Project
setup coming in a later change.") is deleted from production UI along with
every other spec/design-boundary string (scope §3 names the full list; the
lint stage below makes the deletion permanent).
Existing Playwright suites keep passing unmodified wherever selectors survive;
where this package renames a selector or testid (known: `e2e/shell.spec.ts`
asserts the global brand heading and `setup-pending` text, both removed by
design), the suite is updated inside this package's own commit — never
silently, and the spec scenarios name the migration. No suite is deleted to
make the gate pass.

## Behavioral model and invariants
- One shell, one nav, one header per page. The nav (with active-state) and the
  page header render from the current client-side path; no page re-renders a
  second global header and no page reaches another product area only through a
  back-link.
- Tokens are the only visual source of truth: type scale, spacing scale,
  color roles (background/foreground/muted/accent/border at minimum, both
  color schemes the current CSS already supports), and button/input/card
  primitives. All 17 feature panels restyle onto them; no new ad-hoc styles,
  no inline one-off colors or spacing. Label/input collision (seen in the
  credentials capture: "Store a token" colliding with its input) is resolved
  by the shared input primitive, not per-panel fixes.
- The core journey reads as one path: nav order plus per-view next-step
  affordances (project → milestone → evidence → draft → approve → export)
  with empty states that name the next step instead of scaffolding text.
- Scaffolding purge is total: no committed production string names a change,
  spec, design boundary, or "not in this change" state. The lint stage fails
  the gate if a forbidden pattern returns.
- Screenshot acceptance gates the look: every UI requirement is proven by
  Playwright screenshots taken in empty/generic states, compared against the
  archived 2026-10-07 set (01-home, 02-project-new, 03-operations,
  04-credentials in `/tmp/opencode/review-shots/`), with differences
  justified in HANDOFF evidence at implementation time. DOM assertions stay
  for behavior; screenshots gate appearance.

## Failure, boundary and rollback policy
A removed testid breaks its suite loudly (suites are updated in-package, never
silenced); a forbidden scaffolding string fails the lint stage before any
test runs. Unknown client-side path keeps the delivered "Nothing here" view,
now inside the shell with nav intact. Screenshots contain empty/generic states
only: any capture showing a credential, token, PAT, Actor UUID, or signed-in
session value is rejected and retaken. Rollback is frontend-only: revert the
Web tree and the lint stage; no data, migration, or API surface is touched,
so stored evidence, approvals, credentials and operations are unaffected.
Partial restyle (a panel left on ad-hoc styles) fails PRODUCT-SHELL-UX-002
boundary, not silently accepted. If implementation proves a backend change
unavoidable, it is named as a separate future package and this one stays
frontend-only.

## Verification oracle
Existing suites: full Playwright set green (unmodified, or updated in-package
for renamed selectors with the migration named per scenario). New DELTA
captures, one per nav entry minimum (Projects/dashboard, project-new,
Timeline, Operations, Credentials, Settings) in empty/generic states, each
diffed against the closest 2026-10-07 archived capture with differences
justified; lint stage green on a tree containing a planted forbidden string
(proven to fail) and on the clean tree (proven to pass). Conformance probes:
navigate every nav entry from every other entry without a back-link; assert
no Actor UUID, idle-timeout, or lifetime text outside `/settings`; assert no
forbidden string anywhere in the served production bundle.

## Decision ledger and activation gates
Phase: POST-MVP. DEMAND satisfied by explicit owner order after the 2026-10-07
visual review — no further activation needed. No unresolved product-policy
decision in this scoped contract; taste/direction items the author refused to
guess are returned as open questions, not decided here. Third-party versions
are lockfile implementation inputs, not an invitation to redesign contracts.
Missing browsers/DB at implementation time is recorded prerequisite
unavailability, never fabricated success.

### Owner-decided taste decisions (owner-decided via the coordinator, 2026-10-07)
1. Visual direction: KEEP the near-black/gold theme and systematize it into
   tokens. No rebrand, no light default, no new accent. Delicate means
   systematic, spaced, and consistent — not different.
2. Timeline: top-level `/timeline` as a project-picker reusing the existing
   `ProjectTimeline` component, per the authored spec.
3. Settings: session internals + credentials link ONLY. Provider catalogue
   and voice preset summaries stay where they are.
4. The `later-packages`/`setup-pending` block is CONFIRMED dead: delete it.
   If and only if implementation proves it load-bearing for the release
   story, keep the minimum and report exactly what and why.
   (Implementation verdict: not load-bearing — nothing imports the removed
   exports except the deleted block itself; the release story is untouched.)

## Requirement traceability
| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| PRODUCT-SHELL-UX-001 | Shell, nav, per-page headers, session to Settings, journey order | Contract/client-path decisions above | App.tsx + nav + ProjectArea + SessionGate | PRODUCT-SHELL-UX-001 Success/Failure/Boundary | 1.1, 2.1, 3.1, 4.1 | Browser: nav-to-nav without back-links; internals only under /settings; shell.spec migration named |
| PRODUCT-SHELL-UX-002 | Tokens applied across 17 panels, no ad-hoc styles | Token-source-of-truth decisions above | styles.css + tokens module + 17 feature folders | PRODUCT-SHELL-UX-002 Success/Failure/Boundary | 1.1, 2.2, 3.1, 4.1 | Browser + source audit: shared primitives only; label/input collision gone; no one-off styles |
| PRODUCT-SHELL-UX-003 | Scaffolding purge with lint gate | Total-purge + lint decisions above | Web tree + verify.sh lint stage | PRODUCT-SHELL-UX-003 Success/Failure/Boundary | 1.2, 2.3, 3.1, 4.1 | Lint fails on planted string, passes clean; bundle grep clean |
| PRODUCT-SHELL-UX-004 | DELTA-SCREENSHOTS acceptance | Screenshot-gates-look decisions above | Playwright suites + new captures | PRODUCT-SHELL-UX-004 Success/Failure/Boundary | 1.2, 2.4, 3.1, 4.1 | DELTA captures vs 2026-10-07 set, diffs justified; no secret-bearing capture |
