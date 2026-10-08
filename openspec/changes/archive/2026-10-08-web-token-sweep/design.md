# Design: Web token sweep (web-token-sweep)

## Context and implementation boundary
Repository Chronicleaf only. Primary boundary
`src/Chronicleaf.Web/src/styles.css` (literal-to-token
references only) and `src/Chronicleaf.Web/src/tokens.css`
(additions only — no existing token value changes),
`src/Chronicleaf.Web/tests/token-sweep.spec.ts` (new,
standalone: static grep oracles plus computed-style spot checks
through `page.setContent`, no server, no database, no
`scripts/e2e.sh` wiring). Depends on no backend package and
changes none: no API contract is rendered differently because
no computed value changes. Preserve forge.yaml, .standard
receipts, unrelated files, HANDOFF.md (until the second
HANDOFF-only commit), ROADMAP.md, .gitignore, every existing
`data-testid`, every class name, every media query, every
z-index, and current_spec (set to `web-token-sweep`; HANDOFF.md
already points there).

## Language, ownership and release
CSS custom-property references in the Web project; no C#, no
migration, no API edit, no `.tsx` edit. The sweep is shipped as
in-tree token references, never as a third-party system and
never as a runtime import from a CDN. No package install in
this change (no new dependency of any kind).

**Keep local.** Token presentation is Chronicleaf-product
presentation; no matching recurring implementation is evidenced
in sibling platform contracts. Evidence inspected:
dotnet-platform-libs/README.md lists Platform.Jobs and
Platform.Storage families but no Web shell or token system. No
shared `/lib` added and no sibling edit authorized.

## Contract and compatibility decisions
The rendered contract is the delivered CSSOM: for every
touched declaration the computed value after the change is the
computed value before the change, exactly. The mechanism is
value-exact aliasing — a replacement is lawful if and only if
the token's value is character-identical to the literal it
replaces, so no conversion, rounding or "close enough" ever
applies. Where a literal matches an existing token it takes
that token; where none fits, the token is added to `tokens.css`
first (ledger below) and then referenced. Comments carrying
numeric literals are reworded to name breakpoints and tokens
instead, so a naive source grep agrees with the documented
oracle. Media-query `px` breakpoints (`480px`/`720px`/`960px`)
stay literal by design (they are query semantics, not sizes);
unitless `0`, `100%` widths, and `z-index` stay literal by
design; the `1fr` grid fraction stays literal (a fraction, not
a length).

## Token-addition ledger (39 additions, zero existing-value changes)
Spacing (`--space-<px>px` names the pixel value at the
delivered 16px root, so each name documents its own value):
`--space-2px: 0.125rem`, `--space-4px: 0.25rem`,
`--space-5px: 0.3125rem`, `--space-7px: 0.4375rem`,
`--space-8px: 0.5rem`, `--space-14px: 0.875rem`,
`--space-15px: 0.9375rem`, `--space-18px: 1.125rem`,
`--space-22px: 1.375rem`, `--space-24px: 1.5rem`,
`--space-32px: 2rem`, `--space-48px: 3rem`.
Type (semantic names; each used by one surface):
`--font-size-brand: 1.25rem`, `--font-size-tagline: 1.125rem`,
`--font-size-record: 1rem`, `--font-size-preview: 0.925rem`,
`--font-size-code: 0.825rem`, `--font-size-badge: 0.75rem`,
`--line-height-snug: 1.45`, `--line-height-draft: 1.55`.
Tracking: `--tracking-tight: -0.01em`,
`--tracking-tighter: -0.02em`, `--tracking-wide: 0.04em`.
Borders and focus: `--border-thin: 1px`,
`--border-flag: 3px`, `--ring-width: 2px`,
`--ring-offset: 2px`, `--radius-large: 0.625rem`.
Measures (one token per delivered dimension):
`--layout-panel-max: 44rem`, `--measure-input: 16rem`,
`--measure-form-row: 12rem`, `--measure-card: 20rem`,
`--anchor-offset: 5rem`, `--measure-textarea: 4.5rem`,
`--measure-editor: 18rem`, `--measure-draft-max: 28rem`,
`--measure-review-max: 32rem`, `--error-reserve: 1.4em`,
`--offscreen-x: -9999px`.
Existing tokens reused unchanged wherever values match
(`--space-1..7`, `--space-page`, all delivered
`--font-size-*`, `--line-height-body`/`--line-height-tight`,
`--radius-small`/`--radius-medium`/`--radius-card`/`--radius-pill`).

## Behavioral model and invariants
- **Values move, never change.** Every replacement resolves to
  the identical length, so the cascade, the computed styles and
  the painted pixels are unchanged in every viewport, including
  inside all three media queries (whose conditions stay
  literal).
- **Tokens are the only literal source.** After the sweep no
  declaration outside `tokens.css` carries a raw length, a hex
  color, a raw line-height or a raw letter-spacing; the grep
  oracles below fail the suite if one returns.
- **The exclusions are closed, not open.** Only unitless `0`,
  `100%`, `z-index` integers, and media-query `px` breakpoints
  stay literal. Anything else numeric-with-unit outside
  `tokens.css` (or outside a `var()` reference, which is how a
  token is consumed) is non-conformant.
