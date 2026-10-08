# Design: Web performance and animation (web-perf-and-animation)

## Context and implementation boundary

Repository Chronicleaf only. Primary boundary
`src/Chronicleaf.Web/src/styles.css` (one reduced-motion guard block at the
top; one `transition` declaration on each of the five control selectors;
zero existing rules touched otherwise),
`src/Chronicleaf.Web/src/features/screenshot-capture/ScreenshotCapturePanel.tsx`
(`CandidateView` `<img>` attributes only), the root `<section>` of each of
the eight list-owning panels (`MilestoneRows.tsx` `MilestoneList`,
`ProjectTimeline.tsx`, `OperationsPanel.tsx`,
`CredentialVaultPanel.tsx`, `ClientsArea.tsx`,
`GitHubCandidatePanel.tsx`, `GroundedMarkdownDraftPanel.tsx`,
`PublicationPanel.tsx` — one `aria-busy` attribute each), and
`src/Chronicleaf.Web/tests/perf-animation.spec.ts` (new standalone suite
that starts the Vite dev server and route-mocks `/api/*`). Depends on no
backend package and changes none: the candidate `width`/`height` the image
surfaces already arrive in the delivered screenshot-candidate payload.
Preserve forge.yaml, .standard receipts, unrelated files, HANDOFF.md (until
the second HANDOFF-only commit), ROADMAP.md, .gitignore, every existing
`data-testid`, and `current_spec` (already `web-perf-and-animation`).

## Language, ownership and release

React 19 + TypeScript + Vite for `src/Chronicleaf.Web`; no C#, no migration,
no API edit. The animation elements live in the Web project as in-tree
JSX/CSS augmentations over the delivered token set (`tokens.css` roles and
scales via `styles.css` aliases); no new production dependency, no component
library, no CDN import. No package install in this authoring task (`vite`
and `@playwright/test` are already devDependencies and the new suite uses
only those).

**Keep local.** Image dimension attributes, control transitions, container
busy state and reduced-motion guards are Chronicleaf-product presentation;
no matching recurring implementation is evidenced in sibling platform
contracts. Evidence inspected: dotnet-platform-libs/README.md lists
Platform.Jobs and Platform.Storage families but no Web performance or
motion-preference system. No shared `/lib` added and no sibling edit
authorized.

## Contract and compatibility decisions

Consume the delivered API contracts exactly as served (projects/milestones,
screenshots, operations, vault, clients, candidates, drafts, publications):
same paths, payloads, envelope `{code,message,traceId,fieldErrors?}`,
antiforgery/session handling, and revision/If-Match discipline. No new API
route, no envelope change, no approval-binding change, no evidence-rule
change. The four gaps the package closes are:

- **Sized lazy images.** `CandidateView` renders
  `<img … width={candidate.width} height={candidate.height}
  loading="lazy" decoding="async">`. The numbers are the payload's own
  `width`/`height` (already rendered as text beside the image), so the
  box the browser reserves is the box the picture fills — no layout shift
  when the bytes land. `loading="lazy"` keeps below-the-fold candidates
  off the critical path; `decoding="async"` keeps image decode off the
  main thread. No other `<img>` in the tree changes (journey pictures and
  curation previews are out of scope per non-goals; their dimensions are
  not payload-delivered).
- **One shared transition.** `.action`, `.nav__item`, `.filter`, `.badge`
  and `button.link` each gain
  `transition: color 120ms, background-color 120ms, border-color 120ms`.
  Exactly the three color-adjacent properties — the controls in this tree
  change only color, background and border on hover/active/focus, so a
  transition on anything wider would animate nothing and cost style
  recalculation. `outline` (the `:focus-visible` ring) is deliberately
  excluded: a focus indicator must appear instantly to be honest about
  where focus is.
