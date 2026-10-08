# Design: Web accessibility roles (web-accessibility-roles)

## Context and implementation boundary
Repository Chronicleaf only. Primary boundary
`src/Chronicleaf.Web/src/App.tsx` (one explicit `isActive` branch for
`/settings/credentials`), `src/Chronicleaf.Web/src/styles.css` (`min-block-size: 2.5rem` on `.action`
and `.filter`, `button.link` minimum plus padding), every panel
listed in tasks 1.1 (heading tag edits only),
`src/Chronicleaf.Web/src/features/milestone-timeline/TimelinePage.tsx`
(picker `role="group"` + label),
`src/Chronicleaf.Web/src/features/voice-profiles/VoiceArea.tsx`
(`aria-controls` + panel `id`),
`src/Chronicleaf.Web/tests/a11y-roles.spec.ts` (new, own e2e group as
suite 29 in `scripts/e2e.sh` with its port, log and token vars).
Depends
on no backend package and changes none: every API contract annotated
here is delivered and stays byte-identical. Preserve forge.yaml,
.standard receipts, unrelated files, HANDOFF.md (until the second
HANDOFF-only commit), ROADMAP.md, .gitignore, every existing
`data-testid`, every existing class name, and current_spec (set to
`web-accessibility-roles`; HANDOFF.md already points there).

## Language, ownership and release
React + TypeScript + Vite for `src/Chronicleaf.Web`; no C#, no
migration, no API edit. The roles annotations live in the Web
project; heading tags, selection attributes, badge prefixes and touch
minimums are shipped as in-tree JSX/CSS augmentations, never as a
third-party component library and never as a runtime import from a
CDN. No package install in this change (no new dependency of any
kind).

**Keep local.** Roles annotations are Chronicleaf-product
presentation; no matching recurring implementation is evidenced in
sibling platform contracts. Evidence inspected:
dotnet-platform-libs/README.md lists Platform.Jobs and
Platform.Storage families but no Web shell or a11y scaffold. No
shared `/lib` added and no sibling edit authorized.

## Contract and compatibility decisions
Consume the delivered API contracts exactly as served (owner session,
projects/milestones, evidence, capture, generation, publication):
same paths, payloads, envelope `{code,message,traceId,fieldErrors?}`,
antiforgery/session handling, and revision/If-Match discipline. No new
API route, no envelope change, no approval-binding change, no
evidence-rule change. The five gaps the package closes are:

- **Sequential headings.** One `h1` per view; every other heading is
  retagged so each view reads `h1→h2→h3` with no skips (a heading may
  step down any amount; it may never step up more than one). Tag
  changes only: every `className` stays verbatim, so no pixel moves
  and no suite hook moves (`data-testid`s are on the same elements).
  Concretely: milestone-row titles `h3→h2` with their `h4`
  subsections →`h3`; `ProjectTimeline` heading `h3→h2` with day
  headings `h4→h3`; row detail panels (`MilestoneEvidencePanel`,
  `GitHubSyncPanel`, `LocalGitSyncPanel`, `ScreenshotPicker`,
  `GenerationPreviewPanel`, `GroundedMarkdownDraftPanel`,
  `MarkdownReviewPanel`, `ScreenshotCapturePanel`) `h4→h3` with
  their `h5`s →`h4`; project-level panels rendered directly under a
  view `h1` (`GitHubConnectionPanel`, `LocalRepositoryPanel`,
  `GitHubCandidatePanel`, `PublicationPanel`, `JourneyPanel`,
  `ExportPanel`, `EvolutionImagePanel`, `EvolutionGifPanel`)
  `h3/h4→h2` with their `h4` children →`h3`; `TeamPanel` and
  `TenancyPanel` section `h4`s →`h2`; `VoiceArea` drafts `h3→h2`
  with draft view `h4→h3`, and `VoiceProfilePanel`/`VoiceCheckPanel`
  `h3→h2`. Panels already nested correctly (`NarrativeMemoryPanel`
  `h2+h3`, `WeeklyReportPanel` `h2+h3`, `ClientDetail` `h1+h2`,
  `SettingsPage` `h1+h2`, `ProjectAutomations` `h2` sections,
  `ContentVariantPanel` `h3+h4`, `VideoStoryPanel` `h3+h4`,
  `PublicJourneyView` `h2+h3`, dashboard/projects/operations/clients/
  credentials views) are untouched.
- **Selection semantics.** Top nav and `ProjectSubNav` keep
  `aria-current="page"` (unchanged). Filter chips keep
  `role="group"` + `aria-pressed` (`MilestoneRows`, `OperationsPanel`
  unchanged); the `TimelinePage` picker gains the missing
  `role="group"` + `aria-label="Choose a project"` on its wrapper
  (a `div`, keeping its class and `data-testid`). The voice toggle
  gains `aria-controls="voice-panels"` and the controlled panel div
  gains the matching `id` (keeping its `data-testid`), making it a
  proper disclosure: `aria-expanded` already toggles, now it names
  what it toggles.
