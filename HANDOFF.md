current_spec: projects-and-milestones

# Chronicleaf handoff

## Current state — 2026-10-02

Implemented and archived, in order:
1. `community-foundation` → commit `ee1da62`, archived as
   `openspec/changes/archive/2026-10-02-community-foundation/`, canonical spec
   `openspec/specs/runtime-shell/spec.md` (SHELL-001/002/003).
2. `community-persistence` → commit `29fa886`, archived as
   `openspec/changes/archive/2026-10-02-community-persistence/`, canonical spec
   `openspec/specs/community-persistence/spec.md`
   (COMMUNITY-PERSISTENCE-001/002).
3. `community-access` → commit `0b19963`, archived as
   `openspec/changes/archive/2026-10-02-community-access/`, canonical spec
   `openspec/specs/community-access/spec.md` (COMMUNITY-ACCESS-001/002).

Delivered so far: the .NET 10 runtime shell (`GET /api/health` 200 JSON, unknown `/api/*`
→ 404 JSON, SPA fallback, unknown asset → 404, missing build → 503) with a React/TypeScript/Vite
client; the Core/Application/Infrastructure layering over real EF Core + Npgsql with
installation, audit_events and outbox, `IUnitOfWork.ExecuteAsync` committing domain+audit+outbox
under READ COMMITTED, operator-run forward migrations via a repo-local dotnet-ef manifest, the
`{code,message,traceId,fieldErrors?}` envelope, and `GET /api/ready` separating reachable-and-
current schema from `database_unavailable`/`schema_mismatch`; and now the single-user owner
boundary: default-deny `/api` with only health, ready and `POST /api/session` public, a
SHA-256-hashed opaque session cookie (HttpOnly, SameSite=Strict, Secure outside localhost
Development) validated in constant time against a 12h idle / 7d absolute limit and a stable
`installation.owner_actor_id`, generic 401s, origin + per-session antiforgery enforcement on
mutations with 403 before any write, a durable 5-per-15-minutes login throttle returning 429,
and startup revocation of all sessions when the configured token hash changes.

Evidence recorded for `community-access`: `CHRONICLEAF_E2E=1 bash scripts/verify.sh` exit 0
(build, 15/15 shell, 24/24 persistence, 23/23 access against real disposable PostgreSQL,
`npm ci`, `tsc` + `vite build`, 9/9 Playwright against the published app with a real database);
`docker build`/`run` asserting health/ready, anonymous 401, login cookie flags, cross-origin 403
and same-origin logout; name preflight exit 0; `openspec validate --all --strict --no-interactive`
53/53 before and after archive. One shipped persistence assertion was necessarily generalized
because a second forward migration invalidates its hard-coded migration count; the idempotence
property is still asserted on both sides. No EF InMemory store is used anywhere.

Not verified: mobile-viewport capture, Kestrel request-body limits, TLS termination, a second
process against the same Community database, and the destructive `Down` migration. Deployment
stays `deployable: false`; no remote, no push, no publication, and no product claim beyond the
runtime shell, data foundation and owner access boundary.

Next: implement `projects-and-milestones` only. It is the keystone the most remaining MVP
packages depend on (`local-media-storage`, `milestone-timeline`, `voice-profiles`,
`milestone-evidence` and everything downstream of it). `credential-vault` and
`background-jobs` are also dependency-ready now, so they are the follow-on choices. Read only
that package, set one `current_spec`, implement, verify locally, strictly validate, archive
without `--skip-specs`, then make the implementation commit and the HANDOFF-only pointer commit,
and stop. Do not push.

# Bootstrap handoff

2026-09-30: local Forge bootstrap complete; skeleton only, product unimplemented, with 53 active specification packages. No business runtime, frontend, PostgreSQL, browser capture, AI or export implementation. Deployment remains false. main initialized, no commits or remote publication.

Created through workspace Forge binary with `forge --registry <workspace>/.forge/registry.sqlite new chronicleaf --profile aspnet-web --id chronicleaf --name Chronicleaf --standard-pack baseline-service@1.1.0`. Forge registered the id and verified rendered assets. `forge inspect chronicleaf` resolves it. Registry path is invocation-specific, not a production dependency.

