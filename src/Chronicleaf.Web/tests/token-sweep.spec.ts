import { expect, test } from '@playwright/test';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * WEB-TOK-001/002/003/004. The token sweep is a pure CSS move with zero
 * visual change, so this suite proves it without a server or a database:
 * static grep oracles over the sources and the built CSS, plus
 * computed-style spot checks through page.setContent.
 *
 * Run standalone after `vite build`:
 *   npx playwright test tests/token-sweep.spec.ts
 *
 * Raw-sizing oracle (documented exclusions): strip block comments, strip
 * var(...) references (a token consumption is not a raw value), drop
 * @media lines (px breakpoints stay literal by design) and z-index lines,
 * then zero numeric literals with rem/px/em units may remain. Unitless 0,
 * 100%, z-index integers and the 1fr fraction never match the oracle.
 */
const WEB_DIR = join(dirname(fileURLToPath(import.meta.url)), '..');
const SRC_DIR = join(WEB_DIR, 'src');
const STYLES = readFileSync(join(SRC_DIR, 'styles.css'), 'utf8');
const TOKENS = readFileSync(join(SRC_DIR, 'tokens.css'), 'utf8');

function stripComments(css: string): string {
  return css.replace(/\/\*[\s\S]*?\*\//g, '');
}

function codeLines(css: string): string[] {
  return stripComments(css)
    .split('\n')
    .filter((line) => !line.includes('@media') && !line.includes('z-index'));
}

function withoutVars(text: string): string {
  return text.replace(/var\([^)]*\)/g, 'VAR');
}

function builtCssFiles(): string[] {
  const dir = join(WEB_DIR, 'dist', 'assets');
  if (!existsSync(dir)) return [];
  return readdirSync(dir)
    .filter((f) => f.endsWith('.css'))
    .map((f) => readFileSync(join(dir, f), 'utf8'));
}