- **Credentials active nav.** One explicit `isActive` branch:
  entry `/settings/credentials` is active exactly on that path and
  its real sub-paths. No new top-level nav entry; the Settings entry
  keeps its exact/team/tenancy rule, so unknown settings sub-routes
  highlight nothing rather than the nearest parent.
- **Non-color status.** Every `badge--ok` / `badge--pending` /
  `badge--muted` render gains a data-status-derived `Status: `
  accessible name (`aria-label`) beside the text already present
  (milestone rows, timeline entries, credential rows, archived
  markers, curation primary, evidence availability). The visible
  word is untouched, so exact-text assertions keep passing
  unmodified; a screen reader announces the named status instead of
  a bare word, and sighted rendering is unchanged.
- **Touch targets.** `.action` and `.filter` gain
  `min-block-size: 2.5rem`; `button.link` gains
  `min-block-size: 2.5rem` with comfortable padding
  (`padding: 0.375rem 0.25rem`). Nav rows (`.nav__item`) and cards
  are not retouched; filter/action rows wrap as before, one row
  taller where the floor binds.

Existing Playwright suites keep passing unmodified: every
`data-testid` and every class survives, the per-page single-`h1`
assertion in `product-shell-ux.spec.ts` still finds exactly one
`h1`, and the nav `aria-current` assertions still find their target.
The new `tests/a11y-roles.spec.ts` suite proves the five gaps by DOM
attribute (heading order per view, `aria-pressed` on chips,
`aria-expanded` + `aria-controls` on the voice toggle, active nav on
`/settings/credentials`, `Status: ` accessible name on every modifier badge) and
by bounding-box measurement (`≥40px` on sampled
`.action`/`.filter`/`button.link`).

## Behavioral model and invariants
- **One `h1` per view, no skipped levels.** A screen reader walking
  the heading tree of any view hears `h1` once, then sections at
  `h2`, then subsections at `h3`, with no level announced twice in
  a jump. Stepping back up any amount is conformant; stepping up
  more than one is the failure this package removes.
- **Selection is announced the same way everywhere.** A nav entry
  names the current page (`aria-current`); a chip names its pressed
  state inside a labelled group (`aria-pressed` in `role="group"`);
  a disclosure names what it opens (`aria-expanded` +
  `aria-controls`). Three patterns, no fourth.
- **The credentials page is a named nav state.** Deep-linking
  `/settings/credentials` highlights Credentials exactly as clicking
  the nav does; an unknown `/settings/*` path highlights nothing
  rather than guessing.
- **Status never speaks through color alone.** Every color-coded
  badge carries its status word in text (as today) plus a
  non-color accessible name (the `Status: ` label) a screen reader
  announces and a text-mode browser exposes in place.
- **Touch targets meet the floor.** Every `.action`, `.filter` and
  `button.link` control measures at least 40px on its block axis;
  nav rows and card rows keep their layout, one row taller where the
  floor binds.

## Failure, boundary and rollback policy
A removed testid breaks its suite loudly (every testid is preserved);
a skipped heading level fails the new suite (the per-view order
assertion is structural); a missing group role or `aria-controls`
fails the new suite (attribute assertions); a sub-40px control fails
the new suite (bounding-box measurement). Unknown client-side path
keeps the delivered "Nothing here" view, now with no nav highlight
under `/settings/*`. Rollback is frontend-only: revert the Web tree
and the e2e.sh file-list line; no data, migration, API surface,
credential, antiforgery value or testid is touched, so stored
evidence, approvals, credentials and operations are unaffected.
Partial retagging (a panel left skipping a level) fails the order
assertion, not silently accepted. The `aria-label` marker is
attribute-only: removing it changes no text node and no layout.

## Verification oracle
Existing suites: every Playwright suite green unmodified, every
`data-testid` preserved, every `product-shell-ux.spec.ts` and
`e2e/shell.spec.ts` assertion still finds its target. New
`tests/a11y-roles.spec.ts` (suite 29, own installation):
heading order per view with exactly one `h1` and no skipped levels
(dashboard, projects list, project detail areas over a created
project with a confirmed milestone, timeline page with a chosen
project, settings, credentials, operations, clients, review panel in
its empty state); `aria-pressed` on every filter chip group;
`aria-expanded` + `aria-controls` on the voice toggle resolving to
the visible panel; `aria-current="page"` on Credentials for a
deep-linked `/settings/credentials` and no highlight on an unknown
settings sub-route; a non-color marker in every modifier badge on
the covered pages; bounding-box height `≥40px` on sampled
`.action`/`.filter`/`button.link`. Conformance probes: keyboard Tab
from the skip link reaches the voice toggle and the picker chips;
a no-CSS/text-mode read of a badge still names its status.

