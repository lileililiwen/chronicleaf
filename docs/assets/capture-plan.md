# Documentation capture

Standing conventions: capture the actual local route using globally installed shot-scraper or playwright-cli, or the repository's own Playwright dependency. Record command, viewport (desktop 1440x900; mobile 390x844), selector/wait condition, artifact under docs/assets/, Git revision and privacy review (no PAT/BYOK/private evidence). Do not install browser documentation tools per repository. Product screenshot jobs will use Playwright for .NET independently.

`scripts/capture.sh` is the repository's own way to do all of that for the product: it publishes the
application, starts a disposable PostgreSQL, drives a real browser through the real product flows,
writes the PNGs under `docs/assets/`, and prints a JSON evidence block on stdout with the commands
run, the git revision and every digest. A record below is written from that block. Reproduce one
with `bash scripts/capture.sh` from the repository root; it needs Docker, .NET 10, node and the
repository's own Playwright chromium, and it publishes nothing if any step fails.

## Capture record — 2026-10-02 (community-foundation runtime shell)

Status: real runtime capture of the implemented React shell. This is a live screenshot of the
published application, not a mockup and not a design render.

Note added 2026-10-02, after the later capture below was taken: this artifact is bound to the source
digests recorded below and remains true for that revision. It shows the earlier runtime shell, which
had no owner sign-in, no projects and no milestones. It does not depict the current product; the
signed-in product is evidenced by the second 2026-10-02 record at the end of this file.

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

## Capture record — 2026-10-02 (owner session, project and milestones)

Status: real runtime capture of the signed-in product. The application was published, started against
a disposable PostgreSQL, and driven in a real browser through the product's own flows; every record
in these images was created by the product during the run. Not a mockup, not a design render, and
not a page emptied or filled by hand. Reproduce with `bash scripts/capture.sh` from the repository
root; it publishes nothing to `docs/assets/` if any step fails.

- Source: the built and published application produced by `scripts/publish.sh dist/capture Release`,
  started with `ASPNETCORE_URLS=http://127.0.0.1:5081 dotnet Chronicleaf.Api.dll` from
  `dist/capture`, against a `postgres:17.6-alpine` container started for this run, migrated by
  `dotnet ef database update --project src/Chronicleaf.Infrastructure --startup-project
  src/Chronicleaf.Api --no-build` and provisioned with the single installation row. The browser was
  pointed at that published process, so the artifact is the same one that serves `/api/health` and
  the static routing contract. The access token was 32 random bytes generated for this run: the
  server was given only its SHA-256 hash, and the plaintext was typed into the local sign-in form by
  the browser, held in a mode-600 file inside the gitignored publish directory and deleted when the
  run ended. It is not in any image, in the evidence block or in any committed file.