- **Container busy state.** Each list-owning root section gains
  `aria-busy={loading}` with the panel's own loading flag (`MilestoneList`
  wraps its fragment in a `<section data-testid="milestone-list-panel">`
  carrying `aria-busy={milestones.loading}`; `ProjectTimeline`
  `aria-busy={loading}`; `OperationsPanel` `aria-busy={loading}`;
  `CredentialVaultPanel` `aria-busy={credentials.loading}`;
  `ClientsArea` `aria-busy={loading}`; `GitHubCandidatePanel`
  `aria-busy={state.loading}`; `GroundedMarkdownDraftPanel`
  `aria-busy={drafts.reading}`; `PublicationPanel`
  `aria-busy={milestones === null}`). The `web-accessibility-core`
  `aria-live="polite"` + `aria-busy="true"` loading paragraphs stay
  exactly as they are — this adds the container side, never renames the
  announcement side.
- **Reduced-motion guard.** At the very top of `styles.css`, with a comment
  naming this package:
  `@media (prefers-reduced-motion: reduce) { *, *::before, *::after {
  transition-duration: 0.01ms !important; } }`. Duration is collapsed
  rather than `transition: none` so the end state still applies (no
  control is left mid-hover) while the motion is effectively removed; the
  `0.01ms` floor (not `0ms`) keeps shipped WebKit behavior honest about
  firing `transitionend` where any future listener depends on it.

Existing Playwright suites keep passing unmodified: every `data-testid`
survives, no label/input `id` changes, no class added or removed except the
one `milestones__panel` wrapper section (new testid, additive) — and no
heading touched. The `web-token-sweep` source oracles keep passing because
no changed declaration introduces raw sizing, hex, or bare line-height /
letter-spacing (`120ms`/`0.01ms` are time literals the sizing oracle does
not match; the guard block carries no other declaration).

## Behavioral model and invariants

- **The box precedes the bytes.** A candidate image always carries explicit
  dimensions from the payload before its bytes arrive; a candidate whose
  picture cannot be read still shows as a named candidate with no image
  (the delivered fallback), now with the reserved box intact.
- **One transition, five selectors, three properties.** Every interactive
  control in the tree animates the same three properties over the same
  interval; a sixth transitioned selector or a seventh property is
  non-conformant scope creep, not polish.
- **Busy is derived, never stored twice.** Each container reads the same
  flag that gates its loading paragraph; a container reporting busy while
  its list renders (or idle while its loading paragraph shows) is
  non-conformant.
- **Reduced motion removes motion, never meaning.** The guard collapses
  duration only — colors, borders and focus rings still reach their end
  states, and the `aria-busy`/`aria-live` announcements are untouched.

## Failure, boundary and rollback policy

A removed testid breaks its suite loudly (every testid is preserved; the
one new wrapper testid is additive). An unmocked API route in the
standalone suite fails the test rather than hanging (route-mocked fetch
rejects fast; the suite registers a catch-all 500 envelope for calls a
test does not name). Unknown client-side path keeps the delivered "Nothing
here" view. Rollback is frontend-only: revert the Web tree; no data,
migration, API surface, credential, antiforgery value or testid is
touched, so stored evidence, approvals, credentials and operations are
unaffected. Partial wiring (a list container without `aria-busy`, an image
without dimensions) fails the new suite, not silently accepted. A `srcset`,
a skeleton loader, a transform choreography, or a timing token is out of
scope and fails review, not scope-creep accepted. If implementation proves
a backend change unavoidable, it is named as a separate future package and
this one stays frontend-only.

## Verification oracle

