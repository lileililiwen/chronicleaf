# Chronicleaf

> Turn project milestones into publishable stories.

把开发进展自动变成可发布的内容资产。面向独立开发者、开源维护者、AI Coding 用户和小型开发工作室。

## Status

Twenty-three of the dependency-ordered OpenSpec packages are implemented, verified and archived:
`community-foundation` (runtime shell), `community-persistence` (PostgreSQL data foundation),
`community-access` (single-user owner sessions), `projects-and-milestones` (Project and Milestone
records with a real web UI), `background-jobs` (durable bounded operations), `local-media-storage`
(private media on `Platform.Storage`), `platform-jobs-adoption` (the same, on `Platform.Jobs`),
`milestone-evidence` (immutable evidence bundles), `credential-vault` (encrypted BYOK secrets),
`voice-profiles` (owner voice, snapshots, deterministic findings), `llm-provider-contract`
(provider-neutral consented generation), `github-evidence` (bounded GitHub import),
`screenshot-capture` (controlled capture jobs), `llm-openai-adapter`, `llm-gemini-adapter`,
`llm-anthropic-adapter`, `llm-openrouter-adapter`, `llm-ollama-adapter` (all five BYOK transports),
`grounded-markdown-drafts` (evidence-grounded drafts), `screenshot-curation` (human selection),
`milestone-timeline` (private chronological timeline), `markdown-review-export` (human
approval and export) and `community-compose-release` (two-service Community packaging with
restore evidence). The eleventh plus the adoption change are tracked in ROADMAP.md; the adoption
change is not one of the original 53 and exists because `background-jobs` had been built on a
false premise about platform package availability.

What exists today: a .NET 10 API serving a credential-free `/api/health` and an honest `/api/ready`,
hosting the built React client, persisting installation/audit/outbox/projects/milestones/evidence/
operations/credentials/voices/generation-previews/content-revisions/approvals in a real PostgreSQL
through EF Core migrations, requiring an owner session with origin and antiforgery checks on every
mutation, executing background work through `Platform.Jobs` on Hangfire with bounded retries,
leases and cancellation, storing credentials as AES-256-GCM ciphertext under an operator-supplied
key that is never in the database, the repository or the image, and shipping a `compose.yaml`
plus `docs/community-quickstart.md` that bring up the app and PostgreSQL, migrate, provision,
and prove restore-identical timeline and evidence heads. The web UI covers login/logout, a
dashboard, projects, milestone editing with human-only confirm/dismiss, evidence bundles with
visible unavailable sources, credential settings that never display a stored value, an operations
panel, voice profiles with actionable findings, a provider catalogue, GitHub sync, screenshot
capture and curation, a chronological project Timeline, grounded drafts, and human review with
revision-bound approval and byte-exact Markdown export. Some guarantees are enforced by
PostgreSQL rather than by application convention: evidence is append-only by trigger, content
revisions are append-only by trigger, one primary screenshot per milestone is a partial unique
index, and check constraints refuse ungrounded or malformed rows.

What does not exist yet: local-git evidence, release candidates, GraphQL enrichment, narrative
memory, HTML/static-blog export, evolution images, public journey, publishers, video scripts,
agency/Cloud/Hosted capabilities and the demand-gated content packs. The first complete workflow
(GitHub → confirmed Milestone → screenshot → grounded Markdown → human approval → export, plus
the Milestone Timeline) is implemented and proven by `scripts/verify-community.sh`, but
`deployable` remains `false`: this host cannot confine a browser kernel sandbox, so screenshot
success is unproven here, and the credential-gated live provider smoke was not performed.

## MVP

GitHub repository → human-confirmed Milestone → commit/diff evidence → project URL screenshot →
grounded Markdown draft → human editing and approval → export; include a Milestone Timeline. React
web UI is the primary experience.

## Stack and local verification

ASP.NET Core 10, React + TypeScript + Vite, PostgreSQL, Hangfire, xUnit, Playwright, BYOK.
Community deployment eventually uses one application and one PostgreSQL database with filesystem
media; no Redis or message broker.

