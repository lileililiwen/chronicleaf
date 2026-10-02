# Documentation capture

Standing conventions: capture the actual local route using globally installed shot-scraper or playwright-cli, or the repository's own Playwright dependency. Record command, viewport (desktop 1440x900; mobile 390x844), selector/wait condition, artifact under docs/assets/, Git revision and privacy review (no PAT/BYOK/private evidence). Do not install browser documentation tools per repository. Product screenshot jobs will use Playwright for .NET independently.

## Capture record — 2026-10-02 (community-foundation runtime shell)

Status: real runtime capture of the implemented React shell. This is a live screenshot of the
published application, not a mockup and not a design render.

- Source: the built and published application produced by `scripts/publish.sh`, started with
  `ASPNETCORE_URLS=http://127.0.0.1:5081 dotnet Chronicleaf.Api.dll` from `dist/e2e`, so the
  artifact is the same process that serves `/api/health` and the static routing contract.
- Command:
  `npx playwright screenshot --viewport-size="1440,900" --wait-for-selector="[data-testid='setup-pending']" --wait-for-timeout=500 http://127.0.0.1:5081/ ../../docs/assets/chronicleaf-shell.png`
  run from `src/Chronicleaf.Web`, using the repository's own `@playwright/test` 1.63.0
  installation. No separate browser documentation tool was installed.
- Viewport: desktop 1440x900 (PNG, 8-bit RGB). The mobile 390x844 convention is not captured
  for this shell; the layout is a single centred panel and is not separately evidenced.
- Selector/wait condition: the shell's setup-pending element must be visible, then a fixed
  500ms settle, so the capture happens after React has rendered.
- Artifact: `docs/assets/chronicleaf-shell.png`, sha256
  `2534968bcecdab13437db5fe5f58c7c9ae30c7c4003c0071b23441bc4b07c08f`. Two independent runs
  produced this identical digest.
- Git revision: this repository has no commit yet — `git rev-parse --verify HEAD` fails with
  `fatal: 需要一个单独的版本` on branch `main` — so the capture cannot be bound to a commit id.
  It is bound instead to the content of the sources that produced it, by `git hash-object`:
  `src/Chronicleaf.Web/src/App.tsx` b7c69f9e3967432c4649fef2d8a150133515432a,
  `src/Chronicleaf.Web/src/main.tsx` 29f8025f4820dc96175c28c5454e518802a2823e,
  `src/Chronicleaf.Web/src/styles.css` 5cea2a78befc7b4dc448de268e4b44c82e25c4a6,
  `src/Chronicleaf.Web/index.html` 5fb95cbacb9978066c6d74ffee2be820826899e7,
  `src/Chronicleaf.Web/package.json` 04f94c863116e2790741a5c2f19740f9c29832b9,
  `src/Chronicleaf.Web/package-lock.json` a7218a392947033f44448d5d93ee7027b0669f44.
  Built output served to the browser, sha256: `wwwroot/index.html`
  9bac80c3d424013035ebd29be7f4a7da3e49590566a86ed4647e5266449794c3, `wwwroot/assets/index-BEY2Mg3R.js`
  39a5595bdd6df8c1f54ef0eef31f6e0a36b4274e5c94f90460d69a4b2351ca97, `wwwroot/assets/index-ROieAo4J.css`
  e216a1fb9c1cc9b204325bca212badace0bcbdc90b5eb735f478efae5031b5d6.
- Privacy review: the shell renders only the product name, the public tagline and the
  "Project setup coming in a later change" state. There is no user data, no milestone, no
  credential, no PAT, no BYOK token, no private repository content and no filesystem path in
  the image. The capture server log records exactly four requests, all to loopback
  `127.0.0.1:5081` (`/`, `/api/health`, `/assets/index-BEY2Mg3R.js`, `/assets/index-ROieAo4J.css`),
  and no non-loopback request, so the page loads no remote font, script or image. Treat the
  artifact and these digests as private repository evidence by default.