- Command: `bash scripts/capture.sh`, which runs these in order and prints each one:
  1. `git rev-parse HEAD`
  2. `bash scripts/publish.sh dist/capture Release`
  3. `docker run --detach --rm --name chronicleaf-capture-postgres-<pid> --env POSTGRES_DB=chronicleaf_capture
     --env POSTGRES_USER=chronicleaf --env POSTGRES_PASSWORD=chronicleaf-capture --publish
     127.0.0.1::5432 postgres:17.6-alpine`
  4. `docker exec <container> pg_isready --username=chronicleaf --dbname=chronicleaf_capture`, then
     `docker exec <container> psql --username=chronicleaf --dbname=chronicleaf_capture --command 'SELECT 1'`,
     each waited for up to 60s
  5. `ConnectionStrings__Chronicleaf=<loopback container> dotnet ef database update --project
     src/Chronicleaf.Infrastructure --startup-project src/Chronicleaf.Api --no-build` (up to three
     attempts: a container that has just answered a local query can still refuse the first connection
     from the host, and if three attempts do not complete the run stops and prints what EF said)
  6. `docker exec <container> psql --username=chronicleaf --dbname=chronicleaf_capture --command
     "INSERT INTO installation (id, schema_version, created_at) VALUES (<one random uuid>, 1, now())"`
  7. `openssl rand -hex 32` for the token; only `Chronicleaf__AccessTokenHash` is given to the server
  8. `cd dist/capture && ASPNETCORE_URLS=http://127.0.0.1:5081 ASPNETCORE_ENVIRONMENT=Production
     ConnectionStrings__Chronicleaf=<loopback container> Chronicleaf__AccessTokenHash=<redacted>
     Chronicleaf__PublicOrigin=http://127.0.0.1:5081 dotnet Chronicleaf.Api.dll`
  9. `curl --fail --silent --show-error --max-time 2 http://127.0.0.1:5081/api/health`, waited for up
     to 60s
  10. one Node script run from `src/Chronicleaf.Web`, using the repository's own `@playwright/test`
     1.63.0 and its own `playwright.config.ts` for the origin and the desktop viewport
  The full list, the git revision and every digest are printed as JSON on stdout between
  `-----BEGIN capture-evidence-----` and `-----END capture-evidence-----`. That block is evidence for
  this record and is not written into this file. No separate browser documentation tool was installed
  and none was needed; nothing was installed by the script at all.

  What the browser did, in the product's own flows: it signed in on the local session form with the
  keyboard alone, photographed the empty dashboard before anything existed, created the project
  "Chronicleaf" through the project form, and recorded the milestone
  "implement projects-and-milestones records and UI" through the milestone form. That save is what
  stores it as `confirmed`, because the form has no status field. The candidate was then handed to
  the product's own intake route, `POST /api/projects/{id}/milestones/import`, called from inside the
  page with that browser's session cookie and a fresh antiforgery value - the same call the
  repository's own browser fixture in `src/Chronicleaf.Web/tests/projects-and-milestones.spec.ts`
  makes, and the only way to reach a candidate today, because no product surface collects records
  from a source yet. The route answered 201, and the record read back as `candidate` through
  `GET /api/milestones/{id}`. The capture aborts if any of that is not true. No row was written to
  the database for the screenshot's benefit: the only row the script inserts by hand is the
  operator-provisioned installation singleton every installation has, exactly as `scripts/e2e.sh`
  does it.

  The content is this repository's own history, so a reader can check every value on screen: the
  confirmed milestone's title and date are commit 065dd8e
  (`feat(milestones): implement projects-and-milestones records and UI`, committed 2026-10-02T10:46Z),
  the candidate's are commit 5154b26 (`feat(jobs): implement background-jobs durable operation
  execution`, committed 2026-10-02T13:36Z), the version is `v0.1.0` from
  `src/Chronicleaf.Web/package.json`, and the two next steps are the two entries the page itself
  prints under "Not in this change". The candidate's source is `capture-run` with the source key
  `capture-run/5154b26`, which names the capture rather than claiming a record from any external
  service.
- Viewport: desktop 1440x900 and mobile 390x844, each in its own browser context, both from the
  repository's own `Desktop Chrome` device profile at `deviceScaleFactor` 1; the mobile context adds
  `isMobile` and `hasTouch`, so 390x844 is a real mobile-emulated render rather than a narrow
  desktop window. Locale `en-US`, zone `UTC`, `colorScheme: light` and `reducedMotion: reduce` are
  pinned on both contexts so the rendering is the same on every run. The screenshots are full-page:
  the signed-in shell spends roughly half of a 900px viewport on its own brand and session facts, so
  a viewport-only capture of a project shows the first milestone and cuts the second in half, which
  would not evidence what it claims to. The files are therefore 1440x1084, 1440x1589 and 390x2395
  pixels; each size is read out of the written file's own PNG header, not assumed, and printed in
  the evidence block. The empty-state dashboard was captured at desktop only; that state is the
  same at any width, so a mobile copy of it would add a file and no information.
- Selector/wait condition: the steps, each waiting for the thing it just did to be on the page -
  sign in, wait for `[data-testid='product-area']` visible; the genuinely empty first-run dashboard,
  wait for `[data-testid='dashboard-empty']` visible, after asserting the installation really holds
  0 projects and 0 milestones and failing if it does not; create the project, wait for
  `[data-testid='project-name']` visible; record the milestone, wait for `[data-testid='milestone-list']`
  visible; the intake call, wait for 201 and for the record to read back as a candidate; the project
  view, wait for `[data-testid='milestone']` to have 2 items and assert exactly one `confirmed` and
  one `candidate`; then the same sign-in and the same project again in the mobile browser, waiting
  for `[data-testid='product-area']` and `[data-testid='project-detail']`. Every screenshot then
  waits for its view to be visible, then for `document.fonts.ready` and a fixed 500ms settle, with
  animations disabled and the caret hidden.
- Artifact: three PNGs under `docs/assets/`, digests as they are in the tree this record is added
  to, and the second run's digests beside them:

  | file | viewport | PNG | bytes | this run (run 2, committed) | run 1 |
  | --- | --- | --- | --- | --- | --- |
  | `chronicleaf-dashboard-empty-desktop.png` | 1440x900 | 1440x1084 | 84557 | `8c932d612c7f0bb62121f348aab97b021f9b47ce3c6cafd2a91765b430217943` | `28df328d2a7c3f5fa29484a329055ac1931779822b42cbf1c593c32980bec8b7` |
  | `chronicleaf-project-milestones-desktop.png` | 1440x900 | 1440x1589 | 141482 | `f30cd4d5eb3095f87d8fd2d211577656a83c62ff7d84f245981f6e6482d534b9` | `8e44bb4493cc18f845cf0b2281bd618ba9867f8a658f2df9e62f570d44de9e88` |
  | `chronicleaf-project-milestones-mobile.png` | 390x844 | 390x2395 | 136737 | `1a6b77bdceb91e62793e18f7416a141470bf7791e3777c21aafa8277b9d81467` | `363c386ca6348343e5cdb93b5e2f28a846f3b64fb249d1b103182ad947833046` |

  Reproducibility, stated as measured rather than as hoped: **the two runs did not produce
  identical PNG digests, and they cannot.** The signed-in shell prints the owner actor id, and that
  is a UUID the installation generates for itself, so it is different in every run - run 1 rendered
  `dab14e48-175b-4611-b5f5-fd2d3164b59f` where run 2 rendered
  `da48fc92-713a-486f-bac3-fba15d1c3b92`. That is the whole difference, and it was checked rather
  than assumed: the script records the page's visible text with every UUID replaced, and the
  resulting digests are byte-identical across the two runs -
  `2a1562c8c93a3c8ffd690085d05846db5f98264e680a7720d3b646f8f50a4c73` for the empty dashboard and
  `a1d256ca8f1b4e8bc54fcd704bb6e4cd11549cbc020ceb07dc95adeb05f78054` for both project captures. The
  PNG dimensions are also identical across runs. Everything that does not depend on that one random
  identifier is bit-reproducible: all 25 client source blob ids were identical, and so was every
  built client artifact that was served - `dist/capture/wwwroot/index.html`
  `0d4c4d720b7ae4f89bbc00ddfa0daa5c75c33fbfc5381b5a484df93bf2199e40` (486 bytes),
  `dist/capture/wwwroot/assets/index-CtjSzPjz.css`
  `25911c43db5742d88c6f52d4aa5cb76176749b7cbec5d2c597cf8bac392c4c5c` (7899 bytes) and
  `dist/capture/wwwroot/assets/index-D3DjKiQm.js`
  `91daf90daaf1d3a2077891bd35379f12c99df6903df8d53145b04e1715665b32` (260920 bytes). A capture whose
  digests have to match byte for byte can only be had by fixing the installation id, which would be
  a rigged capture rather than a real one, so this record keeps the honest result and names the
  cause.
- Git revision: `git rev-parse HEAD` at the time of the capture was
  `9e910953adfbf31b1c17c9979dd7b6fa3d4ba089` on branch `main`, so unlike the record above this
  capture is bound to a real commit. The working tree at that moment held four untracked paths -
  `scripts/capture.sh` and the three PNGs this record adds - because the capture and this record
  were produced before either was committed; nothing else was modified. The capture is also bound to
  the content of the client sources that produced it, by `git hash-object`:
  `src/Chronicleaf.Web/src/App.tsx` f3edb6aaa0697c982eea6e0ad31e076380028b1f,
  `src/Chronicleaf.Web/src/main.tsx` 29f8025f4820dc96175c28c5454e518802a2823e,
  `src/Chronicleaf.Web/src/styles.css` c40a0dd3880a10dbb2120e189bbbca437b9c6a08,
  `src/Chronicleaf.Web/index.html` 5fb95cbacb9978066c6d74ffee2be820826899e7,
  `src/Chronicleaf.Web/package.json` 04f94c863116e2790741a5c2f19740f9c29832b9,
  `src/Chronicleaf.Web/package-lock.json` a7218a392947033f44448d5d93ee7027b0669f44,
  `src/Chronicleaf.Web/playwright.config.ts` 74ae0396139f8b52f2263c05a42da6902fd8e3f4.
  `main.tsx`, `index.html`, `package.json` and `package-lock.json` are unchanged from the earlier
  record; `App.tsx` and `styles.css` have moved on, and the built asset digests above differ from
  the earlier record's for the same reason. The evidence block carries all 25 blob ids, including
  every file under `src/Chronicleaf.Web/src`.
- Privacy review: performed on the three written files, not asserted. What the images contain is the
  product name, the public tagline, the session facts (`Actor`, `Idle timeout 12 hours`,
  `Session lifetime 7 days`, a Sign out button), the project name and description, the status
  filter, the two milestone records quoted above with their statuses, and the page's own "Not in
  this change" panel. There is no credential, no PAT, no BYOK token, no session cookie, no
  antiforgery value, no absolute host path, no username and no email address in any of them. The one
  machine-generated value on screen is the owner actor UUID in the session facts, and it is an
  identifier the disposable installation invented for itself and threw away with its container; it
  names no person, host or account. The only repository content on screen is two commit
  subjects, two short commit ids and the web package's own version, which this record states openly
  rather than as leaked source: no file contents, no diff, no URL and no credential is visible. The
  access token does not appear in any image, and the sign-in form that would have contained it is
  replaced by the session facts before anything was photographed.

  The request log was read from the run, not assumed. The browser issued 39 requests over 14 unique
  URLs, and the published server recorded 40 request lines - the 39 plus the script's own
  `/api/health` probe. Every one of them is to `127.0.0.1:5081`: `/`, `/assets/index-CtjSzPjz.css`,
  `/assets/index-D3DjKiQm.js`, `/api/session` (2 POST, the two sign-ins, and the GETs that follow
  them), `/api/session/antiforgery`, `/api/projects` (GET and POST), `/api/projects/{id}` and
  `/api/projects/{id}/milestones`, `/api/projects/{id}/milestones/import`, `/api/milestones/{id}` and
  `/projects/{id}`. There is no other host anywhere in the server log, so the page loads no remote
  font, script or image. The driver also refuses rather than allows any non-loopback request, and
  reported zero issued and zero blocked, which is the check that would have caught a remote resource.
  The server log contains no 64-character hex value at all, so neither the session cookie nor the
  antiforgery value reached it. The captured images and these digests remain private repository
  evidence by default.
- Not captured, and not claimed: the background jobs panel at `/operations` is not in these images.
  A meaningful panel capture would need the operations rows that `scripts/e2e.sh` inserts by hand for
  its own fixture, and this capture deliberately inserts no such rows; a panel photograph with
  nothing in it would only show an empty state, which the empty dashboard capture already covers.
  The mobile captures are the same project as the desktop one, at 390x844, and are evidence of that
  width only - the repository has no other mobile-specific surface to show.
