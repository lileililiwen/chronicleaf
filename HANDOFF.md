current_spec: local-media-storage

# Chronicleaf handoff

## Current state — 2026-10-02

Five changes implemented, verified and archived, each with exactly two commits (implementation/
tests/archive, then this HANDOFF-only pointer):

| # | Change | Implementation commit | Canonical spec |
|---|---|---|---|
| 1 | `community-foundation` | `ee1da62` | `openspec/specs/runtime-shell/spec.md` |
| 2 | `community-persistence` | `29fa886` | `openspec/specs/community-persistence/spec.md` |
| 3 | `community-access` | `0b19963` | `openspec/specs/community-access/spec.md` |
| 4 | `projects-and-milestones` | `065dd8e` | `openspec/specs/projects-and-milestones/spec.md` |
| 5 | `background-jobs` | `5154b26` | `openspec/specs/background-jobs/spec.md` |

Each package is archived under `openspec/changes/archive/2026-10-02-<name>/`. No `--skip-specs`
was ever used.

Delivered: the .NET 10 runtime shell with a real React/TypeScript/Vite client; the
Core/Application/Infrastructure layering over EF Core + Npgsql with installation, audit_events and
outbox, `IUnitOfWork.ExecuteAsync` under READ COMMITTED, operator-run forward migrations, the
`{code,message,traceId,fieldErrors?}` envelope and honest `/api/health` vs `/api/ready`; the
single-user owner boundary with hashed opaque sessions, constant-time validation, origin and
per-session antiforgery, a durable login throttle and startup revocation on token-hash change;
Projects and Milestones as persisted records with structurally human-controlled status, SQL
compare-and-set revisions, soft archive and a real product UI; and durable background operations
on Hangfire with PostgreSQL storage, SQL-enforced exactly-one-result, bounded retries, leases,
cancellation and supersede.

Evidence, all re-run by the parent session rather than taken on report: `CHRONICLEAF_E2E=1 bash
scripts/verify.sh` exit 0 with 15/15 shell, 24/24 persistence, 23/23 access, 19/19 project and
milestone, 12/12 jobs, `npm ci`, `tsc` + `vite build` and 21/21 Playwright against the published
app with a real PostgreSQL; `BackgroundJobsTests` 12/12 on three consecutive runs (39s, 40s, 39s)
to show the lease and two-worker scenarios are not flaky; name preflight exit 0; `openspec
validate --all --strict --no-interactive` 53/53 before and after every archive.

A real defect was caught by that independent verification and fixed before its commit:
`LeaseHeartbeat` disposed both of its cancellation sources when `StartAsync` returned, which
threw `ObjectDisposedException` on every worker exit path and left a transient failure stuck in
`running` forever. It reproduced as 8 of 12 job tests failing. The lifetime rule is now explicit
and the heartbeat hands out its own lease-lost token, so the documented cooperative cancellation
is actually reachable. No test was edited to make it pass.

Remaining verification gaps, stated rather than implied: mobile-viewport capture, TLS
termination, a second process against the same Community database, destructive `Down` migrations,
Kestrel request-body limits, concurrency beyond two writers, and the lost-lease cooperative cancel
(no test waits long enough for a 15s beat to fire). Deployment stays `deployable: false`; there is
no remote, no push and no publication.

Next: implement `local-media-storage` only. It is ready (its dependencies are
`community-persistence`, `community-access` and `projects-and-milestones`, all archived) and it is
the next package on the MVP critical path, because `milestone-evidence` — and through it
`github-evidence`, `screenshot-capture` and `voice-profiles` — depends on it. `credential-vault`
is also still ready and is what unblocks `llm-provider-contract`. Read only that package, keep one
`current_spec`, implement, verify locally, strictly validate, archive without `--skip-specs`, then
make the implementation commit and the HANDOFF-only pointer commit, and stop. Do not push.
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
