# Proposal: Web token sweep (web-token-sweep)

## Why
Sixty changes are implemented, verified and archived, and
`product-shell-ux` gave the Web client one token set
(`src/Chronicleaf.Web/src/tokens.css`) — but `styles.css` still
carries hundreds of raw literals beside the tokens: raw `rem`
spacing and sizes, raw `line-height` values, and raw
`letter-spacing` values that duplicate what tokens already name.
Every raw literal is a second source of truth a later edit can
drift from, and the residue is large: a source audit at authoring
time counts 200+ raw sizing literals, 15 raw `line-height`
literals and 13 raw `letter-spacing` literals in `styles.css`.
This package moves every one of them into tokens with
byte-identical rendered output — a pure CSS sweep with zero
visual change and zero behavior change. Status: Specified
handoff; implementation not started. Stage: POST-MVP (new
package; DEMAND satisfied by the batch drain order, next after
`web-responsive-shell`).

## What Changes
Four frontend-only moves inside `src/Chronicleaf.Web/src/`, all
in `styles.css` with token additions in `tokens.css` only: every
raw `rem`/`px` size becomes the matching `--space-*` /
`--font-size-*` reference (new tokens added first where no token
fits, each documented in design.md); every raw color, spacing or
type literal already covered by a token becomes the token
reference (the source already shows zero hex literals outside
`tokens.css`; this requirement pins that invariant in a test);
every raw `line-height` becomes `var(--line-height-body)` or
`var(--line-height-tight)`; two tracking tokens
(`--tracking-tight: -0.01em`, `--tracking-tighter: -0.02em`) are
added and every raw `letter-spacing` becomes a tracking
reference. No `.tsx` edit, no testid change, no API, route,
envelope, migration, or dependency change. One new Playwright
suite `tests/token-sweep.spec.ts` proves the sweep statically
(grep oracles) and visually (computed-style spot checks against
pre-change values captured at the start of the run). See
design.md.

## Package Boundary and Split Assessment
Own only the two CSS modules and the new suite: `styles.css`
(references), `tokens.css` (additions), and
`tests/token-sweep.spec.ts` (new, standalone — no server, no
database, not wired into `scripts/e2e.sh`). Repository
Chronicleaf, React + TypeScript + Vite, no C# surface. Depends
on no new package: every value moved here is already delivered
and stays computed-identical. If a replacement proves
non-identical during implementation, the replacement is reworked
until identical — values are never "close enough". Different
visual, layout or workflow changes are separate packages in
../../../ROADMAP.md, not hidden sub-features. This unit is
accepted by its grep oracles plus computed-style evidence, not
by completion of the full product. Excluded surfaces follow
design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Design tokens are Chronicleaf-product
presentation; no matching recurring implementation is evidenced
in sibling platform contracts. Evidence inspected:
dotnet-platform-libs/README.md lists Platform.Jobs and
Platform.Storage families but no Web shell, design-token system
or token-sweep discipline. Reuse dependency-owned API contracts
exactly as served; never duplicate those concerns. No shared
`/lib` added and no sibling edit authorized.

## BFS Impact Map
- Actor/flow: none — no actor-visible change by design. The
  signed-in owner and the anonymous reader see byte-identical
  rendering before and after; the invariant is proven, not
  asserted.
- Boundary/callers: `src/Chronicleaf.Web/src/styles.css` (every
  raw literal becomes a token reference),
  `src/Chronicleaf.Web/src/tokens.css` (39 documented
  additions, zero value changes to existing tokens),
  `src/Chronicleaf.Web/tests/token-sweep.spec.ts` (new
  standalone suite: static grep oracles plus computed-style
  spot checks via `page.setContent`, no baseURL, no token, no
  database). No `.tsx` file; every `data-testid`, class name,
  media query and z-index stays literal and verbatim.
- Data/integration: no schema, no API, no envelope change; no
  API call, payload, antiforgery handling or revision discipline
  is touched — CSS only.
- Verification: `node scripts/check-openspec-change-names.mjs`
  exit 0; `openspec validate --all --strict --no-interactive`
  green; `npm ci`, `tsc --noEmit`, `vite build` green; the new
  suite green standalone; the pre-existing suites are untouched
  and unweakened (selectors, testids and classes survive
  verbatim, so no migration is needed or performed).
- Privacy/compatibility: the suite renders synthetic elements in
  `page.setContent` only — no credential, token, PAT, Actor
  UUID, or signed-in session value in any committed file,
  fixture, or capture.

## Capabilities
### New Capabilities
- `web-token-sweep`: Every raw size, color, line-height and
  tracking literal in the Web client resolved into design
  tokens with byte-identical rendered output, guarded by grep
  oracles and computed-style spot checks.

## Non-goals
Any computed-value change (values move, never change); any
`.tsx` edit (testids included — avoided entirely); new tokens
beyond the documented additions; visual restyle, palette, type
or spacing changes of any kind; new layout, breakpoint,
z-index or media-query semantics (breakpoints and z-index stay
literal); touching HANDOFF.md, ROADMAP.md, or .gitignore (the
HANDOFF pointer is a separate commit per the standing rule);
new dependency of any kind; no new remote, no push, no
publication. No implementation, archive, commit, push or
deployment in this authoring work.
