# Proposal: Community runtime shell

## Why
The greeting scaffold provides no API contract or React application. Establish a portable single-process runtime shell before persistence or product integrations.

## What Changes
Add a .NET 10 API health contract, React/TypeScript/Vite application shell, native build verification and single-process static hosting.

## Package Boundary and Split Assessment
One outcome: a standalone Community application shell with health and browser entry point. No database, authentication, jobs or product state in this package. These have independent lifecycles and are split.

The complete dependency map now lives in [ROADMAP.md](../../../ROADMAP.md). community-persistence owns database/common API contracts; community-access owns single-user sessions; credential-vault owns secrets; background-jobs owns durable operations; local-media-storage owns scoped media. None is implemented by this runtime shell.

## Sibling and Shared Architecture Reconnaissance

| Candidate | Evidence path/symbol | Reusable code/config/architecture | Compatibility gap | Owner and release boundary | Decision |
|---|---|---|---|---|---|
| Forge | src/generate/mod.rs aspnet-web | deterministic scaffold/standard snapshot | generated .NET 8, target .NET 10 | Forge asset versions | adopt |
| dotnet-platform-libs | README.md Platform.Storage/Local/S3 | storage contracts/adapters | package compatibility/feed not verified | versioned Platform packages | keep local: storage is outside this change; implicit checkout references break portable build |
| workspace-governance | workspace Directory.Build.targets | implicit Platform consumer hook | binds sibling checkout | workspace policy | keep local: explicit opt-out for independent project |

## BFS Impact Map
API process, React entry point, native scripts, Docker build and documentation change. Existing forge.yaml and .standard ownership receipts remain preserved. No database migration, credentials, outbound integration, AI or publisher behavior. Verify health payload, unknown API routes, static routing and missing assets.

## Capabilities
### New Capabilities
- `runtime-shell`: portable API health and React browser entry.

## Non-goals
Persistence, auth, Hangfire, screenshots, GitHub, LLMs, editor, approval, exports, full Compose or publication. Planning only; no implementation or archive in bootstrap.