```sh
node scripts/check-openspec-change-names.mjs     # change-name preflight
openspec validate --all --strict --no-interactive # 53/53
bash scripts/verify.sh                           # build, .NET tests, npm ci, web build
CHRONICLEAF_E2E=1 bash scripts/verify.sh          # the above plus the real-browser fixtures
bash scripts/capture.sh                          # real product UI capture into docs/assets/
```

`scripts/verify.sh` is the configured gate (`.project.json` `verification.command`). It needs a
running Docker daemon because the persistence, access, project, job, media, evidence, credential and
voice scenarios run against a real disposable PostgreSQL 17.6 container; an in-memory store is not
accepted as evidence, and an unavailable database fails the run rather than skipping it. Every
filtered stage asserts it selected at least one test, because `dotnet test --filter` exits 0 when a
pattern matches nothing — a mistyped filter would otherwise be a silent green no-op. The browser
stage is opt-in because it downloads Playwright browsers.

## Platform packages

`Platform.*` 0.1.0 packages are consumed as versioned packages from a **committed,
repository-relative feed** at `packages/platform-feed/`, wired in `nuget.config` beside nuget.org.
There is no absolute sibling path and no environment-variable feed: a fresh clone builds with no
`dotnet-platform-libs` checkout, and `scripts/verify-platform-feed.sh` fails when the committed feed
does not match what the build resolves. `scripts/pack-platform.sh` re-packs the feed from a checked-out
sibling and is the only place a sibling path may appear.

## Running it

Prerequisites: a running Docker daemon, .NET SDK 10 (`global.json` pins the
10.0.1xx line; `dotnet tool restore` provides the `ef` tool), Node 24 with npm
(the web build is also done inside Docker, but this path builds it locally),
plus `openssl` and `curl`. If host ports `5432` or `5080` are already taken,
pick free ones and change the `-p` mapping, the connection-string `Port`, the
`--urls` port and `Chronicleaf__PublicOrigin` together — the four must agree.

A full operator run, in order. The web client must be built first (it is not produced by
`dotnet run`), the access token must be kept by the operator (the README shows the exact
command that loses it), and the singleton `installation` row must be inserted before
`/api/ready` returns 200.

```sh
# 1. The web client. `scripts/publish.sh` in step 5 repeats this build, so skip this
#    step when you publish through it; it is only needed for a `dotnet run` layout.
(cd src/Chronicleaf.Web && npm ci && npm run build)

# 2. PostgreSQL. The single database the application uses; the `chronicleaf` role is created
#    by the image when POSTGRES_USER is set.
docker run -d --name chronicleaf-db \
  -e POSTGRES_USER=chronicleaf \
  -e POSTGRES_PASSWORD=change-me \
  -e POSTGRES_DB=chronicleaf \
  -p 5432:5432 postgres:17.6-alpine

# 3. Operator values. The access token is generated once, the plaintext is saved to a
#    file the operator keeps, and only the SHA-256 hex reaches the API. A token whose
#    plaintext is lost cannot sign in; the hash alone is not a credential.
umask 177
ACCESS_TOKEN="$(openssl rand -base64 48)"
printf '%s' "$ACCESS_TOKEN" > ~/.chronicleaf-access-token
chmod 600 ~/.chronicleaf-access-token
export Chronicleaf__AccessTokenHash="$(printf '%s' "$ACCESS_TOKEN" | sha256sum | cut -d' ' -f1)"
unset ACCESS_TOKEN

export ConnectionStrings__Chronicleaf='Host=127.0.0.1;Port=5432;Database=chronicleaf;Username=chronicleaf;Password=change-me'
export Chronicleaf__PublicOrigin='http://127.0.0.1:5080'

# 32 bytes, outside the repository and outside any image:
openssl rand -out /run/chronicleaf-encryption.key 32
chmod 600 /run/chronicleaf-encryption.key
export Chronicleaf__EncryptionKeyFile=/run/chronicleaf-encryption.key

# 4. Migrate, then provision the singleton installation row. The row is operator state,
#    never self-claimed by a visitor; without it, /api/ready reports database_unavailable
#    while /api/health stays 200.
dotnet tool restore
dotnet ef database update --project src/Chronicleaf.Infrastructure --startup-project src/Chronicleaf.Api
docker exec -i chronicleaf-db psql -U chronicleaf -d chronicleaf -c \
  "INSERT INTO installation (id, schema_version, created_at) VALUES (gen_random_uuid(), 1, now()) ON CONFLICT (id) DO NOTHING;"

# 5. Publish the API with the web client in place, then run it. scripts/publish.sh does
#    the web build, the dotnet publish, and the wwwroot copy in one step. The dll must be
#    started with the publish directory as the working directory: the web root resolves to
#    `<cwd>/wwwroot`, so running it from anywhere else serves the page as 503
#    web_assets_unavailable even though the build exists on disk. (The alternative is
#    setting `Chronicleaf__WebRootPath` to the publish `wwwroot`.)
bash scripts/publish.sh dist/run
cd dist/run
ConnectionStrings__Chronicleaf="$ConnectionStrings__Chronicleaf" \
Chronicleaf__AccessTokenHash="$Chronicleaf__AccessTokenHash" \
Chronicleaf__PublicOrigin="$Chronicleaf__PublicOrigin" \
Chronicleaf__EncryptionKeyFile="$Chronicleaf__EncryptionKeyFile" \
  dotnet Chronicleaf.Api.dll --urls http://127.0.0.1:5080
```

