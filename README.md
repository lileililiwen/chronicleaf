# Chronicleaf

> Turn project milestones into publishable stories.

把开发进展自动变成可发布的内容资产。面向独立开发者、开源维护者、AI Coding 用户和小型开发工作室。

## Status

Five of the 53 dependency-ordered OpenSpec packages are implemented, verified and archived:
`community-foundation` (runtime shell), `community-persistence` (PostgreSQL data foundation),
`community-access` (single-user owner sessions), `projects-and-milestones` (Project and Milestone
records with a real web UI) and `background-jobs` (durable bounded operations on Hangfire).

What exists today: a .NET 10 API that serves a credential-free `/api/health` and an honest
`/api/ready`, hosts the built React client, persists installation/audit/outbox/projects/milestones/
operations in a real PostgreSQL through EF Core migrations, requires an owner session with origin and
antiforgery checks on every mutation, and executes background work with bounded retries, leases and
cancellation. The web UI covers login/logout, a dashboard, projects, milestone editing with
human-only confirm/dismiss, and an operations panel.

What does not exist yet: GitHub integration, Evidence Bundles, screenshot capture, media storage, the
credential vault, AI generation, Markdown drafts, review/approval, export and the Milestone
Timeline. The first complete workflow (GitHub → confirmed Milestone → screenshot → grounded Markdown
→ human approval → export) is therefore not deliverable yet, and deployment is still not claimed.

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
running Docker daemon because the persistence, access, project and job scenarios run against a real
disposable PostgreSQL 17.6 container; an in-memory store is not accepted as evidence, and an
unavailable database fails the run rather than skipping it. The browser stage is opt-in because it
downloads Playwright browsers.

## Running it

```sh
docker run -d --name chronicleaf-db -e POSTGRES_PASSWORD=… -e POSTGRES_DB=chronicleaf -p 5432:5432 postgres:17.6-alpine
export ConnectionStrings__Chronicleaf='Host=127.0.0.1;Port=5432;Database=chronicleaf;Username=postgres;Password=…'
export Chronicleaf__AccessTokenHash="$(printf '%s' "$(openssl rand -base64 48)" | sha256sum | cut -d' ' -f1)"
export Chronicleaf__PublicOrigin='http://127.0.0.1:5080'
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