Existing suites: every Playwright suite green unmodified (full `verify.sh`
deferred to the end of the batch per the batch plan; the light checks run
`tsc`, `vite build` and the new suite only). New
`tests/perf-animation.spec.ts` (standalone: Vite dev server started in
`beforeAll`, `/api/*` route-mocked, no database): the candidate image
carries numeric `width`/`height` equal to the mocked payload plus
`loading="lazy"` and `decoding="async"`; computed `transition-duration`
is `0.12s` on sampled `.action`/`.nav__item`/`.filter` controls and the
source declares the 120ms shorthand on all five selectors; list containers
report `aria-busy="true"` while their fetches are held pending and
`aria-busy="false"` once settled; every built `dist/assets/*.css` file
contains the `prefers-reduced-motion` guard. Conformance probes:
`tsc --noEmit` exit 0, `vite build` exit 0, `token-sweep.spec.ts` still
green (its oracles cover the touched CSS), and the new suite green.

## Decision ledger and activation gates

Phase: POST-MVP. DEMAND satisfied by the 2026-10-08 owner audit order — no
further activation needed. No unresolved product-policy decision in this
scoped contract. Third-party versions are lockfile implementation inputs, not
an invitation to redesign contracts. Missing browsers at implementation time
is recorded prerequisite unavailability, never fabricated success.

### Owner-decided performance decisions (owner-decided via the coordinator, 2026-10-08)

1. **Dimensions from the payload, not the bytes.** The image takes
   `candidate.width`/`candidate.height` the server already returns; no
   client-side probing, no aspect-ratio box, no second source of truth.
2. **120ms stays a literal, not a token.** Timing is not color, spacing or
   type: the token set owns visual roles, and a `--transition-fast` token
   with exactly one value would be ceremony. The design documents the
   literal so a future second timing earns the token.
3. **Color, background-color, border-color — nothing wider.** The tree's
   controls change only these three on state change; `outline` is excluded
   so the focus ring stays instant.
4. **Container side only.** The `aria-live` paragraphs from
   `web-accessibility-core` are not renamed or moved; this package adds
   `aria-busy` to the owning section and nothing else.
5. **MilestoneList gains a wrapper section.** It renders a fragment today,
   so the container signal needs a container: one `<section>` with an
   additive testid, no class or heading change, no other DOM moved.
6. **Collapse duration, don't remove the transition.** The guard keeps the
   end state applying (no stuck mid-hover control) while removing the
   motion; `0.01ms` rather than `0ms` for shipped `transitionend`
   behavior.
7. **Candidate images only.** Journey pictures and curation previews keep
   their markup: their dimensions are not payload-delivered and inventing
   them would be the fabrication this package exists to remove.
8. **Standalone suite with route-mocked API.** The new suite starts the Vite
   dev server and mocks `/api/*` per test — real components, real
   rendering, no database — with computed-style and built-CSS oracles for
   what the DOM cannot show.
9. **Light checks only; full gate deferred.** `tsc`, `vite build` and the
   new suite prove this package; full `CHRONICLEAF_E2E=1 bash
   scripts/verify.sh` runs once at the end of the batch by the
   coordinator, not here.

## Requirement traceability

| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| WEB-PERF-001 sized lazy images | Payload dimensions + lazy/async on candidate `<img>` | Sized-lazy-image decisions above | `ScreenshotCapturePanel.tsx` `CandidateView` | Success / Failure / Boundary | 1.1, 2.1, 3.1, 4.1 | Mocked browser: width/height equal payload, loading lazy, decoding async |
| WEB-PERF-002 shared transition | One 120ms color transition on five selectors | Shared-transition decisions above | `styles.css` control selectors | Success / Failure / Boundary | 1.1, 2.2, 3.1, 4.1 | Computed 0.12s on sampled controls; 120ms shorthand on all five |
| WEB-PERF-003 container busy | `aria-busy={loading}` on eight list roots | Container-busy decisions above | Eight panel root sections | Success / Failure / Boundary | 1.1, 2.3, 3.1, 4.1 | Mocked browser: true while pending, false once settled |
| WEB-PERF-004 reduced-motion guard | Duration-collapsing guard at top of styles.css | Reduced-motion decisions above | `styles.css` top block | Success / Failure / Boundary | 1.1, 2.4, 3.1, 4.1 | Built CSS contains the guard; source comment names the package |
