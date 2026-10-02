# Chronicleaf

> Turn project milestones into publishable stories.

把开发进展自动变成可发布的内容资产。面向独立开发者、开源维护者、AI Coding 用户和小型开发工作室。

## Status

Forge-generated skeleton; product requirements and 53 dependency-ordered OpenSpec packages are specified but not implemented. No GitHub integration, screenshot capture, AI generation, persistence, editor or export is implemented. Not deployable.

## MVP

GitHub repository → human-confirmed Milestone → commit/diff evidence → project URL screenshot → grounded Markdown draft → human editing and approval → export; include a Milestone Timeline. React web UI is the primary experience.

## Stack and local verification

Target: ASP.NET Core 10, React + TypeScript + Vite, PostgreSQL, Playwright for .NET, Hangfire, BYOK. Community deployment eventually uses one application and one PostgreSQL database with filesystem media; no Redis or message broker.

```sh
dotnet build
dotnet run --urls http://127.0.0.1:5080
node scripts/check-openspec-change-names.mjs
openspec validate --all --strict --no-interactive
```

The current endpoint `/` returns a scaffold greeting only. There is no UI or database setup yet. Dockerfile builds and runs this skeleton, not the MVP. Forge's selected `aspnet-web@0.1.0` assets targeted .NET 8; this project explicitly targets .NET 10. `.standard/` remains the original pinned `baseline-service@1.1.0` snapshot; its Compose and test-only verification are not product deployment or meaningful test evidence.

See [requirements](requirement.md), the [complete package/dependency map](ROADMAP.md), [handoff](HANDOFF.md), and [agent workflow](AGENTS.md). Optional semantic retrieval and Hosted Managed AI have explicit unresolved product-policy blockers; MVP does not depend on them. License choice: AGPL-3.0-only, full LICENSE installed; publication is pending explicit repository visibility selection.
