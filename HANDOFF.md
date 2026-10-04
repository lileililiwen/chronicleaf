current_spec: llm-openai-adapter

# Chronicleaf handoff

## Current state — 2026-10-04

Thirteen changes implemented, verified and archived, each with exactly two commits (implementation/
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
| 11 | `llm-provider-contract` | `f226b42` | `openspec/specs/llm-provider-contract/spec.md` |
| 12 | `github-evidence` | `429f2b2` | `openspec/specs/github-evidence/spec.md` |
| 13 | `screenshot-capture` | `e890e11` | `openspec/specs/screenshot-capture/spec.md` |

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
OpenSpec-generated canonical specs — all thirteen of `openspec/specs/*/spec.md` end that way, so it is
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
scripts/verify.sh` exit 0 with the platform-feed stage, twelve filtered stages each asserted to select
at least one test, 15/15 shell, 24/24 persistence, 23/23 access, 19/19 project and milestone, 12/12
jobs, 9/9 media, 14/14 evidence, 32/32 credential vault, 22/22 voice profiles, 26/26 LLM provider
contract, 27/27 GitHub evidence, 52/52 screenshot capture — 275 in total — then `npm ci`, `tsc` +
`vite build` and 41/41 Playwright across six database-isolated browser suites against the published
app with a real PostgreSQL; `scripts/e2e.sh` green on seven runs, five of them on the exact
final code of the last package, whose first run exposed three real UI bugs; `scripts/e2e.sh` 4/4 clean here after 5/5 by the implementer -
nine consecutive green runs of a suite that had been failing about half the time; the seventh
database-isolated Playwright suite for screenshot capture is configured (its own disposable
PostgreSQL, its own published application, the panel-side assertion of `capture_sandbox_unavailable`
and the request-time refusal of a private destination) but was not exercised in this session, because
this host has neither Docker nor the Playwright browsers and the suite's deliverable is the visible
refusal rather than a real capture, so a green run there is a `scripts/e2e.sh` invocation away rather
than an evidence line above; `BackgroundJobsTests` 12/12 on three
consecutive runs before and after the platform swap (41s, 41s, 41s) to show the lease and two-worker
scenarios are not flaky; the feed check exits 0 and exits 1 with the feed removed; the isolated
no-sibling build proven in both directions; container smokes for media and jobs; name preflight exit
0; `openspec validate --all --strict --no-interactive` 53/53, then 54/54 after
`platform-jobs-adoption` was authored, then 55/55 after `llm-provider-contract` was archived, then
54/54 with `github-evidence` moved from an active change into a canonical spec, and 54/54 again with
`screenshot-capture` moved the same way, before and after every archive.

**Repository evidence records a revision, not a name.** A project connects a repository by owner and
name and never by a URL; `src/Chronicleaf.GitHub` is a BCL-only leaf project, like `Chronicleaf.AI`,
so the transport is swappable without reaching the product. Every selected ref is resolved to an
immutable SHA *before* any fact is read, and the collected commits, diffs, releases, issues and pull
requests are appended through the evidence contract that already owns append-only storage — so the
four database check constraints that refuse a fabricated fact apply to GitHub output too. Collection
is a durable job on the delivered queue: an idempotent key returns the same operationId, submission is
202 with the operation the owner watches, a 429 waits for its own reset time through
`IOperationRetryScheduler`, and a bundle that moved during the import is superseded rather than
overwritten. Nothing is written to GitHub — the transport exposes no verb that could — the API host is
a constant with redirects refused, and a credential stays a vault reference that never reaches a row,
a log or a second host.

**A background write now refreshes the panel it wrote to.** The evidence panel already re-read on the
owner's own append; what it lacked was any way to learn that *something else* had appended. Until
`github-evidence` that was unreachable, because only the owner could write a bundle. It is now driven
event-driven rather than by another poll: the sync panel reports the transition into a terminal
operation status once, and `MilestoneRow` passes a token into an optional `reloadKey` on the evidence
panel. The evidence panel imports nothing from the GitHub package, and the fixture that proves it was
confirmed by temporarily neutralising the signal and watching the assertion fail on exactly that
revision.

**Defects caught by independent verification, and one environment flake recorded rather than hidden.**
`LeaseHeartbeat` disposed both of its cancellation sources when `StartAsync` returned, throwing
`ObjectDisposedException` on every worker exit path and leaving a transient failure stuck in `running`
(8 of 12 job tests); the platform-jobs swap let the adapter create its own PostgreSQL storage, leaking
an undisposable pool and breaking 19 unrelated tests with `53300 sorry, too many clients already`; the
`llm-provider-contract` canonicaliser behind the SHA-256 consent binding did not recurse into arrays,
so the digest drifted whenever the evidence array reordered; its web panel read evidence once on mount;
`github-evidence`'s inherited migration-readiness widening updated the expected missing-id list but not
the applied set it filters from, leaving that assertion unsatisfiable; and its sync panel read the
connection once on mount. None was worked around by editing a test. Separately, one full gate run
failed the access stage on a Testcontainers class-cleanup `TaskCanceledException` with all 23 tests
passed. It did not reproduce in three isolated runs (23/23, 9s each, no cleanup failure), the stage is
untouched by `github-evidence`, that package registers no hosted service that could hold a connection
open, and the failure occurred with 18 containers live while the stage took 1m6s against 9s in
isolation. It is recorded as an environment teardown flake, not silently re-run until green.

**A measurement error of the parent's, recorded because it nearly hid a failure.** The first full-gate
run of `github-evidence` was piped to `tail`, which masked the real exit code and reported the task
green while `scripts/verify.sh` had in fact exited 1. Every later gate run uses `set -o pipefail` and
prints `GATE_EXIT` explicitly.

**The generation contract is provider-neutral and consented.** A new leaf assembly `Chronicleaf.AI` holds
the `ILLMProvider` contract and zero `ProjectReference`/`PackageReference`, so the five future adapter
packages (`llm-openai-adapter`, `llm-anthropic-adapter`, `llm-gemini-adapter`, `llm-openrouter-adapter`,
`llm-ollama-adapter`) reach a contract without reaching the credential vault, the evidence store or any
domain record. The consent handshake stores `generation_previews(id, scope_id, milestone_id, purpose,
aggregate_ref, snapshot_json, sha256, expires_at)` with a 15-minute TTL, no raw credential, and a SHA-256
over a canonical JSON document whose object members are written out as literals and recursed into
arrays - a canonicaliser that stopped at the top level was a real bug fixed during integration. The
provider request is rebuilt from the saved snapshot bytes at call time, never from fresh database reads,
so any change to evidence, voice or credential revision after consent yields `consent_stale` 409 before
the transport is reached. No provider is implemented: the only `ILLMProvider` is a test double
registered only in test DI, and `Production_registration_contains_no_adapter_and_no_settings_value_can_reach_one`
proves the production composition root cannot reach it. `scripts/verify.sh` has the new
`llm provider contract tests` stage and the new browser suite is the fifth database-isolated run.

Remaining verification gaps, stated rather than implied: mobile-viewport capture, TLS
termination, a second process against the same Community database, destructive `Down` migrations,
Kestrel request-body limits, concurrency beyond two writers, the lost-lease cooperative cancel (no
test waits long enough for a 15s beat to fire), no upload UI for media, no scheduler for the media
orphan reconciler, and `media-smoke.sh` not exercised in CI. For `github-evidence` specifically, the
opt-in real-token run needs outbound network and a real token and was **not** performed, so no claim is
made about authenticated or private-repository behaviour, real large-compare truncation, or real
`Retry-After` handling; its browser collection scenario does reach the real public `api.github.com`
and really did append a bundle revision, and on a host without that route out it fails loudly rather
than being stubbed. For `screenshot-capture` specifically, the request-time half is asserted by the
new `screenshot capture tests` stage against the same real disposable PostgreSQL, the real
temporary media root and the real API host that the other stages use: a private, loopback,
link-local, multicast, reserved or cloud-metadata destination is refused by the URL policy before
a job exists, the policy is consulted again to the address the name resolves to immediately before
every connection - including when the same address is written in IPv6 spelling as an IPv4-mapped
literal such as `::ffff:10.0.0.5`, which the IPv6 rules would otherwise judge a public page - so a
refused destination is refused before bytes are exchanged; the candidate row is written only after
the bytes are verified and registered by the delivered media store, the unique tuple
`(operation_id, source_url, viewport)` is enforced by the database so a retried or resumed job
cannot write a second copy of the same picture, a duplicate retires the second run's media through
the delivered status transition rather than leaving an orphan, and a media-write failure fails the
candidate before any row is published; and the candidates are written as candidates and never
selected, made primary, captioned or appended to evidence by this package, because curation owns
that. The kernel half - that the browser is reached only through an egress proxy whose connection
refusal is enforced by the kernel - is **not** proven on this host, which has no `CAP_NET_ADMIN`, no
user namespaces, no iptables and no nft: the `IScreenshotProvider` substituted into the
integration tests is a legitimate DI seam (the same shape `IGitEvidenceSource` already uses), so no
browser is launched by the new stage and no page is visited, the PNG bytes that exercise media
storage, the candidate row, the PNG dimension check and the digest are a labelled synthesised
fixture rather than a captured page, and the production provider answers
`capture_sandbox_unavailable` before any browser process is started - the panel names the code in
words, draws no candidate and offers nothing that looks like a retry. The browser fixture is
configured as the seventh database-isolated Playwright suite, but `scripts/e2e.sh` was not run for
it in this session: a host with Docker and the Playwright browsers is one green run away, and the
suite asserts the panel-side refusal only - no claim is made that a real page was captured, and a
candidate image is only asserted when the server actually published one. One
`llm-provider-contract.spec.ts` case failed in a single run out of three
(fast, at 1.1s against ~5s when it passes) and passed in the other two; the surface it covers is
untouched by this package and the cause was not established. Deployment stays `deployable: false`;
there is no remote, no push and no publication.

Next: implement `llm-openai-adapter` only. It is the first real BYOK transport, the leaf
`Chronicleaf.AI` already owns the `ILLMProvider` contract it will sit behind, and the only thing
still blocking `grounded-markdown-drafts`. After it, the chain is `grounded-markdown-drafts` →
`screenshot-curation` → `markdown-review-export` → `community-compose-release`, which is the first
complete workflow.

Two working notes for whoever picks this up. First, `github-evidence` was delivered across two
worker passes because the first was **cancelled mid-flight rather than failed**, and it left a
substantial uncommitted implementation behind; the backend was verified as inherited (build 0
warnings, 27/27) and completed rather than restarted. Audit `git status` before assuming a clean tree
whenever a worker does not return `succeeded`, because the expensive mistake here would have been
discarding that work. Second, every package that touched the web UI in this batch shipped a real
browser-only defect that no .NET test could see — a panel that read its data once on mount, a
canonicaliser that did not recurse into arrays, a sync panel that read its connection once on
mount, an evidence panel that missed a background write, and a migration-readiness widening that
left an assertion unsatisfiable. Run `scripts/e2e.sh` as soon as the UI builds, and diff the whole
change surface, not only the files you wrote.

Read only that package, keep one `current_spec`, implement, verify locally, strictly validate, archive
without `--skip-specs`, then make the implementation commit and the HANDOFF-only pointer commit, and
stop. Do not push.

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
