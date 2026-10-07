# Proposal: Product shell and experience (product-shell-ux)

## Why
Fifty-two changes are implemented, verified and archived, and every requirement
§1–§11 maps to delivered code — except user experience. No package ever
specified the app shell, navigation, design language, or visual acceptance. The
Web client is 30 tsx files across 17 feature folders with ad-hoc styles, and a
live visual review (2026-10-07, captures in `/tmp/opencode/review-shots/`)
shows what that costs: no navigation (every page re-renders the header plus the
Owner-session panel and movement is via back-links), session internals (Actor
UUID, idle timeout, lifetime) displayed on every page, the operations page
footer rendering the literal text "NOT IN THIS CHANGE" (spec-authoring
scaffolding leaking into production), form labels colliding with inputs, one
narrow centered column wasting wide viewports, and no visual hierarchy. The
product works but does not read as one product, and the core journey
(requirement §9: connect repo → milestone → evidence → screenshot → draft →
review → export) has no visible path. This package gives Chronicleaf one
coherent face without changing any backend contract. Status: Specified
handoff; implementation not started. Stage: POST-MVP (new package; DEMAND
satisfied by explicit owner order after visual review).

## What Changes
Introduce one app shell with top-level navigation (Projects, Timeline,
Operations, Credentials, Settings), per-page headers replacing the repeated
global header, session internals relocated to Settings, one design-token set
(type scale, spacing, color roles, button/input/card primitives) applied across
all 17 feature panels with no new ad-hoc styles, a visible core-journey order
(project → milestone → evidence → draft → approve → export), a purge of all
spec/design-boundary text from production UI guarded by a lint stage, and
screenshot acceptance (Playwright captures compared against the archived set —
a DELTA-SCREENSHOTS discipline) gating the look while DOM assertions keep
gating behavior. Frontend only: no migrations, no API routes, no envelope, no
approval binding, no evidence rule changes. See design.md.

## Package Boundary and Split Assessment
Own only the Web shell, navigation, tokens, scaffolding purge and screenshot
acceptance; repository Chronicleaf, React + TypeScript + Vite, no C# surface.
Depends on nothing new: every backend contract this UI renders is already
delivered and stays byte-identical. If a backend change proves unavoidable
during implementation, it is named as a separate future package and this one
stays frontend-only. Different persistence/approval/publishing/Cloud lifecycles
are separate packages in ../../../ROADMAP.md, not hidden sub-features. This
unit is accepted by its scoped browser evidence and outcome, not by completion
of the full product. Excluded surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Shell layout, navigation, and design tokens are
Chronicleaf-product presentation; no matching recurring implementation is
evidenced in sibling platform contracts. Evidence inspected:
dotnet-platform-libs/README.md lists Platform.Jobs and Platform.Storage
families but no Web shell, navigation, or design-token system. Reuse
dependency-owned API contracts (access/projects/operations/vault) exactly as
served; never duplicate those concerns. No shared `/lib` added and no sibling
edit authorized.

## BFS Impact Map
- Actor/flow: the signed-in single-user owner (community-access session gate
  stays the entry guard); anonymous sees sign-in only, as today.
- Boundary/callers: `src/Chronicleaf.Web/src/App.tsx` (shell, nav, headers),
  `src/Chronicleaf.Web/src/styles.css` plus one tokens module, all 17 folders
  under `src/Chronicleaf.Web/src/features/` as style consumers only,
  `ProjectArea` route map (client-side paths reused, none added except
  `/settings` and `/timeline` as client-side views over existing APIs),
  `SessionGate` (sign-in/out form only; internals move out),
  `scripts/verify.sh` (new lint stage), Playwright suites under
  `src/Chronicleaf.Web/` (selectors/testids preserved, or migrated with suite
  updates inside this package's own commit — notably `e2e/shell.spec.ts`,
  which asserts the current global brand heading and `setup-pending` text).
- Data/integration: no schema, no API, no envelope change; all API calls keep
  their existing paths, payloads and antiforgery/session handling.
- Verification: existing Playwright suites must keep passing (unmodified, or
  updated in-package for renamed selectors); new DELTA screenshots per
  requirement PRODUCT-SHELL-UX-004 prove the look against the 2026-10-07 set.
- Privacy/compatibility: screenshots and fixtures show empty/generic states
  only — no credential, token, PAT, or signed-in session value in any
  committed file, fixture, or capture. Session internals no longer render on
  every page, which also removes the Actor UUID from routine captures.

## Capabilities
### New Capabilities
- `product-shell-ux`: One coherent app shell, navigation, design language,
  scaffolding-free UI, and screenshot-gated visual acceptance.

## Non-goals
Backend changes of any kind (migrations, routes, envelope, approval bindings,
evidence rules); new product workflows or routes beyond the two client-side
views (`/settings`, `/timeline`) over existing APIs; rebranding, marketing
copy, or taste beyond the token system (open taste questions go to the owner,
listed in the return); TipTap or any editor rebuild; mobile-viewport or
print-specific design; touching HANDOFF.md, ROADMAP.md, or .gitignore
(coordinator owns them). No implementation, archive, commit, push or
deployment in this authoring work.
