# Chronicleaf

> Turn project milestones into publishable stories.

把开发进展自动变成可发布的内容资产。面向独立开发者、开源维护者、AI Coding 用户和小型开发工作室。

## Status

Ten of the 53 dependency-ordered OpenSpec packages are implemented, verified and archived:
`community-foundation` (runtime shell), `community-persistence` (PostgreSQL data foundation),
`community-access` (single-user owner sessions), `projects-and-milestones` (Project and Milestone
records with a real web UI), `background-jobs` (durable bounded operations), `local-media-storage`
(private media on `Platform.Storage`), `platform-jobs-adoption` (the same, on `Platform.Jobs`),
`milestone-evidence` (immutable evidence bundles), `credential-vault` (encrypted BYOK secrets) and
`voice-profiles` (owner voice, snapshots, deterministic findings). The tenth plus the adoption change
are tracked in ROADMAP.md; the adoption change is not one of the original 53 and exists because
`background-jobs` had been built on a false premise about platform package availability.

What exists today: a .NET 10 API serving a credential-free `/api/health` and an honest `/api/ready`,
hosting the built React client, persisting installation/audit/outbox/projects/milestones/evidence/
operations/credentials/voices in a real PostgreSQL through EF Core migrations, requiring an owner
session with origin and antiforgery checks on every mutation, executing background work through
`Platform.Jobs` on Hangfire with bounded retries, leases and cancellation, and storing credentials as
AES-256-GCM ciphertext under an operator-supplied key that is never in the database, the repository
or the image. The web UI covers login/logout, a dashboard, projects, milestone editing with
human-only confirm/dismiss, evidence bundles with visible unavailable sources, credential settings
that never display a stored value, an operations panel, and voice profiles with actionable findings.
Some guarantees are enforced by PostgreSQL rather than by application convention: evidence is
append-only by trigger, and four check constraints make it impossible to record a collected fact that
was not collected.

What does not exist yet: GitHub integration, screenshot capture, AI generation, Markdown drafts,
review/approval, export, and the Milestone Timeline. The first complete workflow (GitHub → confirmed
Milestone → screenshot → grounded Markdown → human approval → export) is therefore **not** deliverable
yet, and `deployable` remains `false`.

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

```sh
docker run -d --name chronicleaf-db -e POSTGRES_PASSWORD=… -e POSTGRES_DB=chronicleaf -p 5432:5432 postgres:17.6-alpine
export ConnectionStrings__Chronicleaf='Host=127.0.0.1;Port=5432;Database=chronicleaf;Username=postgres;Password=…'
export Chronicleaf__AccessTokenHash="$(printf '%s' "$(openssl rand -base64 48)" | sha256sum | cut -d' ' -f1)"
export Chronicleaf__PublicOrigin='http://127.0.0.1:5080'
# 32 bytes, outside the repository and outside any image:
umask 177 && openssl rand -out /run/chronicleaf-encryption.key 32
export Chronicleaf__EncryptionKeyFile=/run/chronicleaf-encryption.key
dotnet tool restore
dotnet ef database update --project src/Chronicleaf.Infrastructure --startup-project src/Chronicleaf.Api
dotnet run --project src/Chronicleaf.Api --urls http://127.0.0.1:5080
```

The access token itself is never stored: `Chronicleaf__AccessTokenHash` is the SHA-256 hex of a
token you keep. A missing hash prevents a non-Development start; a database that is unreachable makes
`/api/ready` 503 while `/api/health` stays 200. Migrations are applied by the operator, never on HTTP
startup. Schema is `hangfire` plus the EF-managed tables in the same database.

See [requirements](requirement.md), the [complete package/dependency map](ROADMAP.md),
[handoff](HANDOFF.md), and [agent workflow](AGENTS.md). Optional semantic retrieval and Hosted
Managed AI have explicit unresolved product-policy blockers; MVP does not depend on them. License:
AGPL-3.0-only, full LICENSE installed; publication is pending explicit repository visibility
selection and has not been performed. `.standard/` remains the original pinned
`baseline-service@1.1.0` snapshot; its Compose and test-only verification are not product deployment
or meaningful test evidence. `docs/assets/capture-plan.md` records the real UI captures, their
revision and their privacy review.
