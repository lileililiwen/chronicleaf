# Runtime shell design

## Context and boundary
Chronicleaf repository, C# .NET 10 process with React/TypeScript/Vite static client. No common /lib code added or sibling modified. Adopt Forge skeleton; preserve standard snapshots and use local MSBuild opt-out. Shared storage decision is deferred to its owning package, not reinvented here.

## Decisions
Move root Program.cs and csproj to src/Chronicleaf.Api/Chronicleaf.Api.csproj; add Chronicleaf.sln and tests/Chronicleaf.Api.Tests with xUnit/Microsoft.AspNetCore.Mvc.Testing matching .NET 10. Program exposes partial Program for WebApplicationFactory. No domain layer until domain capability exists. Root global.json pins .NET 10 feature band.

src/Chronicleaf.Web contains package.json, package-lock.json, tsconfig.json, vite.config.ts, index.html and src/main.tsx. Use React/TypeScript/Vite with exact supported dependency versions resolved and locked at implementation. npm run build performs tsc then vite build. Add test:e2e script invoking Playwright for actual browser shell fixtures; package-local feature test selectors will use this script. App renders Chronicleaf and the tagline, with a visible “Project setup coming in a later change” state. No simulated milestones. No credential inputs. Development Vite proxies /api to the API; production publish includes built Web/dist in Api/wwwroot via an explicit build/publish step, never a machine path.

GET /api/health returns 200 application/json {"status":"ok","service":"chronicleaf"}; no runtime secrets, provider health, version claims or database access. Unknown /api/* paths return JSON 404 {"code":"not_found"}; never SPA HTML. Non-API client routes return index.html; unknown asset paths return 404. If static build is absent, / returns 503 application/json {"code":"web_assets_unavailable"}, while health still returns 200. Health is liveness, not dependency readiness. No CORS opening, remote access control or auth claim in this package.

Update Dockerfile with Node build stage, .NET 10 publish and non-root runtime; pin selected image versions at implementation and expose 8080. Build consumes only this repository. Add scripts/verify.sh to build API/test and npm ci/build; GitHub/network-dependent restore failures are recorded, not bypassed. Configure .project.json verification.command to this script after meaningful tests exist. Do not modify .standard snapshot without Forge upgrade/ownership review.

## Failure, compatibility and rollback
Missing SDK/Node fails preflight; package restore failure fails verification. No migrations/state to roll back. Reverting this package restores greeting scaffold. Remove only superseded tracked scaffold paths in the scoped implementation commit. No approval/publication workflow, persistence or credentials exist yet.

## Traceability
| Requirement | Decision/boundary | Scenarios | Tasks | Oracle |
|---|---|---|---|---|
| SHELL-001 | Api Program health/404 | health, unknown API | 2.1,3.1,4.1 | WebApplicationFactory JSON assertions |
| SHELL-002 | Web entry/static hosting | app, missing build, asset | 2.2,3.2,4.2 | browser text + API status/content tests |
| SHELL-003 | native scripts/Docker | isolated checkout, failed build | 2.3,3.3,4.3 | build from temp clone without siblings + container HTTP |