- **No selector, class, testid or query is renamed.** The sweep
  edits declaration values only; every pre-existing suite keeps
  its hooks unmodified and no suite is migrated.

## Failure, boundary and rollback policy
A raw literal surviving in `styles.css` fails the new suite
(grep oracles); a hex literal outside `tokens.css` fails the
new suite (built-CSS set comparison); a computed value
differing from its captured pre-change value fails the new
suite (spot checks); a renamed selector or testid fails the
pre-existing suites loudly (none is touched). Unknown
client-side path keeps the delivered "Nothing here" view
unchanged. Rollback is frontend-only: revert the two CSS files
and delete the new suite; no data, migration, API surface,
credential, antiforgery value or testid is touched, so stored
evidence, approvals, credentials and operations are
unaffected. Partial work (a literal left raw at verification
time) fails its requirement, not silently accepted.

## Verification oracle
Static (no server): `styles.css` source with comments
stripped, `var(...)` references stripped, and `@media` /
`z-index` lines excluded shows zero matches for a numeric
literal with a `rem`/`px`/`em` unit; zero hex literals in
`styles.css` source; the hex set of the built `dist` CSS
equals the hex set of `tokens.css` (token definitions are the
only lawful hex); every `line-height:` declaration references
`var()`; every `letter-spacing:` declaration references
`var()`; `tokens.css` defines `--tracking-tight: -0.01em` and
`--tracking-tighter: -0.02em` exactly. Visual (no server):
`page.setContent` with the shipped stylesheets over synthetic
elements asserts 50+ computed-style probes equal the
pre-change values captured at the start of the run (16px root:
body `16px`, card padding `16px`, action padding `8px 15px`,
nav gap `6px`, page heading `28px`, shell padding `32px`, and
the full ledger in the suite). Method note: the DELTA
screenshot set is untouched because no pixel may move; the
computed-style diff is the sharper oracle for a change whose
claim is byte-identical rendering.

## Decision ledger and activation gates
Phase: POST-MVP. DEMAND satisfied by the batch drain order —
no further activation needed. No unresolved product-policy
decision in this scoped contract. Third-party versions are
lockfile implementation inputs, not an invitation to redesign
contracts. Missing browsers at implementation time is recorded
prerequisite unavailability, never fabricated success.

### Token-sweep decisions
1. **Token-first ordering.** All 39 additions land in
   `tokens.css` before any `styles.css` reference is written,
   so no reference ever names a token that does not exist.
2. **Value-exact aliasing only.** A replacement is lawful if
   and only if the token value is character-identical to the
   replaced literal; nothing is converted, rounded or
   approximated.
3. **Pixel-named spacing extensions.** New `--space-<px>px`
   tokens name their own value at the delivered root, keeping
   the delivered `--space-1..7` scale untouched and making
   each addition self-documenting.
4. **Semantic font additions.** Six `--font-size-*` tokens
   name the surface they serve (brand, tagline, record,
   preview, code, badge); same-valued spacing uses the
   spacing token, never the font token.
5. **Border and ring tokens.** `1px` borders take
   `--border-thin`, the `3px` finding flag takes
   `--border-flag`, `2px` outlines take `--ring-width` and
   `outline-offset` takes `--ring-offset`.
6. **Measure tokens for dimensions.** Widths, heights,
   scroll margin, the error-slot reserve and the offscreen
   distance each take one named token; `0`, `100%`,
   `z-index` and media-query breakpoints stay literal.
7. **Line-height mapping.** Raw `1.5` takes
   `--line-height-tight`, raw `1.6` takes
   `--line-height-body`; the two orphans take new
   `--line-height-snug` (`1.45`) and `--line-height-draft`
   (`1.55`).
8. **Tracking tokens.** `--tracking-tight` (`-0.01em`) and
   `--tracking-tighter` (`-0.02em`) are added per the brief;
   the `0.04em` uppercase labels take `--tracking-wide`.
9. **Standalone proof, deferred full gate.** The new suite
   needs no server or database and runs via a direct
   `playwright test` invocation; the full `verify.sh` runs
   once at the end of the batch by the coordinator, not here.

## Requirement traceability
| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| WEB-TOK-001 sizing sweep + no-change invariant | Raw sizes become token references, rendering identical | Value-exact + token-first + spacing + measure decisions above | styles.css references + tokens.css additions | Success / Failure / Boundary | 1.1, 2.1, 3.1, 4.1 | Stripped-source grep zero; 50+ computed probes equal pre-change values |
| WEB-TOK-002 color sweep | Covered color/spacing/type literals become tokens, zero hex | Value-exact decisions above | styles.css references | Success / Failure / Boundary | 1.1, 2.2, 3.1, 4.1 | Zero hex in styles.css; built-CSS hex set equals tokens.css hex set |
| WEB-TOK-003 line-height sweep | Raw 1.5/1.6 become vars | Line-height-mapping decision above | styles.css references + two new tokens | Success / Failure / Boundary | 1.1, 2.3, 3.1, 4.1 | Every line-height declaration references var() |
| WEB-TOK-004 tracking sweep | Tracking tokens added, raw spacing replaced | Tracking decision above | tokens.css three tokens + styles.css references | Success / Failure / Boundary | 1.1, 2.4, 3.1, 4.1 | Every letter-spacing references var(); tight/tighter defined exactly |