function hexSet(css: string): Set<string> {
  const out = new Set<string>();
  for (const m of css.matchAll(/#([0-9a-fA-F]{3,8})\b/g)) {
    let h = m[1].toLowerCase();
    if (h.length === 3) h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
    if (h.length === 6) out.add('#' + h);
  }
  return out;
}

test('WEB-TOK-001: no raw rem/px sizing survives in styles.css source', async () => {
  const code = withoutVars(codeLines(STYLES).join('\n'));
  const hits = code.match(/-?\d*\.?\d+(?:rem|px|em)\b/g) ?? [];
  expect(hits, `raw sizing literals outside tokens/vars: ${hits.join(', ')}`).toEqual([]);
});

test('WEB-TOK-002: zero hex outside tokens.css, built CSS matches the token set', async () => {
  expect(STYLES.match(/#[0-9a-fA-F]{3,8}\b/g) ?? []).toEqual([]);
  const built = builtCssFiles();
  expect(built.length, 'run `vite build` before this suite: no dist/assets/*.css found').toBeGreaterThan(0);
  const builtHex = new Set<string>();
  for (const css of built) for (const h of hexSet(css)) builtHex.add(h);
  expect([...builtHex].sort()).toEqual([...hexSet(TOKENS)].sort());
});

test('WEB-TOK-003: every line-height declaration references a token', async () => {
  const decls = codeLines(STYLES)
    .map((l) => l.trim())
    .filter((l) => l.startsWith('line-height:'));
  expect(decls.length).toBeGreaterThan(0);
  for (const d of decls) expect(d, d).toContain('var(--line-height-');
});

test('WEB-TOK-004: tracking tokens exist and every letter-spacing references one', async () => {
  expect(TOKENS).toContain('--tracking-tight: -0.01em;');
  expect(TOKENS).toContain('--tracking-tighter: -0.02em;');
  const decls = codeLines(STYLES)
    .map((l) => l.trim())
    .filter((l) => l.startsWith('letter-spacing:'));
  expect(decls.length).toBeGreaterThan(0);
  for (const d of decls) expect(d, d).toContain('var(--tracking-');
});

test('WEB-TOK-001 invariant: computed styles equal the captured pre-change values', async ({
  page,
}) => {
  await page.setContent(`<html><head><style>${TOKENS}\n${STYLES}</style></head><body>
<div class="shell" id="shell"><div class="shell__panel" id="panel">p</div></div>
<div class="app" id="appwrap"><nav class="nav" id="nav"><button class="nav__item" id="navitem">x</button></nav>
<h1 class="page__heading" id="h1">t</h1>
<div class="card" id="card">c</div>
<button class="action" id="action">a</button>
<button class="filter" id="filter">f</button>
<span class="badge" id="badge">b</span>
<input class="session__input" id="sinput" />
<button class="session__action" id="saction">s</button>
<div class="notice" id="notice">n</div>
<div class="record" id="record">r</div>
<div class="milestone" id="ms">m</div>
<div class="timeline__entry" id="tl">t</div>
<div class="empty" id="empty">e</div>
<div class="finding" id="finding">f</div>
<span class="app__brand" id="brand">b</span>
<p class="shell__tagline" id="tagline">t</p>
<p class="view__state" id="vstate">v</p>
<p class="empty__body" id="ebody">e</p>
<p class="milestone__notes" id="mnotes">m</p>
<div class="draft__body" id="dbody">d</div>
<div class="review__preview" id="rprev">r</div>
</div></body></html>`);
  // Expected values captured from the pre-change stylesheets at the start
  // of the run (16px root) and verified identical after the sweep.
  const expected: Array<[string, string, string]> = [
    ['body', 'font-size', '16px'],
    ['#shell', 'padding-top', '32px'],
    ['#panel', 'padding-top', '48px'],
    ['#panel', 'max-width', '704px'],
    ['#appwrap', 'padding-left', '32px'],
    ['#appwrap', 'max-width', '1152px'],
    ['#h1', 'font-size', '28px'],
    ['#h1', 'letter-spacing', '-0.56px'],
    ['#nav', 'column-gap', '6px'],
    ['#nav', 'margin-top', '12px'],
    ['#navitem', 'padding-top', '8px'],
    ['#navitem', 'padding-right', '15px'],
    ['#navitem', 'font-size', '15px'],
    ['#card', 'padding-top', '16px'],
    ['#card', 'border-radius', '12px'],
    ['#card', 'row-gap', '6px'],
    ['#action', 'padding-top', '8px'],
    ['#action', 'padding-right', '15px'],
    ['#action', 'font-size', '15px'],
    ['#action', 'min-block-size', '40px'],
    ['#filter', 'padding-top', '5px'],
    ['#filter', 'font-size', '14px'],
    ['#badge', 'padding-top', '2px'],
    ['#badge', 'font-size', '12px'],
    ['#sinput', 'padding-top', '8px'],
    ['#sinput', 'min-width', '256px'],
    ['#sinput', 'border-radius', '8px'],
    ['#saction', 'padding-right', '16px'],
    ['#notice', 'padding-top', '12px'],
    ['#notice', 'line-height', '22.5px'],
    ['#notice', 'font-size', '15px'],
    ['#record', 'padding-top', '14px'],
    ['#record', 'border-radius', '10px'],
    ['#ms', 'padding-top', '18px'],
    ['#tl', 'padding-top', '12px'],
    ['#tl', 'border-radius', '12px'],
    ['#empty', 'padding-top', '24px'],
    ['#empty', 'border-radius', '12px'],
    ['#finding', 'border-left-width', '3px'],
    ['#finding', 'padding-top', '12px'],
    ['#brand', 'font-size', '20px'],
    ['#brand', 'letter-spacing', '-0.4px'],
    ['#tagline', 'font-size', '18px'],
    ['#tagline', 'line-height', '27px'],
    ['#vstate', 'line-height', '22.5px'],
    ['#ebody', 'line-height', '24px'],
    ['#mnotes', 'line-height', '24px'],
    ['#dbody', 'line-height', '21.7px'],
    ['#rprev', 'line-height', '23.68px'],
    ['#rprev', 'font-size', '14.8px'],
  ];
  for (const [sel, prop, want] of expected) {
    const got = await page.evaluate(
      ([s, p]) => getComputedStyle(document.querySelector(s)!).getPropertyValue(p),
      [sel, prop] as [string, string],
    );
    expect(got, `${sel} ${prop}`).toBe(want);
  }
});
