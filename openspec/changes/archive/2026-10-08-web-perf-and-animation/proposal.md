# Proposal: Web performance and animation (web-perf-and-animation)

## Why

Sixty-two changes are implemented, verified and archived, and the Web client
has a shell, tokens, responsive rules, an accessibility core and form
feedback — but it still shifts under the owner's eyes and moves with no
manners. A live UI audit (2026-10-08, against the real client on `main`)
shows four concrete gaps, all frontend-only: candidate screenshots render
with no width or height, so every candidate list that populates reflows the
page (cumulative layout shift on a path the owner watches while a job
finishes); interactive controls (`.action`, `.nav__item`, `.filter`,
`.badge`, `button.link`) snap between states with no transition, so hover,
active and focus changes read as flicker; list-owning panels announce
loading only through the `aria-live` paragraph while their container section
says nothing, so assistive technology meets the list with no busy context;
and there is no `prefers-reduced-motion` guard anywhere, so owners who ask
the OS for less motion get the full amount. This package closes the four
gaps without changing any backend contract, dependency, route, envelope,
approval binding, evidence rule, or validation limit. Status: Specified
handoff; implementation not started. Stage: POST-MVP (new package; DEMAND
satisfied by 2026-10-08 owner audit order). No drainable package remains
ahead of it (HANDOFF records `web-form-feedback` as the most recent change
and `current_spec` is `web-perf-and-animation`).

## What Changes

Add one performance-and-animation layer to the Web client: explicit
`width` + `height` on the `ScreenshotCapturePanel` candidate `<img>` (taken
from `candidate.width`/`candidate.height` the server already returns) plus
`loading="lazy"` and `decoding="async"`, so candidate lists populate without
layout shift; one shared hover/focus transition
(`transition: color 120ms, background-color 120ms, border-color 120ms`) on
`.action`, `.nav__item`, `.filter`, `.badge` and `button.link`, tokens
untouched and no new timing token; `aria-busy={loading}` on the root section
of every panel that owns a list (milestone list, timeline, operations,
credentials, clients, candidates, drafts, publications) while its list is
loading, following the `web-accessibility-core` `aria-live` discipline
already in place; and one `prefers-reduced-motion` guard at the top of
`styles.css` collapsing all transitions for owners who request reduced
motion. One new standalone Playwright suite
`tests/perf-animation.spec.ts` (real React rendering through the Vite dev
server with route-mocked API responses for the image and container
assertions, computed-style probes for the transitions, built-CSS grep for
the reduced-motion guard — no server, no database) proves every
requirement. No new production dependency, no C#, no migration, no route,
no envelope. See design.md.

## Package Boundary and Split Assessment

Own only the Web client performance-and-animation layer: the candidate
image attributes, the shared transition declarations, the container
`aria-busy` wiring, and the reduced-motion guard. Repository Chronicleaf,
React + TypeScript + Vite, no C# surface. Depends on no new package: every
backend contract this UI annotates is already delivered and stays
byte-identical, and the candidate dimensions the image names
(`candidate.width`/`candidate.height`) already arrive in the delivered
screenshot-candidate payload and are only surfaced, never changed. If a
backend change proves unavoidable during implementation, it is named as a
separate future package and this one stays frontend-only. The two deferred
owner-BLOCKED packages (`semantic-narrative-retrieval`,
`hosted-managed-ai`) are unchanged and are not a hidden sub-feature. This
unit is accepted by its scoped browser evidence and outcome, not by
completion of the full product. Excluded surfaces follow design and
non-goals.

## Sibling and Shared Architecture Reconnaissance

**Keep local.** Image dimension attributes, control transitions,
container busy state and reduced-motion guards are Chronicleaf-product
presentation; no matching recurring implementation is evidenced in sibling
platform contracts. Evidence inspected: dotnet-platform-libs/README.md lists
Platform.Jobs and Platform.Storage families but no Web performance,
transition, or motion-preference system. Reuse dependency-owned API
contracts (projects, milestones, screenshots, operations, vault, clients,
candidates, drafts, publications) exactly as served; never duplicate those
concerns. No shared `/lib` added and no sibling edit authorized.

## BFS Impact Map

- Actor/flow: the signed-in single-user owner (community-access session gate
  stays the entry guard). Anonymous and journey readers meet no candidate
  list and no transitioning control beyond the public journey chrome, so no
  anonymous surface changes.
- Boundary/callers: `src/Chronicleaf.Web/src/styles.css` (reduced-motion
  guard at the top; transition declarations on the five control selectors;
  zero existing rules touched otherwise),
  `ScreenshotCapturePanel.tsx` (`CandidateView` image attributes only),
  the eight list-owning panels (one `aria-busy` attribute on each root
  section; no testid, label, class or heading change):
  `MilestoneRows.tsx` (`MilestoneList`), `ProjectTimeline.tsx`,
  `OperationsPanel.tsx`, `CredentialVaultPanel.tsx`, `ClientsArea.tsx`,
  `GitHubCandidatePanel.tsx`, `GroundedMarkdownDraftPanel.tsx`,
  `PublicationPanel.tsx`, and
  `src/Chronicleaf.Web/tests/perf-animation.spec.ts` (new standalone
  suite). No backend code; no C# file; no migration; no API contract change.
- Data/integration: no schema, no API, no envelope change; all API calls keep
  their existing paths, payloads and antiforgery/session handling; every
  existing `data-testid` is preserved so the 30+ existing browser suites keep
  their hooks (no new testid is needed — the suite asserts existing
  `capture-candidate-image`, list container testids, and computed styles).
- Verification: existing Playwright suites must keep passing unmodified; new
  `tests/perf-animation.spec.ts` proves the four gaps behaviorally against
  the real components (Vite dev server plus route-mocked API, no database)
  with computed-style and built-CSS oracles; `web-token-sweep`'s source
  oracles keep passing because every changed CSS declaration references
  tokens or time literals only — no raw sizing, no hex, no bare line-height
  or letter-spacing (verified in the light checks by `tsc`, `vite build`
  and the new suite, with full `verify.sh` deferred to the end of the batch
  per the batch plan).
- Privacy/compatibility: every assertion is on empty/generic states or
  structurally-attribute checks with fixture values only — no credential,
  token, PAT, Actor UUID, or signed-in session value in any committed file,
  fixture, or capture.

## Capabilities

### New Capabilities

- `web-perf-and-animation`: Four closed performance-and-animation gaps on the
  Web client (sized lazy candidate images, one shared 120ms control
  transition, container `aria-busy` on every list-owning panel,
  `prefers-reduced-motion` guard) plus one standalone Playwright suite that
  proves all four.

## Non-goals

Backend changes of any kind (migrations, routes, envelope, approval
bindings, evidence rules, candidate payload — the `width`/`height` fields
already exist server-side and are only surfaced); new image formats, CDN
delivery, or responsive `srcset` (explicit dimensions plus lazy decoding
only); new transition kinds beyond color/background/border (no transform or
opacity choreography); per-panel motion or skeleton loaders (the
`aria-live` + `aria-busy` discipline already in place is extended, not
replaced); restyling any panel beyond the transition declarations; new
timing tokens (120ms stays a literal by owner decision 2); touching
HANDOFF.md, ROADMAP.md, or .gitignore (the HANDOFF pointer is a separate
commit per the standing rule); new production dependency; no new remote, no
push, no publication. No implementation, archive, commit, push or deployment
in this authoring work.