Local changes after generation: net10.0, global.json .NET 10, production-shaped skeleton Dockerfile, explicit PlatformConsumerOptOut, governance/requirements/license/initial OpenSpec. `.standard/` and original Forge ownership receipts are preserved. `.project.json` extended after rendering, so its original receipt no longer describes byte-identical generated metadata; use ownership-aware review before any Forge upgrade. Forge maturity L1 reflects generator metadata, not runtime delivery or completed MVP.

Verification:
- From repository root: `dotnet build --nologo` passed, 0 warnings/errors, SDK 10.0.400.
- `node scripts/check-openspec-change-names.mjs` passed.
- `openspec validate --all --strict --no-interactive` bootstrap passed 1/1; after roadmap authoring passed 53/53.
- `git diff --check` passed; bootstrap files remain untracked, so separate content checks are required too.
- Full GNU AGPLv3 text installed in LICENSE. No copyright holder invented.
- GitHub CLI authentication available; ownership/visibility and publication remain undecided. No remote created/push performed.

Initial workspace-root `dotnet build chronicleaf/chronicleaf.csproj` failed NETSDK1045 because SDK discovery used the parent .NET 8 context, also importing Platform.Core implicitly. Build from this repository to honor global.json; local Directory.Build.props opts out of implicit sibling coupling. No SDK rollback to .NET 8.

Not verified: running HTTP endpoint, container build/run, product UI capture, application tests (none exist), Gate pass, workspace metadata policy, remote publication. See docs/assets/capture-plan.md. The .standard test-only check is not meaningful product verification. No deployability claim.

Next: superseded — community-foundation is implemented and archived; see Current state above for the active pointer. Public/private GitHub visibility requires a user choice before publication; full license is present, but no publication readiness claimed.

Each completed OpenSpec spec/change requires exactly two commits: first the implementation/tests/archive commit, then a HANDOFF.md-only pointer/evidence commit.

Implement one change on main, locally verify and strictly validate, archive without --skip-specs, create commit 1, advance the single current_spec pointer or remove it if no next change exists, then commit only HANDOFF.md and stop. Do not request conversational authorization for either commit when implementation is authorized; do not push. Bootstrap did not complete or archive community-foundation.

## OpenSpec authoring evidence — 2026-09-30

Added 52 complete changes from requirement.md and ROADMAP.md; total53 active. Each new package includes proposal, design, tasks and delta specs with stable requirement ids and Success/Failure/Boundary scenarios. ROADMAP.md is the dependency-ordered package map and full requirement coverage index. Four fixed BFS → DFS → BFS → Verification task phases are used; all implementation checkboxes stay unchecked. The existing foundation remains current and was aligned with the split access/persistence/secrets/jobs/media boundaries.

MVP includes native Community shell, PostgreSQL/common error contracts, owner access, secrets, durable tasks, scoped filesystem media, projects/milestones/versioned evidence, private Timeline, bounded GitHub collection, controlled capture/curation, voice, provider contract plus five BYOK adapters, grounded Markdown, human review/export and two-service release verification. POST-MVP/demand/Cloud additions are separate conditional packages, not MVP completion prerequisites.

Two explicit policy blockers:
- semantic-narrative-retrieval: optional demand confirmation, embedding provider/model/dimensions and transfer consent. Owner: Chronicleaf maintainer. Structured Narrative Memory works independently.
- hosted-managed-ai: operator provider/model, quota/unit weights and financial/unknown-usage settlement policy. Owner: Chronicleaf maintainer. Community and Hosted BYOK work independently.

Hashnode transport endpoint/auth/draft mutation was resolved against official provider-owned reference; target capability/role and plan are checked before dispatch. Medium and LinkedIn outputs are reviewed manual content packages, not promises of native transport support. Full video synthesis/TipTap remain considerations with no invented implementation contracts.

Validation: name preflight and strict OpenSpec passed53/53. Package graph is acyclic and dependency-ordered; required artifacts/four phase headings, 107 requirement IDs, 319 observable scenarios, 480 unchecked tasks, 58 local links and untracked-document whitespace checked separately. Provider-shaped fixtures and native commands in tasks are future implementation oracles, not executed runtime tests. No application source, implementation, archive, commits, push or deployment in this authoring step.

Capture contract targets a hardened Linux container backend: dedicated browser UID/supervisor, destination-validating proxy and enforced egress firewall. Compose packaging must verify required capabilities and actual blocked network traffic; native environments without that sandbox report capture_sandbox_unavailable rather than navigate with reduced isolation. This is a future implementation contract, not proven sandbox/runtime evidence.
