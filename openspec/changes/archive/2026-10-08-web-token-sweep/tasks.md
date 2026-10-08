## 1. BFS — Baseline and impact coverage
- [x] 1.1 Mapped WEB-TOK-001..004 + all 12 scenarios to styles.css (references only), tokens.css (39 additions, zero value changes), tests/token-sweep.spec.ts (new, standalone via page.setContent — no server, no database, no e2e.sh wiring). Baseline recorded: 200+ raw sizing literals, 15 raw line-height literals, 13 raw letter-spacing literals, zero hex outside tokens.css; computed-style ledger captured at 16px root (body 16px, card padding 16px, action padding 8px 15px, nav gap 6px, page heading 28px, shell padding 32px, plus 40+ further probes).

## 2. DFS — Requirement-by-requirement implementation
- [x] 2.1 WEB-TOK-001: every raw rem/px size in styles.css replaced with the matching --space-* / --font-size-* reference (existing tokens where values match, ledger additions where none fit); exclusions stay literal (unitless 0, 100%, z-index, media-query px breakpoints, 1fr); comments reworded off numeric literals.
- [x] 2.2 WEB-TOK-002: every raw color/spacing/type literal already covered by a token replaced; zero hex outside tokens.css pinned (source already clean; built-CSS hex set asserted equal to the tokens.css hex set).
- [x] 2.3 WEB-TOK-003: every raw line-height 1.5/1.6 replaced with var(--line-height-tight)/var(--line-height-body); orphans 1.45/1.55 take new --line-height-snug/--line-height-draft.
- [x] 2.4 WEB-TOK-004: --tracking-tight (-0.01em) and --tracking-tighter (-0.02em) added plus --tracking-wide (0.04em); all raw letter-spacing values replaced.

## 3. BFS — Cross-surface regression and completeness
- [x] 3.1 No selector, class, testid, media query or z-index renamed; no .tsx byte changed; computed-style ledger identical on all 50+ probes; pre-existing suites untouched and unweakened (hooks survive verbatim, so no migration); git diff --check clean.

## 4. Verification
- [x] 4.1 `npm ci`, `tsc --noEmit` (exit 0), `vite build` (exit 0), `npx playwright test tests/token-sweep.spec.ts` green standalone; `node scripts/check-openspec-change-names.mjs` exit 0; `openspec validate --all --strict --no-interactive` green; archive readiness with exactly two commits (no push). Full verify.sh deferred to end of batch per the batch plan.
