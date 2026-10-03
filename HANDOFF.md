current_spec: llm-provider-contract

# Chronicleaf handoff

## Current state — 2026-10-03

Ten changes implemented, verified and archived, each with exactly two commits (implementation/
tests/archive, then this HANDOFF-only pointer):

| # | Change | Implementation commit | Canonical spec |
|---|---|---|---|
| 1 | `community-foundation` | `ee1da62` | `openspec/specs/runtime-shell/spec.md` |
| 2 | `community-persistence` | `29fa886` | `openspec/specs/community-persistence/spec.md` |
| 3 | `community-access` | `0b19963` | `openspec/specs/community-access/spec.md` |
| 4 | `projects-and-milestones` | `065dd8e` | `openspec/specs/projects-and-milestones/spec.md` |
| 5 | `background-jobs` | `5154b26` | `openspec/specs/background-jobs/spec.md` |
| 6 | `local-media-storage` | `77fa203` | `openspec/specs/local-media-storage/spec.md` |
| 7 | `platform-jobs-adoption` | `b1ff0f6` | `openspec/specs/platform-jobs-adoption/spec.md` |
| 8 | `milestone-evidence` | `70419de` | `openspec/specs/milestone-evidence/spec.md` |
| 9 | `credential-vault` | `9d1e3c8` | `openspec/specs/credential-vault/spec.md` |
| 10 | `voice-profiles` | `fd4a6bd` | `openspec/specs/voice-profiles/spec.md` |

Each package is archived under `openspec/changes/archive/2026-10-0*-<name>/`. No `--skip-specs` was
ever used. `platform-jobs-adoption` was authored during this batch (proposal/design/tasks/specs)
after the `background-jobs` deviation below was found, and validated before implementation.

## Platform packages — read this before touching any Platform.* dependency

`Platform.*` 0.1.0 packages are real, built, and available. The authoritative list is
`/home/paul/code/dotnet-platform-libs/artifacts/packages/` (76 nupkgs). **An earlier agent
incorrectly reported them as unavailable** and rebuilt a Platform contract locally; do not repeat
that. Ruling 12 of `/home/paul/code/ARCHITECTURE.md` (~lines 485-515) decides how a consumer takes
them, and two sibling repos already follow it:

- a **repo-relative** feed directory with the `.nupkg` files **committed** — chronicleaf's is
  `packages/platform-feed/`;
- `nuget.config` with `<clear />`, that relative path, and nuget.org;
- **no absolute sibling path in any build-time config, and the `NUGET_PLATFORM_FEED` environment
  variable is explicitly rejected** — an env var a CI runner lacks is the same failure class as a
  hard-coded path;
- `scripts/pack-platform.sh` is the one place a sibling path may appear; it is maintenance only and
  nothing in the build calls it;
- `scripts/verify-platform-feed.sh` runs as the first stage of `scripts/verify.sh` and fails when
  the committed feed does not match what the build resolves.

SHELL-003 is satisfied by the committed feed, and was proven in both directions by the parent: a
fresh `/tmp` copy with no `.git`, no `bin`/`obj`, no sibling and an empty `NUGET_PACKAGES` builds
with 0 warnings and 0 errors, and fails `NU1301` exit 1 when only `packages/platform-feed` is
removed.

**Resolved deviation — `platform-jobs-adoption` (commit `b1ff0f6`, owner-authorized 2026-10-03).**
`background-jobs` had used raw `Hangfire.Core`/`Hangfire.PostgreSql` with a Chronicleaf-owned
dispatcher because it was told `Platform.Jobs` was unavailable. The same feed also contains
`Platform.Jobs` and `Platform.Jobs.Hangfire` 0.1.0, so a dedicated change adopted them: enqueue is now
`Platform.Jobs.IJobDispatcher` via the adapter's `HangfireJobDispatcher`, routing is
`IJobPayloadHandler` invoked by `HangfireJobExecutor`, and time is `Platform.Core.Time.IClock`. The
commit's primary oracle was that **both** `BackgroundJobsTests.cs` and `BackgroundJobsApiHost.cs` are
byte-unchanged — 12/12 passing on three consecutive runs against the platform adapter, with no
assertion edited. Operation state stays Chronicleaf-owned, no migration was added, and Hangfire moved
1.8.21 → 1.8.23 with no observable default change. Two design statements are corrected in that
commit: the operation id arrives as a `string`, not a `JsonElement`, and `AddPlatformHangfireJobs`
**does** support Chronicleaf's PostgreSQL schema.

Known artifacts to expect, not chase: `git diff --check` reports a trailing blank line for
OpenSpec-generated canonical specs — all seven of `openspec/specs/*/spec.md` end that way, so it is
the tool's archive output, not a hand-editing defect. `dotnet ef database update` cannot run in this
environment (its design-time graph wants an uncached `Dapper.AOT` 1.0.48), so container smokes
migrate through the application's own `ChronicleafDbContext` in a `/tmp` project.

Delivered: the .NET 10 runtime shell with a real React/TypeScript/Vite client; the
Core/Application/Infrastructure layering over EF Core + Npgsql with installation, audit_events and
outbox, `IUnitOfWork.ExecuteAsync` under READ COMMITTED, operator-run forward migrations, the
`{code,message,traceId,fieldErrors?}` envelope and honest `/api/health` vs `/api/ready`; the
single-user owner boundary with hashed opaque sessions, constant-time validation, origin and
per-session antiforgery, a durable login throttle and startup revocation on token-hash change;
Projects and Milestones as persisted records with structurally human-controlled status, SQL
compare-and-set revisions, soft archive and a real product UI; durable background operations on
Hangfire with PostgreSQL storage, SQL-enforced exactly-one-result, bounded retries, leases,
cancellation and supersede; and private media on `Platform.Storage`/`Platform.Storage.Local` with
server-generated keys, two-phase upload/promote, traversal and symlink containment, and
`media_in_use`; and immutable, revisioned Evidence Bundles where append-only and honesty are
enforced by the database itself.

