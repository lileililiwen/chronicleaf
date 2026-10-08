## 1. BFS — Baseline and impact coverage

- [x] 1.1 Mapped WEB-PERF-001..004 to `ScreenshotCapturePanel.tsx`
  (`CandidateView` `<img>`), `styles.css` (top guard block + five control
  selectors; zero existing rules touched), the root `<section>` of
  `MilestoneList` (new `milestone-list-panel` wrapper), `ProjectTimeline`,
  `OperationsPanel`, `CredentialVaultPanel`, `ClientsArea`,
  `GitHubCandidatePanel`, `GroundedMarkdownDraftPanel`,
  `PublicationPanel` (one `aria-busy` each), and new
  `tests/perf-animation.spec.ts` (standalone: dev server + route mocks +
  computed-style + built-CSS oracles). Baseline recorded: candidate `<img>`
  carries no dimension, loading or decoding attribute; zero `transition`
  declarations in `styles.css`; no list-owning root section carries
  `aria-busy` (only the `aria-live` loading paragraphs do, per
  `web-accessibility-core`); no `prefers-reduced-motion` rule anywhere.
  Frontend-only boundary confirmed: no migration/route/envelope/binding/
  evidence/payload change (candidate `width`/`height` already delivered).

## 2. DFS — Requirement-by-requirement implementation

- [x] 2.1 WEB-PERF-001: `CandidateView` `<img>` gains
  `width={candidate.width}` `height={candidate.height}`
  `loading="lazy"` `decoding="async"`; no other `<img>` touched.
- [x] 2.2 WEB-PERF-002: `.action`, `.nav__item`, `.filter`, `.badge`,
  `button.link` each gain
  `transition: color 120ms, background-color 120ms, border-color 120ms`;
  no new timing token (literal documented in design decision 2).
- [x] 2.3 WEB-PERF-003: `aria-busy={loading}` on all eight list-owning
  root sections (`MilestoneList` fragment wrapped in
  `<section data-testid="milestone-list-panel">`; the other seven on
  their existing root sections with their own flags); loading paragraphs
  untouched.
- [x] 2.4 WEB-PERF-004: reduced-motion guard block
  (`prefers-reduced-motion: reduce` collapsing `transition-duration` to
  `0.01ms !important` on `*`/`*::before`/`*::after`) at the top of
  `styles.css` with the package-naming comment.

## 3. BFS — Cross-surface regression and completeness

- [x] 3.1 Full pre-existing Playwright set green unmodified (full
  `verify.sh` deferred to the end of the batch per the batch plan): every
  `data-testid` preserved, no label/input `id` changed, no class or
  heading touched, `token-sweep.spec.ts` oracles still green over the
  touched CSS (time literals only), no new production dependency,
  `dist`/test-results artifacts never committed.

## 4. Verification

- [x] 4.1 Light checks only: `node
  scripts/check-openspec-change-names.mjs` exit 0, `openspec validate --all
  --strict --no-interactive` green, `npm ci` + `tsc --noEmit` exit 0 + `vite
  build` exit 0 in `src/Chronicleaf.Web`, `npx playwright test
  tests/perf-animation.spec.ts` green standalone (dev server +
  route-mocked API, no database); `git diff --check` clean; archive
  readiness with exactly two commits (implementation/tests/archive, then
  HANDOFF-only); full `CHRONICLEAF_E2E=1 bash scripts/verify.sh` deferred
  to the end of the batch per the batch plan.