## Decision ledger and activation gates
Phase: POST-MVP. DEMAND satisfied by the 2026-10-08 owner audit order
— no further activation needed. No unresolved product-policy decision
in this scoped contract. Third-party versions are lockfile
implementation inputs, not an invitation to redesign contracts.
Missing browsers/DB at implementation time is recorded prerequisite
unavailability, never fabricated success.

### Owner-decided roles decisions (owner-decided via the coordinator, 2026-10-08)
1. **Heading model.** KEEP one `h1` per view with sections at `h2`
   and subsections at `h3`. No new section titles are added to fix
   a skip: stray headings are retagged, so the visible copy is
   byte-identical and only the tag moves.
2. **Visual freeze.** KEEP every `className` verbatim through the
   retag. Styling is class-driven, so a tag-only edit moves no
   pixel; the touch-target floor is the only intentional visual
   change in the package.
3. **Shared-panel depth rule.** KEEP the shallowest-parent rule: a
   panel rendered directly under a view `h1` promotes to `h2`
   (`GitHubConnectionPanel`, `LocalRepositoryPanel`,
   `GitHubCandidatePanel`, publish/evolution panels, team/tenancy
   sections); a panel rendered inside a milestone row nests at `h3`
   under the promoted `h2` row title.
4. **Selection vocabulary.** KEEP exactly three patterns —
   `aria-current="page"` for nav, `role="group"` + `aria-pressed`
   for chips, `aria-expanded` + `aria-controls` for the disclosure
   — and add no fourth (no `aria-selected`, no tablist).
5. **Credentials nav.** KEEP credentials as a settings sub-route
   with its own top-nav entry and an explicit `isActive` branch.
   No new top-level nav entry; unknown settings sub-routes
   highlight nothing.
6. **Status marker.** KEEP the data-status-derived `Status: `
   accessible name (`aria-label`) beside the text already present.
   No hidden span is added — a hidden prefix would join the badge's
   text content and break the exact-text assertions four existing
   suites hold on these badges — and no shape/character marker is
   added to the visible badge, so sighted rendering and every
   `toHaveText` keep passing unmodified while a screen reader
   announces the named status.
7. **Touch floor.** KEEP `min-block-size: 2.5rem` (40px) on
   `.action`, `.filter` and `button.link`, with comfortable padding
   on `button.link` only. Nav items and cards are not retouched.
8. **Suite placement.** KEEP the new suite in its own e2e group
   with its own disposable database (suite 29, after wayfinding).
   Playwright sorts spec files alphabetically, so a suite-1
   placement would run these record-creating tests before the
   installation-emptiness suites — the first full-gate run proved
   exactly that. Its own installation keeps every emptiness claim
   honest, for the same reason suites 2 to 28 each have one.
9. **No axe expansion.** KEEP the axe-core smoke exactly where
   `web-accessibility-core` left it. Every assertion in this
   package is structural DOM or bounding-box; no new scan is added
   and no new dependency is installed.

## Requirement traceability
| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| WEB-A11Y-ROLES-001 headings | One h1 per view, h1→h2→h3, tags only | Heading-model + depth-rule + visual-freeze decisions above | Panels in tasks 1.1, classes/testids preserved | Success / Failure / Boundary | 1.1, 2.1, 3.1, 4.1 | Browser: per-view heading order, one h1, no level skip |
| WEB-A11Y-ROLES-002 selection | aria-current kept, chips grouped, disclosure named | Selection-vocabulary decisions above | TimelinePage picker, VoiceArea toggle, navs unchanged | Success / Failure / Boundary | 1.1, 2.2, 3.1, 4.1 | Browser: aria-pressed in role=group; aria-expanded + aria-controls resolving |
| WEB-A11Y-ROLES-003 credentials nav | Explicit isActive rule, no new entry | Credentials-nav decisions above | App.tsx isActive branch | Success / Failure / Boundary | 1.1, 2.3, 3.1, 4.1 | Browser: deep-linked /settings/credentials highlights Credentials; unknown sub-route highlights nothing |
| WEB-A11Y-ROLES-004 non-color status | Hidden Status prefix in every modifier badge | Status-marker decisions above | 9 badge sites with aria-label | Success / Failure / Boundary | 1.1, 2.4, 3.1, 4.1 | Browser: every badge--ok/pending/muted carries the Status: name; exact-text assertions unmodified |
| WEB-A11Y-ROLES-005 touch targets | 40px floor on action/filter/link | Touch-floor decisions above | styles.css minimums | Success / Failure / Boundary | 1.1, 2.5, 3.1, 4.1 | Browser: boundingBox height ≥40px on sampled controls; nav/card rows intact |