Open `http://127.0.0.1:5080/` in a browser; the sign-in page asks for the token whose
plaintext is in `~/.chronicleaf-access-token`. `curl` callers use
`POST /api/session` with body `{"accessToken":"<the plaintext>"}` and the
`Origin: http://127.0.0.1:5080` header; the response is `204 No Content` and the session
cookie is set in the same call. Concretely, keeping cookies across calls:

```sh
printf '{"accessToken":"%s"}' "$(cat ~/.chronicleaf-access-token)" > /tmp/login.json
curl -X POST http://127.0.0.1:5080/api/session \
  -H 'Content-Type: application/json' -H 'Origin: http://127.0.0.1:5080' \
  -c /tmp/cookies.txt -b /tmp/cookies.txt --data-binary @/tmp/login.json
curl -b /tmp/cookies.txt http://127.0.0.1:5080/api/session  # {"authenticated":true,...}
```

A wrong token is one generic `invalid_credentials` (the
code never distinguishes wrong, malformed or rotated), five wrong tries inside fifteen
minutes from one client key turns into `too_many_requests` until the window expires
(durable, so a restart is not a fresh allowance). A database that is unreachable makes
`/api/ready` 503 while `/api/health` stays 200. Migrations are applied by the operator,
never on HTTP startup. Schema is `hangfire` plus the EF-managed tables in the same
database. A missing access-token-hash prevents a non-Development start; in Development the
process starts but no login can ever succeed because there is no token to match.

`scripts/verify.sh` is the configured gate for the change-authoring workflow
(`.project.json` `verification.command`); it builds, runs the .NET stages against a real
disposable PostgreSQL 17.6 container, then runs `npm ci` and the web build. Every filtered
stage asserts it selected at least one test, because `dotnet test --filter` exits 0 when a
pattern matches nothing — a mistyped filter would otherwise be a silent green no-op. The
browser stage is opt-in because it downloads Playwright browsers, and the screenshot
sandbox is refused with `capture_sandbox_unavailable` on a host without the kernel
isolation that production confinement needs (Docker with `--cap-add=NET_ADMIN`, a
user-namespace mapping, and an outbound proxy on the loopback); the panel names the code
and draws no candidate.

See [requirements](requirement.md), the [complete package/dependency map](ROADMAP.md),
[handoff](HANDOFF.md), and [agent workflow](AGENTS.md). Optional semantic retrieval and Hosted
Managed AI have explicit unresolved product-policy blockers; MVP does not depend on them. License:
AGPL-3.0-only, full LICENSE installed; publication is pending explicit repository visibility
selection and has not been performed. `.standard/` remains the original pinned
`baseline-service@1.1.0` snapshot; its Compose and test-only verification are not product deployment
or meaningful test evidence. `docs/assets/capture-plan.md` records the real UI captures, their
revision and their privacy review.