**Credential safety is structural.** Credentials are AES-256-GCM sealed with a 32-byte key read
per operation from an operator-mounted file that exists in neither the database, the repository nor
the image, with associated data binding scope, provider, credential id and key version. A row moved
to another scope or opened under an older key version does not decrypt. The absence of plaintext is
proven by reading the raw `bytea` columns and searching for the sentinel **unaligned**, because a
secret need not be aligned inside a ciphertext. The panel cannot issue a write until the read that
publishes the credential's revision has landed, so a save can never be constructed against a state
the client has not read - which is the defect that left a 41-character secret sitting in a DOM input
after a refused write.

**The gate can no longer lie by matching nothing.** `dotnet test --filter` with a pattern that
selects no tests exits 0, so a mistyped filter turns a whole suite into a silent no-op reporting
success — it already happened with `VoiceProfileTests` (the class is `VoiceProfilesTests`;
singular matched 0, plural matches 22). `scripts/verify.sh` now asserts every filtered stage selects
at least one test and fails loudly otherwise, and each stage prints its own count. This closes the
failure class, not the one instance.

**A voice finding never edits text.** Forbidden terms, emoji, title length and language script are
checked deterministically with per-cluster NFC folding and case-insensitive comparison, and each
finding carries `{rule, field, start, length, excerpt, term, normalizedTerm, message}` — a span into
the caller's own text, not a boolean. A draft's voice is a value snapshot written in the same INSERT
that creates it, so editing a profile later cannot reach it. Approval is refused while findings
remain, and the owner's bytes are proven identical before and after the check.

**What the database now guarantees, not just the code.** Evidence is append-only by PostgreSQL
triggers that refuse `UPDATE`/`DELETE` on bundles and items from any writer, and a collected fact
cannot be claimed because four check constraints refuse an `unavailable` item with no reason, an
`unavailable` item with media, a `partial` item naming no exclusion, and a `complete` item with no
text. Bundle revision is decided by `ON CONFLICT (milestone_id, revision) DO NOTHING RETURNING id`,
so two writers on one expected revision produce exactly one committed bundle rather than a merge,
and idempotent append is fact identity `(milestoneId, kind, sourceRef, sourceRevision,
payloadSha256)` over a canonical JSON hash, so arrival order cannot change it. `milestone-evidence`
also implemented the real `IMediaReferenceChecker` that `local-media-storage` deliberately left as a
seam, so `media_in_use` now actually holds for referenced media.

Evidence, all re-run by the parent session rather than taken on report: `CHRONICLEAF_E2E=1 bash
scripts/verify.sh` exit 0 with the platform-feed stage, 15/15 shell, 24/24 persistence, 23/23 access,
19/19 project and milestone, 12/12 jobs, 9/9 media, 14/14 evidence, 32/32 credential vault, `npm ci`,
`tsc` + `vite build` and 34/34 Playwright across four database-isolated browser suites against the
published app with a real PostgreSQL; `scripts/e2e.sh` green on seven runs, five of them on the exact
final code of the last package, whose first run exposed three real UI bugs; `scripts/e2e.sh` 4/4 clean here after 5/5 by the implementer -
nine consecutive green runs of a suite that had been failing about half the time; `BackgroundJobsTests` 12/12 on three
consecutive runs before and after the platform swap (41s, 41s, 41s) to show the lease and two-worker
scenarios are not flaky; the feed check exits 0 and exits 1 with the feed removed; the isolated
no-sibling build proven in both directions; container smokes for media and jobs; name preflight exit
0; `openspec validate --all --strict --no-interactive` 53/53, then 54/54 after
`platform-jobs-adoption` was authored, before and after every archive.

Two real defects were caught by that independent verification and fixed before their commits:
`LeaseHeartbeat` disposed both of its cancellation sources when `StartAsync` returned, which threw
`ObjectDisposedException` on every worker exit path and left a transient failure stuck in `running`
forever (8 of 12 job tests failing); and the platform-jobs swap initially let the adapter create its
own PostgreSQL storage, which leaked an undisposable pool and broke 19 unrelated project/milestone
tests with `53300 sorry, too many clients already`. Neither was worked around by editing a test.

Remaining verification gaps, stated rather than implied: mobile-viewport capture, TLS
termination, a second process against the same Community database, destructive `Down` migrations,
Kestrel request-body limits, concurrency beyond two writers, the lost-lease cooperative cancel (no
test waits long enough for a 15s beat to fire), no upload UI for media, no scheduler for the media
orphan reconciler, and `media-smoke.sh` not exercised in CI. Deployment stays `deployable: false`;
there is no remote, no push and no publication.

Next: implement `llm-provider-contract` only. All four of its dependencies — `credential-vault`, `background-jobs`,
`voice-profiles` and `milestone-evidence` — are now archived, so it is the next
ready package and the keystone for the provider layer. Once it lands, the unblocked
set is `github-evidence` and
`screenshot-capture` (both need `milestone-evidence`, now archived) and then
`llm-provider-contract` → `llm-openai-adapter` → `grounded-markdown-drafts` →
`screenshot-curation` → `markdown-review-export` → `community-compose-release`, which is the first
complete workflow. `milestone-evidence` being archived also means `voice-profiles` and
`llm-provider-contract` now have a second, not sole, dependency met. Read only that package, keep one
`current_spec`, implement, verify locally, strictly validate, archive without `--skip-specs`, then
make the implementation commit and the HANDOFF-only pointer commit, and stop. Do not push.

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
