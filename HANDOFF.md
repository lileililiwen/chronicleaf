# Chronicleaf handoff

## Current state — 2026-10-05

Fifty-two changes implemented, verified and archived, each with exactly two commits (implementation/
tests/archive, then this HANDOFF-only pointer). No `current_spec` remains: every drainable change is
archived and the two owner-BLOCKED changes are deferred to ROADMAP for later
(`semantic-narrative-retrieval`: demand plus embedding model/provider/dimension and consent policy;
`hosted-managed-ai`: provider/model/quota/unit weights plus financial settlement policy — both
packages parked intact in `openspec/deferred/`, nothing active remains).

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
| 14 | `llm-openai-adapter` | `777417f` | `openspec/specs/llm-openai-adapter/spec.md` |
| 15 | `grounded-markdown-drafts` | `32f3e6f` | `openspec/specs/grounded-markdown-drafts/spec.md` |
| 16 | `screenshot-curation` | `9c1ce4a` | `openspec/specs/screenshot-curation/spec.md` |
| 17 | `milestone-timeline` | `4cfa8c3` | `openspec/specs/milestone-timeline/spec.md` |
| 18 | `llm-gemini-adapter` | `b5f74ac` | `openspec/specs/llm-gemini-adapter/spec.md` |
| 19 | `llm-anthropic-adapter` | `ab23ace` | `openspec/specs/llm-anthropic-adapter/spec.md` |
| 20 | `llm-openrouter-adapter` | `c51dbd7` | `openspec/specs/llm-openrouter-adapter/spec.md` |
| 21 | `llm-ollama-adapter` | `efed7af` | `openspec/specs/llm-ollama-adapter/spec.md` |
| 22 | `markdown-review-export` | `64d6868` | `openspec/specs/markdown-review-export/spec.md` |
| 23 | `community-compose-release` | `5a40d46` | `openspec/specs/community-compose-release/spec.md` |
| 24 | `local-git-evidence` | `f7e4a81` | `openspec/specs/local-git-evidence/spec.md` |
| 25 | `github-release-candidates` | `08651c4` | `openspec/specs/github-release-candidates/spec.md` |
| 26 | `github-graphql-enrichment` | `c7a97b8` | `openspec/specs/github-graphql-enrichment/spec.md` |
| 27 | `narrative-memory` | `a7d12f4` | `openspec/specs/narrative-memory/spec.md` |
| 28 | `html-content-export` | `aae5181` | `openspec/specs/html-content-export/spec.md` |
| 29 | `content-asset-variants` | `65309ac` | `openspec/specs/content-asset-variants/spec.md` |
| 30 | `screenshot-evolution-images` | `f2916e1` | `openspec/specs/screenshot-evolution-images/spec.md` |
| 31 | `static-blog-export` | `066e153` | `openspec/specs/static-blog-export/spec.md` |
| 32 | `public-project-journey` | `6bc7e8e` | `openspec/specs/public-project-journey/spec.md` |
| 33 | `agency-client-projects` | `764eb0e` | `openspec/specs/agency-client-projects/spec.md` |
| 34 | `cloud-tenant-boundary` | `0a6a935` | `openspec/specs/cloud-tenant-boundary/spec.md` |
| 35 | `team-project-permissions` | `0f47fca` | `openspec/specs/team-project-permissions/spec.md` |
| 36 | `cloud-object-storage` | `bdfcec9` | `openspec/specs/cloud-object-storage/spec.md` |
| 37 | `scheduled-project-sync` | `bfa302a` | `openspec/specs/scheduled-project-sync/spec.md` |
| 38 | `hosted-archive-operations` | `2db6039` | `openspec/specs/hosted-archive-operations/spec.md` |
| 39 | `github-app-connection` | `f5a93d4` | `openspec/specs/github-app-connection/spec.md` |
| 40 | `linkedin-copy-package` | `637a1fc` | `openspec/specs/linkedin-copy-package/spec.md` |
| 41 | `medium-import-package` | `780b6fd` | `openspec/specs/medium-import-package/spec.md` |
| 42 | `publisher-contract` | `8e53d58` | `openspec/specs/publisher-contract/spec.md` |
| 43 | `publisher-github-pages-adapter` | `6a4fb12` | `openspec/specs/publisher-github-pages-adapter/spec.md` |
| 44 | `publisher-devto-adapter` | `b880186` | `openspec/specs/publisher-devto-adapter/spec.md` |
| 45 | `publisher-hashnode-adapter` | `f1d990c` | `openspec/specs/publisher-hashnode-adapter/spec.md` |
| 46 | `publisher-ghost-adapter` | `49707ac` | `openspec/specs/publisher-ghost-adapter/spec.md` |
| 47 | `publisher-wordpress-adapter` | `e99205b` | `openspec/specs/publisher-wordpress-adapter/spec.md` |
| 48 | `publisher-custom-blog-adapter` | `7350a78` | `openspec/specs/publisher-custom-blog-adapter/spec.md` |
| 49 | `evolution-gif-export` | `95350b3` | `openspec/specs/evolution-gif-export/spec.md` |
| 50 | `xiaohongshu-content-package` | `75a8fba` | `openspec/specs/xiaohongshu-content-package/spec.md` |
| 51 | `video-story-scripts` | `cfaacc1` | `openspec/specs/video-story-scripts/spec.md` |
| 52 | `agency-weekly-reports` | `6457c2b` | `openspec/specs/agency-weekly-reports/spec.md` |

Each package is archived under `openspec/changes/archive/2026-10-0*-<name>/`. No `--skip-specs` was
ever used. `platform-jobs-adoption` was authored during this batch (proposal/design/tasks/specs)
after the `background-jobs` deviation below was found, and validated before implementation.

**Release evidence (`community-compose-release`, commit `5a40d46`, all run by the implementer
rather than taken on report).** `bash scripts/verify.sh` exit 0 with the platform-feed stage,
twenty-two filtered stages each asserted to select at least one test (15/15 shell, 24/24
persistence, 23/23 access, 19/19 projects, 12/12 jobs, 9/9 media, 14/14 evidence, 32/32 vault,
22/22 voice, 26/26 provider contract, 23/23 OpenAI, 24/24 Gemini, 24/24 Anthropic, 23/23
OpenRouter, 26/26 Ollama, 27/27 GitHub evidence, 52/52 capture, 18/18 curation, 30/30 drafts,
7/7 timeline, 11/11 review-export, 10/10 compose-release), `npm ci`, `tsc` + `vite build`, all
green. `bash scripts/verify-community.sh` exit 0 against the real image: two-service bring-up,
operator migrate plus installation provision, readiness 200/200, fixture smoke (project →
confirmed milestone → manual evidence → timeline, private capture refused 403 before any job),
a public capture job failed visibly with `capture_sandbox_unavailable`, restart-identical and
backup/restore-identical timeline and evidence heads through destroyed volumes. Name preflight
exit 0; `openspec validate --all --strict --no-interactive` 54/54 before and after the archive,
which used no `--skip-specs`. `deployable` stays `false`: this host cannot confine a browser
so screenshot success is unproven here, and the credential-gated live provider smoke was not
performed. There is no remote, no push and no publication.

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
OpenSpec-generated canonical specs — all fourteen of `openspec/specs/*/spec.md` end that way, so it is
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

Evidence, all re-run by the parent session rather than taken on report: `bash scripts/verify.sh` exit
0 with the platform-feed stage, thirteen filtered stages each asserted to select at least one test,
15/15 shell, 24/24 persistence, 23/23 access, 19/19 project and milestone, 12/12 jobs, 9/9 media, 14/14
evidence, 32/32 credential vault, 22/22 voice profiles, 26/26 LLM provider contract, 23/23 LLM OpenAI
adapter, 27/27 GitHub evidence, 52/52 screenshot capture — 298 in total — then `npm ci`, `tsc` +
`vite build`, all green. The full run for the last package was `CHRONICLEAF_E2E=1 bash scripts/verify.sh`
with 41/41 Playwright across six database-isolated browser suites against the published app with a real
PostgreSQL; `scripts/e2e.sh` green on seven runs, five of them on the exact
final code of the github-evidence package, whose first run exposed three real UI bugs; `scripts/e2e.sh` 4/4 clean here after 5/5 by the implementer -
nine consecutive green runs of a suite that had been failing about half the time; the seventh
database-isolated Playwright suite for screenshot capture is configured (its own disposable
PostgreSQL, its own published application, the panel-side assertion of `capture_sandbox_unavailable`
and the request-time refusal of a private destination) but was not exercised, because the suite's
deliverable is the visible refusal rather than a real capture and a green run there is a
`scripts/e2e.sh` invocation away rather than an evidence line above; `BackgroundJobsTests` 12/12 on
three consecutive runs before and after the platform swap (41s, 41s, 41s) to show the lease and
two-worker scenarios are not flaky; the feed check exits 0 and exits 1 with the feed removed; the
isolated no-sibling build proven in both directions; container smokes for media and jobs; name preflight
exit 0; `openspec validate --all --strict --no-interactive` 53/53, then 54/54 after
`platform-jobs-adoption` was authored, then 55/55 after `llm-provider-contract` was archived, then
54/54 with `github-evidence` moved from an active change into a canonical spec, 54/54 again with
`screenshot-capture` moved the same way, and 54/54 again with `llm-openai-adapter`, before and after
every archive.

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
`screenshot capture tests` stage against the same real disposable PostgreSQL, the real
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
browser is launched by that stage and no page is visited, the PNG bytes that exercise media
storage, the candidate row, the PNG dimension check and the digest are a labelled synthesised
fixture rather than a captured page, and the production provider answers
`capture_sandbox_unavailable` before any browser process is started - the panel names the code in
words, draws no candidate and offers nothing that looks like a retry. The browser fixture is
configured as the seventh database-isolated Playwright suite, but `scripts/e2e.sh` was not run for
it: a host with Docker and the Playwright browsers is one green run away, and the
suite asserts the panel-side refusal only - no claim is made that a real page was captured, and a
candidate image is only asserted when the server actually published one. For `llm-openai-adapter`
specifically, the substitution is a message handler underneath the adapter's own `HttpClient`, so
every stage above the socket is shipped code - the request the adapter serialised, the header it
built from a real vault lease, the redirect it refused to follow, the 2 MiB cap it enforced while
bytes were arriving, and the status mapping. What is therefore **not** claimed: that OpenAI works, that
any named model exists or is available to any particular owner, or that a real response has any shape
beyond the published reference the fixtures were transcribed from. The credential-gated live smoke the
design names is **not** performed, and no assertion in that stage may be read as evidence that a paid
call was made, that a schema was enforced remotely, or that a call is cheap. The accepted-model
allowlist is empty unless an operator configures one, so an untouched deployment refuses every model
before a credential is leased - that refusal is asserted, and the happy path is asserted only against
a model the allowlist names. One `llm-provider-contract.spec.ts` case failed in a single run out of three
(fast, at 1.1s against ~5s when it passes) and passed in the other two; the surface it covers is
untouched by this package and the cause was not established. Deployment stays `deployable: false`;
there is no remote, no push and no publication.

**A provider is now reachable, and the boundary is structural rather than documented.** The OpenAI
adapter lives in its own project at `src/Chronicleaf.AI/Providers/OpenAI`, which references
`Chronicleaf.AI` and nothing else in the repository — so it cannot reach the credential vault, the
evidence store or any domain record, and every fact it needs arrived inside the `GenerationRequest`
the contract already resolved. The credential crosses a seam the adapter declares
(`IOpenAIApiKeySource`: a key, for a scope, briefly) and only the composition root knows the answer
comes from a leased, sealed, scope-bound credential; the bridge is `VaultOpenAIApiKeySource` in the API
project, and it opens a scope per lease rather than capturing a scoped vault in a singleton. The
outbound request is exactly the consented payload at a fixed origin — model, instruction, evidence as
a labelled data section, the ceiling, `store: false`, the schema as a structured value — and nothing
else: no `tools`, no `tool_choice`, no `stream`, no `previous_response_id`, no `conversation`, no
`include`. The evidence sits in the user message under the heading the instruction already names,
because a prompt whose instruction refers to a section the request does not contain is a lie to the
model. A key is leased, becomes one bearer header, and is returned before the call returns; no
envelope, log line, exception message or result field carries it, a request header or a provider
response body, and the provider's error body is disposed unread in every branch because that is where a
customer's name and occasionally a key sit in plain text. The accepted-model list is configuration
and is empty unless an operator fills it in, so an untouched deployment refuses every model before a
credential is leased — and that refusal is the honest state of a transport nobody has pointed at
anything, not a missing feature. Registration is the whole rollback lever and is not a setting: there
is no flag that un-installs an adapter at runtime, because a provider that silently stops being
reachable in the middle of an owner's workflow is a worse failure than one that was never there.

**A draft is validated before it is prose.** `grounded-markdown-drafts` asks a provider for structure
first — a strict JSON summary where every factual item names the evidence it came from — and validates
that structure against the frozen snapshot *before* any Markdown exists, then asks for the prose from
the accepted summary and the ids it fixed. The order is the requirement and it is also the only order
that works: a model that invents something loses while the invention is still a field a validator can
read, rather than becoming a sentence in a document an owner is about to believe. Phase 1 is bounded at
one repair call and then `invalid_summary`; a grounding failure spends no repair call at all, because
repairing a citation does not make a citation true. The 202 response is a job on the delivered queue,
the outcome is committed as `draft` and there is no approve verb anywhere in the package — approval is
`markdown-review-export`'s, bound to a revision and a body hash.

**The database, not the code, holds the two invariants that matter.** `content_revisions` is
append-only by trigger, and a revision and its asset head move in one statement, so a second generation
appends rather than overwrites and a stale writer is a compare-and-set that loses instead of merging.
Four check constraints refuse a row that is not a digest, that claims evidence revision zero, that
rewrites itself, or that names an evidence item the bundle does not contain. Findings split by what can
be settled locally: an evidence id outside the snapshot and a figure appearing in no evidence item are
fatal, while an unreferenced claim or a claim resting only on unreadable evidence is *reported* against
the stored revision. The read path recomputes the same rules, so what a reader is told cannot depend on
whether they asked before or after the draft was written.

**Three defects this package found, none of them worked around in a test.** A footnote label is `c1`,
not `[^c1]` — the caret is Markdown's sigil, and putting it inside the identifier made every correct
citation look like a reference to nothing. Extracting numerals from prose to check them against evidence
read `[^c1]` as the number 1, which refused every honest body; the citation labels are stripped before
the figures are read. And the browser fixture's seed was writing its `INSERT` through `docker exec`
*without* `-i`, so the heredoc never reached `psql`: the seed reported success, the suite ran, and the
database held no draft at all. That is the same failure class as the filter that matches nothing, one
layer down — a green suite proving nothing — so the seed now reads its own rows back and refuses to
continue unless the one revision it hashed is the one that is stored.

**Three pre-existing assertions that only ever passed by hand.** Three migration-readiness scenarios
each named the newest required migration by value, which is a value somebody has to remember to update
and is why they went red every time a later package added one; they now assert the newest by its
position in the required list and the applied set by membership, which cannot go stale. The provider
fixture asserted that *no* slot reports an installed adapter, which stopped being true when
`llm-openai-adapter` shipped, and the panel's "nothing can be called" notice had been asserting a
statement that was no longer true either; both now derive their claim from the server's own `usable`
catalogue, and the panel says what is actually installed — an adapter for `openai` with no credential
stored.

**A choice is now a state, and a selection is a publication.** `screenshot-curation` moves the
four columns capture left constant - status, caption, primary flag, display order - under a
two-revision precondition: If-Match carries the screenshot revision and the body carries the
evidence revision, and both must be current, because the candidate row and the bundle are written
in one transaction. Selecting appends a screenshot item naming the picture's media and caption;
editing the caption appends a correcting revision naming what it corrects; deselecting appends a
revision that no longer carries the picture while earlier bundles keep their rows, so approved
historical content still reads what it was approved against. One primary per milestone is a
partial unique index with the old flag cleared before the new one is set in the same transaction;
an order must name exactly the selected pictures once each and moves no bundle. A capture re-run
still writes candidates only, so it can never promote, demote, recaption or reorder a human
choice. The picker lives beside the capture panel rather than inside it, so the capture contract
that panel asserts - no select control on an offered picture - is untouched.

**Three defects this package found, none of them worked around in a test.** The milestone lock
read a `scope_id` the milestones table never had - the scope arrives through the project join, so
the lock does too. The primary switch set the new flag before clearing the old one, which the
one-primary index refused with 23505: clearing first is what makes the switch atomic rather than
impossible. And the promote-while-leaving check read the row's own flag when the request had not
named one, so rejecting a primary was refused as its own promotion: only an explicit
`isPrimary: true` on an unselected picture is a 422, and leaving the set clears the flag in the
same write. The browser suite found two fixture defects of its own: row locators captured before
a move were polled after it, so the retry watched whatever moved into the position, and a
"stale" PATCH that happened to describe the stored state answered the correct no-op 200 - the
assertions re-query inside the retry now, and the stale probe carries a nonce caption so it is
always a change.

Evidence, all run by the implementer rather than taken on report: `CHRONICLEAF_E2E=1 bash
scripts/verify.sh` exit 0 - platform feed, build, fifteen filtered stages each asserted to select
at least one test (15/15 shell, 24/24 persistence, 23/23 access, 19/19 projects, 12/12 jobs, 9/9
media, 14/14 evidence, 32/32 vault, 22/22 voice, 26/26 provider contract, 23/23 OpenAI adapter,
27/27 GitHub evidence, 52/52 capture, 18/18 curation, 30/30 drafts - 346 in total), `npm ci`,
`tsc` + `vite build`, then 52/52 Playwright across nine database-isolated browser suites
(21/21 shell/access/projects/jobs, 3/3 evidence, 6/6 vault, 4/4 voice, 4/4 providers, 3/3 GitHub,
3/3 capture, 4/4 drafts, 4/4 curation); name preflight exit 0;
`openspec validate --all --strict --no-interactive` 54/54 before and after the archive, which used
no `--skip-specs`. The curation migration also drops the milestone-only candidate index the
capture migration created but the model never described, and widens captions 500 → 1000 for the
owner's words; the e2e seed's candidate rows name the two real capture jobs the suite requested
(which refuse `capture_sandbox_unavailable` on this host and write nothing) and one real uploaded
PNG behind all three. Deployment stays `deployable: false`; there is no remote, no push and no
publication.

Next: `current_spec` is `github-app-connection`, the next package in drain order after
`hosted-archive-operations`. `hosted-archive-operations` is archived
(`2db6039`, canonical spec `openspec/specs/hosted-archive-operations/spec.md`): operator-run
backup manifests plus owner-run recoverable deletion over tenant-scoped stores, with Community
behavior byte-identical. One `archive_backup_manifests` row per operator-measured backup
carrying the schema/key versions, the media inventory hash and the content digest beside an
opaque inventory token (no route serves manifests, so no location or key history reaches an
owner response); `MeasureAsync`/`RecordAsync`/`RehearseAsync` recompute both hashes from the
live scope and compare byte for byte, so an unknown schema, an unknown key version or a
drifted inventory/digest each fail loudly with the old stamp kept. One `archive_tombstones`
row per confirmed deletion carrying the 30-day window, the erase request, an operator hold
and the restore/purge stamps under a partial unique index (one live tombstone per project);
`DELETE /api/projects/{id}/archive-data` confirms the project's own name at its own revision
(422/409/428), revokes a live journey and disables an enabled schedule through the delivered
contracts before inserting the tombstone, while scope credentials, evidence, milestones,
assets and media are untouched; `POST .../restore` lifts a live deletion inside the window
at the tombstone's revision (past it is `archive_restore_expired` 410); the operator
`PurgeExpiredAsync` pass skips an open window, a hold, a live approval or a live projection
with its reason and otherwise retires only unreferenced project media through the delivered
store (referenced media 409-kept) with a receipt itemizing retained bundles/assets. Hosted
reads/edits gate through `RequireProjectPermissionAsync` (read/edit) with the Community path
branch-free; foreign projects and manifests answer the delivered 404 with no foreign data.
Read that package's own boundary before extending it, and note the four defects verification
found: minimal-API DELETE forbids inferred bodies (`[FromBody]` explicit); nested units of
work are rejected, so the delete suspends first and inserts last and the purge retires
before stamping (each step idempotent and retry-safe); an owner session lapses across the
31-day clock move, so post-move reads sign in again; and `projects` carries no timestamps,
so foreign-scope seeds insert without them. Evidence, all run by the implementer rather than
taken on report: `bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`) with
the platform-feed stage and the new `hosted archive operations tests` stage (17/17) alongside
all existing stages, `npm ci`, `tsc` + `vite build`, `dotnet build --nologo --no-incremental`
0 errors with the same 2 pre-existing warnings at HEAD, name preflight exit 0, and `openspec
validate --all --strict --no-interactive` 54/54 before and after the archive, which used no
`--skip-specs`. No Web UI was shipped: the design's optional panel is satisfied API-observable
(manifests are operator rows by design, never owner responses), and no new browser suite was
added. The opt-in real-backup rehearsal against a real snapshot inventory was not performed:
only live-row digests are compared and no claim is made about any external snapshot store.
Deployment stays `deployable: false`; there is no remote, no push and no publication.
`scheduled-project-sync` is archived
(`bfa302a`, canonical spec `openspec/specs/scheduled-project-sync/spec.md`): opt-in
recurring repository synchronization, with Community behavior byte-identical. One
`project_sync_schedules` row per project carrying an enabled flag, a 15..10080-minute cadence,
a `next_run_at`, the last operation id and an optimistic revision; `PUT
/api/projects/{id}/sync-schedule` creates at If-Match zero and edits under compare-and-set
(428 missing, 409 stale, 422 interval, 409 `schedule_no_connection` without a readable
connection, 429 `schedule_quota` past 10 enabled schedules per scope) while `GET` serves the
stored intent with the last operation's redacted status, error code and completion stamp. A
one-minute `ProjectSyncTicker` loop fires due rows into kind `project.scheduled-sync` operations
on the delivered queue keyed `scheduleId:dueUnix` (target project, input revision bound, one
active sync per project across both sync kinds, misses collapsed to one interval past now), and
the handler runs the delivered candidate poll — no second poll, transport or store — so a moved
project supersedes through the delivered `project` target mapping and provider outcomes keep
their delivered codes. Archive/disconnect disables the schedule and cancels polls that never
started; Hosted reads/edits gate through `RequireProjectPermissionAsync` (read/edit) with the
Community path branch-free. Read that package's own boundary before extending it, and note the
three defects verification found: `AddHostedService` alone does not resolve the ticker by
concrete type (registered as its own singleton first, then hosted from it); the domain record's
stored revision needs a `Rehydrate` factory rather than replaying `Change` in the store reader;
and the collapse test's expectation was wrong, not the code (an active op holds `next_run_at`
until it finishes). Evidence, all run by the implementer rather than taken on report:
`bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`) with the platform-feed stage
and the new `scheduled project sync tests` stage (12/12) alongside all existing stages, `npm
ci`, `tsc` + `vite build`, `dotnet build --nologo --no-incremental` 0 errors with the same 2
pre-existing warnings at HEAD, name preflight exit 0, and `openspec validate --all --strict
--no-interactive` 54/54 before and after the archive, which used no `--skip-specs`. No Web UI
was shipped: the design's React settings are satisfied API-observable (candidate-only behavior
is structural — the tick can only start the delivered poll — and off-by-default is the absent
row), and no new browser suite was added. The opt-in real-GitHub run was not performed: only
the wire bytes are scripted and no claim is made about any real repository. Deployment stays
`deployable: false`; there is no remote, no push and no publication.
`team-project-permissions` is archived
(`0f47fca`, canonical spec `openspec/specs/team-project-permissions/spec.md`): Hosted team
invitations plus per-project roles, with Community behavior byte-identical. One
`tenant_invitations` row per invite carrying a normalized email label, a tenant role, a
SHA-256 token hash, a 7-day expiry and single-use accept/revoke stamps, plus one
`project_memberships(project_id, user_id, permission)` row per explicit grant
(reader|editor|approver|publisher) and an optimistic `tenant_memberships.revision` column
(backfilled 1); private invitation, team-member and project-grant routes plus a service-layer
`team-check` probe and `team-access` read, with the real domain project read and edit gated
in Hosted mode through `RequireProjectPermissionAsync` (owner bypasses implicitly, Community
takes no branch). Reader reads, editor edits, approver approves/exports, publisher publishes
only an approved head (`approval_required` 409 otherwise); last-owner demote/remove is
`team_last_owner` 409, expired token `invitation_expired` 410, used/revoked/unknown tokens
`invitation_reused` 409 (unknown reuses delivered 404), duplicate pending invite
`invitation_exists` 409, and duplicate grants no-op with no write and no audit row. A
revoked member's sessions die while grant and audit rows stay as history. The role and
permission vocabularies are closed by database check constraints. Hosted identity is
issuer/subject with no email claim, so the email is the owner's label and the 32-byte bearer
token disclosed exactly once is the grant. Read that package's own boundary before extending
it, and note the two defects verification found: the token is 32 bytes (64 hex chars, not
128), and the tenant boundary already owns `DELETE .../members/{userId}`, so team
administration lives under `.../team-members` rather than answering one path twice (the
first run failed ambiguous-match 500 on exactly that collision). Evidence, all run by the
implementer rather than taken on report: `bash scripts/verify.sh` exit 0 with the
platform-feed stage and the new `team project permission tests` stage (12/12) alongside all
existing stages, `npm ci`, `tsc` + `vite build`, name preflight exit 0, and
`openspec validate --all --strict --no-interactive` 54/54 before and after the archive,
which used no `--skip-specs`. No Web UI was shipped: the design names it optional, the
scenarios are API-observable, and no new browser suite was added. Deployment stays
`deployable: false`; there is no remote, no push and no publication.
`cloud-tenant-boundary` is archived
(`0a6a935`, canonical spec `openspec/specs/cloud-tenant-boundary/spec.md`): Hosted end-user
identity plus tenant data isolation, with Community behavior byte-identical. One OIDC
issuer/subject user row per verified login (single-use hashed state/nonce, exact
issuer/audience/lifetime/nonce match, RS256 against the issuer key set, BCL-only), one tenant
with a fresh scope id plus owner membership and explicit select per verified user (default quota
1), and revocation that stamps the membership and kills that tenant's sessions. Every domain
route resolves the active tenant's scope through a request-scoped `IOwnerAccessService`
decorator - zero endpoint edits, Community path delegates branch-free - so all existing
`scope_id` predicates isolate projects/credentials/evidence/media/operations in SQL; FORCED
row-level policies on the tenant tables back it per transaction context (`SET LOCAL`
`app.tenant_id`). A Community volume in Hosted mode 409s (`community_import_required`) until
the explicit per-tenant import is recorded, and Community rows are never moved. In Hosted mode
the operator token path is closed and owner cookies authenticate nothing. Read that package's
own boundary before extending it, and note the three defects verification found: a double-join
DTO-ordered LINQ does not translate (order entities, project raw, format client-side); a custom
GUC first touched by `SET LOCAL` reads back `''` not NULL afterwards, so the RLS unset-branch
is `nullif(..., '')`; and the fixture's `chronicleaf` role is a superuser that bypasses RLS
unconditionally, so the pooled-connection proof runs as a least-privilege role. Also note the
two adjacent fixes in the same commit: the OIDC transport holds its own client rather than a
bare `HttpClient` singleton (three delivered transports already resolve one bare client, so a
second would make resolution order-dependent), and the static-blog ZIP assertion sorted actual
but not expected while asset ids are random GUIDs (a coin-flip the gate lost twice) - now
sorted on both sides, test-only. `agency-client-projects` is archived
(`764eb0e`, canonical spec `openspec/specs/agency-client-projects/spec.md`): one
`clients` row per agency client carrying a name (1..160), plain-text notes (20k), an
optimistic revision, a membership context revision and a soft-archive stamp, plus one
nullable `projects.client_id` foreign key (Restrict, null means personal); private routes
for client CRUD, `GET /api/clients/{id}/projects` hierarchy, `GET
/api/clients/{id}/context` report context built from explicit project ids, and `PATCH
/api/projects/{id}/client` assignment under the project's own If-Match. A foreign-scope
client is `not_found` 404 without exposure, an archived target is `client_archived` 409
while moving away or clearing stays allowed, a duplicate association at the stored revision
is a no-op with no write and no audit row, and every membership change advances both
clients' context revisions while leaving client edit revisions alone. The project read now
carries `clientId`. Read that package's own boundary before extending it, and note the two
defects its tests found: a new envelope code needs an explicit middleware type-arm (the
status-code mapping alone still answers 500), and a `JsonElement?` body property collapses
an explicit JSON null into a missing key, so the assignment route binds the raw document to
tell "clear back to personal" from "key absent" (absent is 422). No Web UI was shipped: the
design names it optional, the scenarios are API-observable, and no new browser suite was
added. `public-project-journey` is archived
(`6bc7e8e`, canonical spec `openspec/specs/public-project-journey/spec.md`): one
`project_journeys` row per project carrying a global kebab-case slug, an enabled flag, an
optimistic revision, the canonical selection hash and the frozen snapshot JSON; three private
routes (preview computes the hash, PUT publishes/revokes under revision + hash checks, GET
reads state) and two anonymous no-store routes (`GET /api/journey/{slug}`,
`GET /api/journey/{slug}/media/{mediaId}`) that serve exactly the explicitly published
confirmed milestones, approved bodies and approval-covered pictures. Unapproved is
`approval_required`, moved is `approval_stale`, a stale preview hash is
`journey_selection_stale` 409 with the snapshot untouched, a taken slug is
`journey_slug_conflict` 409, a moved revision is `revision_conflict` 409, a missing revision
on update is 428, and a revocation or lapsed approval suspends the public read as 404 without
deleting the row. Private notes, candidates, unselected pictures and credentials never enter
the snapshot and are asserted absent. Read that package's own boundary before extending it,
and note the one defect its tests found: the service lowercased slugs instead of refusing
them, so `UPPER` published as `upper` - slugs are now taken verbatim and anything but
lowercase kebab case is 422. `static-blog-export` is archived
(`066e153`, canonical spec `openspec/specs/static-blog-export/spec.md`): a job-built
deterministic ZIP (`index.html` timeline oldest-first, `posts/{assetId}.html` rendered through
the HTML export package's own sanitizing adapter, `assets/{mediaId}.png` deduplicated,
`manifest.json` with per-file checksums, `.nojekyll`) covering exactly the explicitly selected
approved assets; the request freezes per-asset bindings (content revision, body hash, evidence
bundle/revision, media selection hash) and the run re-resolves them, so a revocation ends the
job `static_blog_superseded` with no completed archive while the download re-checks the same
bindings before serving a byte. Unapproved is `approval_required`, moved is `approval_stale`,
missing bytes are `media_unavailable`, traversal base paths are 422, and the same selection
builds reproducible checksums twice. Read that package's own boundary before extending it, and
note the one defect its tests found: the base-path validator split `/journey` on `/` and
refused the leading slash's empty segment as a parent directory - every export POST answered
422 until the first segment was skipped. `screenshot-evolution-images` is archived
(`f2916e1`, canonical spec `openspec/specs/screenshot-evolution-images/spec.md`): derived
comparison PNGs (before-after, horizontal-timeline, vertical-long, gallery) rendered from
SELECTED screenshots across 2..12 confirmed milestones through a BCL-only composer (native-size
cells on neutral padding, no resampling, gaps as unavailable bands), each row carrying a
re-derivable manifest citing the media, caption and evidence revisions every cell was built
from; candidates are never compared and no render promotes one. The artifact is a draft until
a human approves the exact selection and artifact hashes (preview is reviewable, download is
gated under the review package's `approval_required`/`approval_stale`); a caption edit
supersedes the render as `revision_conflict` and an oversized canvas is `payload_too_large`
with nothing stored. Read that package's own boundary before extending it, and note the three
defects its tests found: feature exceptions need explicit middleware type-arms (status-code
mapping alone still answers 500); EF's FK-index convention must appear in the hand-written
Designer or readiness fails `PendingModelChangesWarning`; and comparison cells are uniform
(max-dimension) grid cells, so a narrower source sits centered rather than packed.
`content-asset-variants` is archived (`65309ac`,
canonical spec `openspec/specs/content-asset-variants/spec.md`): seven deterministic formats
(changelog, technical-blog, social-short, github-release, readme-update, portfolio,
weekly-progress) derived from an approved Markdown story through fixed templates with no
provider and no new tables - variants are new `content_assets` rows bound to the source evidence
head and the current voice snapshot, each a draft until separately approved (never approved is
`approval_required`, moved evidence is `approval_stale`). Read that package's own boundary
before extending it, and note the two derivation defects its tests found: the narrative context
builder's composed lines carry machine tokens (dates, entry refs, evidence ids) no
evidence-cited body may contain, so history arrives as structured exportable entry texts with
unverifiable figures omitted; and the technical-blog limitations section is `Limitations`, not
`Known limitations`. `html-content-export` is archived (`aae5181`, canonical spec
`openspec/specs/html-content-export/spec.md`): sanitized portable HTML export of the approved
revision through the same approval bindings the Markdown export re-checks (never approved is
`approval_required`, moved head is `approval_stale`), rendered by the pure
`HtmlExportAdapter` (fixed allowlist template, embedded CSS, no scripts/frames/event handlers,
unsafe-protocol links and source image runs shown as escaped characters), packed as a single
`.html` file or a ZIP with generated `images/image-N.png` paths plus a SHA-256 manifest, with
a missing approved picture refused as `media_unavailable` 409 rather than served picture-less.
Read that package's own boundary before extending it, and note the one renderer defect its
browser coverage found: a special character starting nothing (unclosed `**`) needed an explicit
single-character advance or the inline loop would spin — the spec now carries that run. `narrative-memory` is archived (`f7e4a81`, canonical spec
`openspec/specs/local-git-evidence/spec.md`) and its own notes are in the package section above, including
the registration shape, the two diff numbers and the one registration name a later transport must not
reuse. `community-compose-release` was the MVP-RELEASE gate and is
archived (`5a40d46`, canonical spec `openspec/specs/community-compose-release/spec.md`):
reproducible two-service Community packaging (application + PostgreSQL on the pinned digest,
no Redis), operator quickstart, container entrypoint with best-effort capture firewall, and
complete workflow evidence (GitHub → confirmed Milestone → screenshot → grounded Markdown →
human approval → export plus Timeline). Read that package's own boundary before extending it,
and note the two defects it exposed while proving itself: the Dockerfile's build stage had not
been rebuilt since the leaf projects landed (container publish failed CS0246 while native
builds stayed green — every earlier container smoke predates the drift), and the application
port needed to become overridable because a shared daemon runs parallel verifications. The
remaining active changes are POST-MVP, CLOUD or DEMAND-GATED follow-ups; none is selected.
`markdown-review-export` is archived (`64d6868`, canonical spec
`openspec/specs/markdown-review-export/spec.md`): a human edit appends a revision carrying the
owner's words and invalidates live approvals as `content_edited`; an approval binds the head
revision, the body hash, the evidence head, the voice snapshot hash and the media selection
hash, and is refused while any voice finding or any `claim_evidence_missing` grounding finding
remains, with no acknowledgment flag that clears it; an export serves the approved revision's
exact bytes as a Markdown attachment only while every binding still describes the stored state
(never approved is `approval_required`, once approved is `approval_stale`); carried-forward
evidence mints new item ids, so stale citations stay dangling and only a fresh grounded
generation can be approved again. Read that package's own boundary before extending it, and
note the two compositional updates it forced: the drafts suite's "no approve route" boundary
now asserts zero rows in the review package's table rather than zero tables, and the provider
contract browser spec now matches the panel's plural "adapters are installed for" sentence.
`llm-ollama-adapter` is archived (`efed7af`, canonical spec
`openspec/specs/llm-ollama-adapter/spec.md`): the fifth BYOK transport posts the consented payload
to the operator-allowlisted origin's chat resource with an optional per-call bearer lease -
a consent without a credential is a keyless call, not a refusal - carries no request id because
Ollama supplies none, and reports a local endpoint with no credential required and no native
schema support, so the schema rides in the prompt and the text is validated locally. An
unfinished generation, a truncation and an unrecognised stop are all invalid, never a draft,
and prose refusals arrive as text the adapter cannot classify. Read that package's own boundary
before extending it, and note the last slot migration it forced: both doubles sat on `ollama`,
so both hosts now insert their double first and the drafts guard proves the double shadows the
real adapter. The contract panel gained a conditional endpoint field for local-endpoint slots.
`llm-openrouter-adapter` is archived (`c51dbd7`, canonical spec
`openspec/specs/llm-openrouter-adapter/spec.md`): the fourth BYOK transport posts the consented payload
to the fixed chat completions resource at the OpenRouter origin with a per-call bearer lease, records
the provider's own generation id as the correlation handle, and reports no native schema support so
the schema rides in the prompt and the text is validated locally. A truncation is invalid even with a
prefix, because the ceiling may be an upstream hop's rather than the named model's. Read that
package's own boundary before extending it, and note the slot migration it forced: the double in
`LlmProviderContractTests` moved from `openrouter` to `ollama` - asserted by that suite's own
catalogue guards. Both doubles then sat on `ollama` until `llm-ollama-adapter` claimed the last
slot and moved both hosts to insert-first registration (see that package's note above).
`llm-anthropic-adapter` is archived (`ab23ace`, canonical spec
`openspec/specs/llm-anthropic-adapter/spec.md`): the third BYOK transport posts the consented payload
to the fixed messages resource at the Anthropic origin with a per-call `x-api-key` lease beside the
pinned version header, records the provider's own message id as the correlation handle, and reports
no native schema support so the schema rides in the prompt and the text is validated locally. Read
that package's own boundary before extending it, and note the two slot migrations it forced: a test
double registered on a slot production has since claimed is shadowed by the real adapter, so
`LlmProviderContractTests` moved its double from `anthropic` to `openrouter` and
`GroundedMarkdownDrafts` moved its scripted double from `anthropic` to `ollama` - each asserted by
that suite's own slot-ownership guard, and the next adapter package moves them again the same way.
`llm-gemini-adapter` is archived (`b5f74ac`, canonical spec
`openspec/specs/llm-gemini-adapter/spec.md`): the second BYOK transport posts the consented payload
to the model's generateContent resource at the fixed Google origin with a per-call `x-goog-api-key`
lease, no request id is invented where the provider supplies none, and a safety stop is a refusal
with its partial text discarded. Read that package's own boundary before extending it, and note the
registration defect it exposed: `TryAddSingleton<ILLMProvider>` keys on the service type, so a
second adapter using it silently installs nothing in production — every adapter extension now uses
plain `AddSingleton` and the resolver keeps the first adapter per slot.
`milestone-timeline` is archived (`4cfa8c3`, canonical spec
`openspec/specs/milestone-timeline/spec.md`): a project's confirmed history now reads oldest
first on a private route with a cursor bound to scope, project and the candidate filter, and the
project view shows it under its own dates with candidates hidden until asked for. Read the
milestone package's own boundary before extending it: no route here writes anything, a dismissed
record is omitted rather than narrated, and a cursor from another project, another filter or the
milestone list is refused rather than applied to the wrong history.
`screenshot-curation` is archived (`9c1ce4a`, canonical spec
`openspec/specs/screenshot-curation/spec.md`): a captured picture can now be selected against the
same evidence bundle a draft is grounded in, with one primary, an explicit order and plain-text
captions. Read the draft package's own boundary before extending it: a draft is text a human acts
on, so the local schema validator, the voice findings and the "draft only" framing are all
load-bearing and none of them is decoration.

Two working notes for whoever picks this up. First, `github-evidence` was delivered across two
worker passes because the first was **cancelled mid-flight rather than failed**, and it left a
substantial uncommitted implementation behind; the backend was verified as inherited (build 0
warnings, 27/27) and completed rather than restarted. Audit `git status` before assuming a clean tree
whenever a worker does not return `succeeded`, because the expensive mistake here would have been
discarding that work. Second, this batch has now produced a defect in the *same* class three times, and
the last one is the most instructive: a test double registered on a provider slot the production
composition root had since claimed was shadowed by the real adapter, so every scenario in that suite
would have received a real provider call instead of its scripted answer. A test that silently stops
testing looks exactly like a test that passes. When you register anything into a composition root,
re-read which existing registrations it shadows, and re-run the suites of every package that used to own
that slot — here that meant moving `LlmProviderContractTests` to a slot no package implements yet and
turning "production has no adapter" into a claim about one unimplemented slot, which is a stronger
assertion than the one it replaced. Run `scripts/e2e.sh` as soon as the UI builds, and diff the whole
change surface, not only the files you wrote.

Read only that package, keep one `current_spec`, implement, verify locally, strictly validate, archive
without `--skip-specs`, then make the implementation commit and the HANDOFF-only pointer commit, and
stop. Do not push.

## Package notes

`agency-client-projects` is archived (`764eb0e`, canonical spec
`openspec/specs/agency-client-projects/spec.md`). Read that package's own boundary before
extending it, and note what it decided that a later reader would otherwise have to rediscover.

**Release evidence (`agency-client-projects`, commit `764eb0e`, all run by the implementer
rather than taken on report).** `CHRONICLEAF_E2E=1 bash scripts/verify.sh` exit 0
(`GATE_EXIT=0`) with the platform-feed stage and thirty-two filtered stages (the prior
thirty-one plus `agency client project tests`) each asserted to select at least one
test, including **10/10 agency client projects** run directly against the same real
disposable PostgreSQL and the real API host (two projects under Client A listed in name
order with the personal project excluded and a project edit preserving the grouping;
foreign-scope and invented client ids both 404 with the project untouched at revision 1;
client lists paged by name with the scope-bound cursor; a move from A to B depicted under
B's bumped context generation and absent from A's while neither client edit revision moved;
archived client refusing new assignments as `client_archived` while keeping its historical
tree and still letting a project move away or clear; report context depicting exactly the
requested ids grouped under that client; duplicate association a no-op with no context
bump; stale revisions 409 and missing ones 428 on both routes; empty/oversized names and
notes, missing/malformed client ids and malformed context ids all 422 naming the field;
sessionless list 401; migration columns, bounds, `fk_projects_client_id`,
`IX_projects_client_id` and `ix_clients_scope_id_name` read back from the catalog).
Neighbour suites re-run against the same build: 19/19 projects-and-milestones, 23/23
access, 7/7 milestone-timeline. `npm ci`, `tsc` and `vite build` all green, and
`bash scripts/e2e.sh` green across all fourteen database-isolated browser suites (no new
suite: this package ships no UI). `dotnet build --nologo --no-incremental` 0 errors with
the same 2 pre-existing warnings at HEAD. Name preflight exit 0;
`openspec validate --all --strict --no-interactive` 54/54 before and after the archive,
which used no `--skip-specs`. Deployment stays `deployable: false`; there is no remote,
no push and no publication.

`public-project-journey` is archived (`6bc7e8e`, canonical spec
`openspec/specs/public-project-journey/spec.md`). Read that package's own boundary before
extending it, and note what it decided that a later reader would otherwise have to rediscover.

**Release evidence (`public-project-journey`, commit `6bc7e8e`, all run by the implementer
rather than taken on report).** `CHRONICLEAF_E2E=1 bash scripts/verify.sh` exit 0
(`GATE_EXIT=0`) with the platform-feed stage and thirty-one filtered stages (the prior
thirty plus `public project journey tests`) each asserted to select at least one
test, including **14/14 public project journey** run directly against the same real
disposable PostgreSQL and the real API host (enabled selection serves exactly its entries
oldest-first with captions and media addresses, all no-store; approved picture bytes served
anonymously as PNG while unselected and foreign pictures are 404; never-enabled slug 404
with private preview 401 sessionless; private notes, candidate title, unselected media and
credential value asserted absent; revoke disables app and media routes immediately; stale
hash 409 with snapshot untouched; edit-triggered stale approval suspends as 404 with the
row intact; slug conflict 409; stale revision 409 and missing revision 428; unapproved
`approval_required`; foreign asset 404; six non-kebab slugs 422; dismissed milestone 422;
absent journey 404 with revoke as disabled no-op). `npm ci`, `tsc` and `vite build` all
green, and `bash scripts/e2e.sh` green across all fourteen database-isolated browser
suites (67+ Playwright, no new suite: this package ships no UI).
`dotnet build --nologo --no-incremental` 0 errors with the same 2 pre-existing warnings at
HEAD. Name preflight exit 0; `openspec validate --all --strict --no-interactive` 54/54 before
and after the archive, which used no `--skip-specs`.

**A journey is a projection, not a source of truth.** The PUT carries the approved asset
ids beside the preview hash it pins, because a hash alone cannot be recomputed into the
selection it covers; the service re-resolves the same selection through the delivered
asset, approval, evidence and media stores (reusing the review package's
`approval_required`/`approval_stale` and the static-blog package's canonical selection
hash) and freezes the snapshot only after every binding passes. The public read serves
the frozen snapshot bytes only while a live re-resolution still agrees on every binding
(content revision, body hash, evidence bundle/revision, media selection hash, title and
date) - any drift suspends as the same 404 a missing slug answers, so a caller cannot
probe for a journey that exists but is not public. The slug lookup is the one unscoped
read in the package; media bytes are opened under the journey row's own scope with an
empty actor and only for pictures the live snapshot depicts. No UI was shipped
(route-only, the `static-blog-export` precedent). Deployment stays `deployable: false`;
there is no remote, no push and no publication.

`static-blog-export` is archived (`066e153`, canonical spec
`openspec/specs/static-blog-export/spec.md`). Read that package's own boundary before
extending it, and note what it decided that a later reader would otherwise have to rediscover.

**Release evidence (`static-blog-export`, commit `066e153`, all run by the implementer
rather than taken on report).** `CHRONICLEAF_E2E=1 bash scripts/verify.sh` exit 0
(`GATE_EXIT=0`) with the platform-feed stage and thirty filtered stages (the prior
twenty-nine plus `static blog export tests`) each asserted to select at least one
test, including **15/15 static blog export** run directly against the same real
disposable PostgreSQL and the real API host (two approved posts with text, one
allowlisted picture and a matching manifest; unapproved refused 409 before any job;
foreign-asset 404; nonroot `/journey` base path as metadata with offline relative
links; revocation superseded with no archive; moved evidence stale; missing picture
`media_unavailable`; traversal base paths 422; idempotent replay; byte-repeatability
across two exports; migration/schema plus builder unit cases for cancel, 413, hostile
text and base-path normalization). `npm ci`, `tsc` and `vite build` all green, and
`bash scripts/e2e.sh` green across all fourteen database-isolated browser suites
(67/67 Playwright, no new suite: this package ships no UI).
`dotnet build --nologo --no-incremental` 0 errors with the same 2 pre-existing warnings at
HEAD. Name preflight exit 0; `openspec validate --all --strict --no-interactive` 54/54 before
and after the archive, which used no `--skip-specs`.

**An archive holds only what an approval still authorizes.** The POST reuses the review
package's own bindings rather than redefining them - never approved is
`approval_required`, once approved but moved is `approval_stale` - and freezes them into
`static_blog_export_requests` beside the canonical selection; the job kind
`project.static-blog-export` binds the project so a project edited mid-run supersedes at
the worker's own commit. Posts are rendered per asset through the delivered
`IContentExportAdapter`, whose generated `images/image-N.png` paths are the only strings
rewritten (to shared `assets/{mediaId}.png`), so the sanitizer stays single-sourced and a
template change breaks loudly in the success test rather than silently. The site checksum
covers the canonical file listing only, entries pack in sorted order with a fixed
timestamp, and the download re-renders and compares digests before serving - a mismatch is
a 500, not a download. No UI was shipped (route-only, the `html-content-export`
precedent; the design marks it optional), no bytes reach any host (no Pages push: the
export is a local artifact), and no approval, generation or publication lives here.
Deployment stays `deployable: false`; there is no remote, no push and no publication.

`screenshot-evolution-images` is archived (`f2916e1`, canonical spec
`openspec/specs/screenshot-evolution-images/spec.md`). Read that package's own boundary before
extending it, and note what it decided that a later reader would otherwise have to rediscover.

**Release evidence (`screenshot-evolution-images`, commit `f2916e1`, all run by the implementer
rather than taken on report).** `CHRONICLEAF_E2E=1 bash scripts/verify.sh` exit 0
(`GATE_EXIT=0`) with the platform-feed stage and twenty-nine filtered stages (the prior
twenty-eight plus `screenshot evolution image tests`) each asserted to select at least one
test, including **12/12 screenshot evolution images** run directly against the same real
disposable PostgreSQL and the real API host (pixel-equality assertions on decoded canvas
regions, gap-band fill assertions, idempotency replay/conflict, viewport-mismatch vs gallery
labels, 413 with an empty list afterwards, renderer unit cases for the pixel ceiling and gap
composition). `npm ci`, `tsc` and `vite build` all green, and `bash scripts/e2e.sh` green
across all fourteen database-isolated browser suites (no new suite: this package ships no UI).
`dotnet build --nologo --no-incremental` 0 errors with the same 2 pre-existing warnings at
HEAD. Name preflight exit 0; `openspec validate --all --strict --no-interactive` 54/54 before
and after the archive, which used no `--skip-specs`.

**A comparison depicts selected pictures and nothing else.** Every depicted cell is read
through the curation store with its selection state: naming a candidate is 422
`validation_failed` on `selectedScreenshotIds`, never a promotion, and a milestone with no
selected picture is an `unavailable` gap band in the manifest and on the canvas, never an
interpolation. Mixed viewports outside the gallery are 422
`comparison_viewport_mismatch`; the gallery labels each cell's viewport. The PNG is composed
by BCL code only (grey/RGB/RGBA 8-bit decode with all five filters, uniform max-dimension
cells, neutral padding, deterministic grey re-encode the delivered media store accepts), so
there is no image library to pin and no resampling that could invent a before screenshot.
The artifact's media is stored first and the row second (the capture package's order), with a
quiet retire when the row loses its insert; source bytes stay pinned by the evidence
screenshot items curation already publishes, so this row pins only its own derivation.
Approval binds the selection hash and the artifact hash on the same row through the review
package's own codes, and the export re-checks both live before serving the exact bytes.
Deployment stays `deployable: false`; there is no remote, no push and no publication.

`content-asset-variants` is archived (`65309ac`, canonical spec
`openspec/specs/content-asset-variants/spec.md`). Read that package's own boundary before
extending it, and note what it decided that a later reader would otherwise have to rediscover.

**Release evidence (`content-asset-variants`, commit `65309ac`, all run by the implementer
rather than taken on report).** `CHRONICLEAF_E2E=1 bash scripts/verify.sh` exit 0
(`GATE_EXIT=0`) with the platform-feed stage and twenty-eight filtered stages (the prior
twenty-seven plus `content asset variant tests`) each asserted to select at least one test,
including **10/10 content asset variants** run directly against the same real disposable
PostgreSQL. `npm ci`, `tsc` and `vite build` all green. `bash scripts/e2e.sh`
green including **2/2 Playwright in suite 14** (fourteenth disposable PostgreSQL + published
application on port 5094): an approved story derives a changelog with its sections, citations
and its own approval, and a readme-update derivation names its proposal as one.
`dotnet build --nologo --no-incremental` 0 errors with the same 2 pre-existing warnings at
HEAD. Name preflight exit 0; `openspec validate --all --strict --no-interactive` 54/54 before
and after the archive, which used no `--skip-specs`.

**A variant is a derivation, never a generation.** `POST /api/milestones/{id}/assets`
answers 201 synchronously through the deterministic `ContentVariantRenderer` (template version
`content-variants/1` recorded as the revision's prompt version): no provider, no credential, no
consent, no job, no new tables - variants are new rows in the drafts package's own
`content_assets`/`content_revisions` keyed by the unique `(scope, milestone, type)` index, so
no migration was added. The source must be the milestone's approved `markdown-story` carrying
a live approval that still binds the head revision, the body hash and the evidence head
(never approved is `approval_required`, moved evidence is `approval_stale`, a moved request
revision is `revision_conflict`); the derivative starts with no approval row, so approving the
source never approves the variant and the variant's own approval binds and stales exactly like
its source's through the review package's unchanged routes. Voice precedence is structural:
the renderer receives the profile's resolved effective values and cuts the title to the
effective `TitleMaxChars`, so a project override shortens the preset's limit on the variant.
Narrative history arrives as structured exportable entry texts (private entries stay absent;
lines with figures the cited evidence never stated are omitted), with entry ids in the audit
row rather than the body. The rendered body is held to the same `MarkdownDraftValidator`
citation/numeral rules as a generated draft before anything is stored. The panel
(`ContentVariantPanel` under the review panel) lists, derives, previews inertly through the
review package's own renderer, and approves each variant on its own revision; readme-update
carries its own proposal notice element. Deployment stays `deployable: false`; there is no
remote, no push and no publication.

`html-content-export` is archived (`aae5181`, canonical spec
`openspec/specs/html-content-export/spec.md`). Read that package's own boundary before extending
it, and note what it decided that a later reader would otherwise have to rediscover.

**Release evidence (`html-content-export`, commit `aae5181`, all run by the implementer rather
than taken on report).** `CHRONICLEAF_E2E=1 bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`) with
the platform-feed stage and twenty-seven filtered stages each asserted to select at least one
test: 15/15 shell, 24/24 persistence, 23/23 access, 19/19 projects, 12/12 jobs, 9/9 media, 14/14
evidence, 32/32 vault, 22/22 voice, 26/26 provider contract, 23/23 OpenAI, 24/24 Gemini, 24/24
Anthropic, 23/23 OpenRouter, 26/26 Ollama, 27/27 GitHub evidence, 18/18 GraphQL enrichment, 21/21
local Git, 52/52 capture, 18/18 curation, 30/30 drafts, 7/7 timeline, 11/11 review-export, **10/10
HTML export**, 7/7 narrative memory, 10/10 compose-release, 40/40 release candidates — 567 in
total. `npm ci`, `tsc` and `vite build` all green. `bash scripts/e2e.sh` green: 65/65 Playwright
across thirteen database-isolated browser suites (21/21 shell/access/projects/jobs, 3/3 evidence,
6/6 vault, 4/4 voice, 4/4 providers, 3/3 GitHub, 3/3 capture, 4/4 drafts, 4/4 curation, 3/3
timeline, 2/2 review-export **with the new sanitized-HTML download assertions**, 5/5 candidates,
3/3 narrative). `dotnet build --nologo --no-incremental` 0 errors with the same 2 pre-existing
warnings at HEAD. Name preflight exit 0; `openspec validate --all --strict --no-interactive`
54/54 before and after the archive, which used no `--skip-specs`.

**Inheritance note.** This package was delivered across two worker passes because the first was
killed mid-flight and left a substantial uncommitted implementation behind (adapter, service,
endpoint, panel, 10 backend tests). The second pass verified it as inherited
(`dotnet build --nologo --no-incremental` 0 errors with the same 2 pre-existing warnings,
10/10 green on first run, 11/11 on the untouched `markdown-review-export` dependency) and
completed rather than restarted it: every ticked task was re-verified with real evidence, and
no inherited defect was found — nothing inherited was rewritten.

**The export is the same approval in a second shape, and the renderer owns nothing.**
`GET /api/assets/{id}/export?format=html` re-checks every binding the Markdown export
re-checks through the review package's own `ResolveApprovedAsync` — the HTML path shares it
rather than redefining it — then reads the approval-covered pictures out of the media store
and hands the pure `HtmlExportAdapter` a closed snapshot of bytes and pictures: no URL to
fetch, no id to resolve, so a remote image in the source cannot become a request. Source image
runs render as their own literal characters, links keep only `http`/`https`/relative
destinations with `rel="noopener noreferrer"`, and every artifact path and filename is
generated from the asset id and revision, never from owner text. The audit row names the
revision and approval in the same vocabulary as the Markdown export, never the prose.
Deployment stays `deployable: false`; there is no remote, no push and no publication.

`narrative-memory` is archived (`a7d12f4`, canonical spec
`openspec/specs/narrative-memory/spec.md`). Read that package's own boundary before extending it,
and note what it decided that a later reader would otherwise have to rediscover.

**Release evidence (`narrative-memory`, commit `a7d12f4`, all run by the implementer rather than
taken on report).** `CHRONICLEAF_E2E=1 bash scripts/verify.sh` exit 0 with the platform-feed stage
and twenty-six filtered stages each asserted to select at least one test: 15/15 shell, 24/24
persistence, 23/23 access, 19/19 projects, 12/12 jobs, 9/9 media, 14/14 evidence, 32/32 vault,
22/22 voice, 26/26 provider contract, 23/23 OpenAI, 24/24 Gemini, 24/24 Anthropic, 23/23
OpenRouter, 26/26 Ollama, 27/27 GitHub evidence, 18/18 GraphQL enrichment, 21/21 local Git,
52/52 capture, 18/18 curation, 30/30 drafts, 7/7 timeline, 11/11 review-export, **7/7 narrative
memory**, 10/10 compose-release, 40/40 release candidates — 557 in total. `npm ci`, `tsc` and
`vite build` all green. `bash scripts/e2e.sh` green: 65/65 Playwright across thirteen
database-isolated browser suites (21/21 shell/access/projects/jobs, 3/3 evidence, 6/6 vault, 4/4
voice, 4/4 providers, 3/3 GitHub, 3/3 capture, 4/4 drafts, 4/4 curation, 3/3 timeline, 2/2
review-export, 5/5 candidates, **3/3 narrative**). `dotnet build --nologo --no-incremental` 0
errors with the same 2 pre-existing warnings at HEAD. Name preflight exit 0;
`openspec validate --all --strict --no-interactive` 54/54 before and after the archive, which
used no `--skip-specs`.

**Inheritance note.** This package was delivered across two worker passes because the first was
killed mid-flight by a server restart and left a substantial uncommitted implementation behind
(backend, migration, panel, 7 backend tests). The second pass verified it as inherited
(`dotnet build --nologo --no-incremental` 0 errors, 7/7 green on first run) and completed rather
than restarted it: the only new code is the browser suite (`tests/narrative-memory.spec.ts`,
suite 13 in `scripts/e2e.sh` on port 5093) plus one-line `verify.sh` comment coverage. No
inherited defect was found; nothing inherited was rewritten.

**One gate flake, recorded rather than hidden.** The first full-gate run failed the browser stage
while the standalone `scripts/e2e.sh` passed immediately before and twice after; the rerun gate
passed end to end (GATE_EXIT=0). The filtered gate output did not identify which suite tripped,
so no suite is blamed here — but a browser stage that fails once under full-gate load and passes
in isolation is load contention, not product behavior, until a second occurrence says otherwise.

**What the database now guarantees, not just the code.** `narrative_entries` carries the owner's
words with kind/visibility check constraints and a revision compare-and-set; `project_summaries`
is one row per project (unique index) with a SHA-256-hex input hash and a stale mark; a refresh
recomputes the hash from entry revisions, milestone revisions, evidence heads and live approval
excerpts, so a corrected decision, a revised milestone or a new bundle all invalidate the stored
body at the next run. Entry writes mark the summary stale; a failed build keeps the prior body
under the stale mark with `narrative_summary_build_failed` on the operation. `IProjectNarrativeContext`
is registered for future drafts callers but no drafts path consumes it yet — wiring it into
generation would change consented payloads, so it waits for an explicit package.

`local-git-evidence` is archived (`f7e4a81`, canonical spec
`openspec/specs/local-git-evidence/spec.md`). Read that package's own boundary before extending it, and
note six things it decided that a later reader would otherwise have to rediscover.

**The read is committed revisions only, and that is structural rather than a filter.** Every Git command
after the two `rev-parse` resolutions takes two SHAs — `git log <base>..<head>`, `git diff <base>..<head>
-- <paths>` — so an untracked file, a staged change and a dirty edit are not candidates for anything, and
there is no point in the code where they could be read and then decided against. The working tree is
touched exactly once, by a `status` whose lines are counted and dropped: the bundle says *N uncommitted
changes, deliberately not read*, and it is a number because one of those names could be a file nobody meant
to publish, and because a count is the only claim that stays true about an instant. A range log holds the
commits *after* the base, so a bundle carries the base in its metadata item and the head as a commit row —
asserting two commit rows would be a claim about a different collection, and the suite says so where it
asserts one.

**The read-only claim is enforced by a closed verb set, and proven by bytes.** `GitCommandRunner` refuses
any subcommand outside `rev-parse`, `log`, `diff` and `status` before a process exists, so there is no code
path by which this product fetches, pushes, checks out, resets, cleans or gc's a repository. Each process is
started from an `ArgumentList` — never a composed string, so there is no shell — with the pager,
`credential.helper`, `diff.external`, textconv and `core.hooksPath` all off, and standard input closed. The
scenarios prove it by hashing every byte under the repository including `.git` before and after a
collection, not by counting commands: a count of commands only proves this repository's own idea of what a
mutating command is. `git diff` is given a range and then `--` and *then* the paths, in that order, because
Git reads everything after a bare `--` as a pathspec — a range placed after it produces an empty diff, an
exit code of zero and no error anywhere, which is the same silent-no-op class as a filter that matches
nothing, one layer down.

**Containment is asked twice, and a symlink is followed one segment at a time.** `RepoRef.Local` refuses
traversal as pure text; `LocalGitPolicy.ResolveWithin` then resolves the path segment by segment, following
links and re-testing containment after each step, so `root/link/notes.md` is refused at the step the link is
followed rather than at the end — a check that resolved only the final component would call it contained.
The check runs again immediately before every read, because a path inside a root when it was registered can
be a link out of it by the time a job runs, and that case has its own scenario. An absent path and an
escaping path are different answers on purpose: one is a fact about a directory (`not_a_repository`, 422)
and one is a refusal to look outside (`outside_root`, 403). The same reasoning fixes the statuses: an
unregistered alias is a 404, because the request named a root that does not exist here rather than a
malformed body, and a 422 would send the caller to change a name that was fine. `.` is the one accepted dot
segment and it means the registered root itself, and the database check constraint agrees.

**One transport, two registration names, because a shadowed slot is a silent no-op.** `LocalGitEvidenceSource`
satisfies the delivered `IGitEvidenceSource` *and* the new `ILocalGitSource`, and the composition root
registers it under the local name only — `IGitEvidenceSource` is the hosted transport's, and a second
registration under that name would move every GitHub collection onto a directory. This repository has been
bitten by the same class three times, so the suite asserts it from the other side: in a production-built
container, `IGitEvidenceSource` is still `GitHubEvidenceClient` and `ILocalGitSource` is the local one.
A future package that wants a third transport should give it its own registration name and assert the
others are still what they were.

**A moved head is a permanent, named outcome, and it must be checked before the generic classification.**
`GitEvidenceOutcome.Superseded` was added to the leaf contract for this, because a local collection is
several commands over some seconds rather than one HTTP call: a commit made in the middle would otherwise
produce a bundle whose items name one revision and hold facts from another. `ThrowFor` handles it *and*
`CollectAsync` checks it before calling `ThrowFor`, because the first version of this package had only the
second and the `default:` arm of `ThrowFor` turned a moved head into `local_git_job_git_unavailable` —
transient, retried, and on the retry it succeeded by collecting a revision the owner never asked for. The
scenario that proves it uses the one seam these tests allow: a runner that *delegates* to the production one
and can move a head between two commands, because a race nothing outside the process can schedule has to be
scheduled from inside it.

**Two diff numbers, and the smaller one is the one that had to be chosen.** 256 KiB is the design's
*output* cap and bounds the pipe; a read the pipe truncated is a dependency failure, because a prefix of a
diff with nothing in it saying what is missing is exactly what the evidence contract exists to prevent. The
*evidence* budget is 128 KiB — half the delivered bundle's own text limit — because a patch budget equal to
the whole bundle limit leaves no room for the repository metadata, the commits, the exclusions and the
truncation records, and a repository with a large diff would fail the append instead of producing the honest
truncated bundle this package promises. The file cap counts every file recorded, an excluded one included:
counting only the readable ones would let a repository of a thousand secrets blow past the cap while
recording nothing about them.

**Release evidence (`local-git-evidence`, commit `f7e4a81`, all run by the implementer rather than taken on
report).** `bash scripts/verify.sh` exit 0 with the platform-feed stage and twenty-three filtered stages
each asserted to select at least one test: 15/15 shell, 24/24 persistence, 23/23 access, 19/19 projects,
12/12 jobs, 9/9 media, 14/14 evidence, 32/32 vault, 22/22 voice, 26/26 provider contract, 23/23 OpenAI,
24/24 Gemini, 24/24 Anthropic, 23/23 OpenRouter, 26/26 Ollama, 27/27 GitHub evidence, **21/21 local Git**,
52/52 capture, 18/18 curation, 30/30 drafts, 7/7 timeline, 11/11 review-export, 10/10 compose-release —
467 in total. `npm ci`, `tsc` and `vite build` all green. `dotnet build --nologo` succeeds with the
**same two warnings that are already at HEAD** (`ScreenshotCandidate._displayOrder` never assigned and the
one obsolete `HasCheckConstraint` in the curation configuration) and 0 errors; this package adds none, and
`--no-incremental` was used to make that claim a real one rather than a consequence of an incremental build
reporting nothing. Name preflight exit 0; `openspec validate --all --strict --no-interactive` 54/54 before
and after the archive, which used no `--skip-specs`.

**Release evidence (`github-release-candidates`, commit `08651c4`, all run by the implementer
rather than taken on report).** `bash scripts/verify.sh` exit 0 with the platform-feed stage and
twenty-four filtered stages each asserted to select at least one test (15/15 shell, 24/24 persistence,
23/23 access, 19/19 projects, 12/12 jobs, 9/9 media, 14/14 evidence, 32/32 vault, 22/22 voice, 26/26
provider contract, 23/23 OpenAI, 24/24 Gemini, 24/24 Anthropic, 23/23 OpenRouter, 26/26 Ollama, 27/27
GitHub evidence, 21/21 local Git, 52/52 capture, 18/18 curation, 30/30 drafts, 7/7 timeline, 11/11
review-export, 10/10 compose-release, **40/40 release candidates** — 507 in total). `npm ci`, `tsc`
and `vite build` green; `dotnet build --nologo --no-incremental` 0 errors with the same 2 pre-existing
warnings at HEAD. `bash scripts/e2e.sh` green on the final code: 56/56 Playwright across twelve
database-isolated browser suites (21/21 shell/access/projects/jobs, 3/3 evidence, 6/6 vault, 4/4 voice,
4/4 providers, 3/3 GitHub, 3/3 capture, 4/4 drafts, 4/4 curation, 3/3 timeline, 2/2 review-export, **5/5
candidates**). Name preflight exit 0; `openspec validate --all --strict --no-interactive` 54/54 before
and after the archive, which used no `--skip-specs`. Canonical spec
`openspec/specs/github-release-candidates/spec.md`.

**Four defects the inherited work carried, none worked around in a test.** The transport registration
used `TryAddSingleton`, which keys on the service type and silently installs nothing once claimed —
now plain `AddSingleton`, per the standing rule. The new design-time factory hardcoded a placeholder
connection string, so `dotnet ef database update` dialled the wrong server and `scripts/e2e.sh` died
at suite 1 — it now honors `ConnectionStrings__Chronicleaf` for real migrations. The candidate seed
connected a fictional repository the connect route honestly refuses with 422 — it connects the same
real public `octocat/Hello-World` suite 6 uses. And the candidate list returned an empty page for a
project with no connection, so the panel could never show its no-connection state — the list and
count now refuse `github_candidate_connection_missing` 422 by name, with a new .NET scenario asserting
it. Two browser-suite defects of the same unrun-suite class: the spec clicked a dashboard link where
the product renders buttons, and its forbidden-words check matched honest prose ("published") instead
of controls — both fixed in the spec, and the panel no longer renders both empty states at once.
One environment flake recorded: a curation reorder poll failed once and passed on both adjacent runs
(4/4 before, 4/4 after); untouched by this change.

Next: `current_spec` advances to `narrative-memory` (depends only on archived
`grounded-markdown-drafts` + `markdown-review-export`). `github-graphql-enrichment` is archived
(`c7a97b8`, canonical spec `openspec/specs/github-graphql-enrichment/spec.md`): optional
relationship-only enrichment of already-collected evidence through one fixed named GraphQL
operation (`ChronicleafRelationshipEnrichment`, page literals in the query text, only owner/name/
validated-SHA/server-cursor vary), `POST /api/milestones/{id}/github-enrichment`
`{evidenceRevision,idempotencyKey}` → 202 `milestone.github-enrichment` job. Every SHA is
validated 40-hex before any request; partial errors become `unavailable` items in the package's
own reason vocabulary (never a response sentence); pagination dedups on provider node id;
permission problems end as `github_enrichment_job_permission_denied` with nothing appended; 429
waits the provider's reset via `OperationRateLimitedException`; a bundle that moves during the
query or between request and run is `github_enrichment_job_superseded` (run-row revision check
plus CAS append, milestone moves covered by the delivered input-revision check). No new
credential scope: the vault lease is one bearer header for one read. The reader reuses the
delivered GitHub `HttpClient` (no second client, plain `AddSingleton`, `IGitEvidenceSource`
still `GitHubEvidenceClient`); the run row is `github_enrichment_runs` (new required migration
`20261010090000`, schema const + `ExpectedMigrationId` advanced). Read that package's boundary
before extending it. No Web UI was added: the appended issue/PR items render in the existing
evidence panel, so no new Playwright suite exists and `CHRONICLEAF_E2E=1` was not run.

**Release evidence (`github-graphql-enrichment`, commit `c7a97b8`, all run by the implementer
rather than taken on report).** `dotnet build --nologo --no-incremental` 0 errors with the same
2 pre-existing warnings at HEAD (Core `_displayOrder`, obsolete `HasCheckConstraint`). `bash
scripts/verify.sh` exit 0 with `GATE_EXIT=0` and the new `github graphql enrichment tests` stage
**18/18** (`GithubGraphqlEnrichmentTests`: 001 success/failure/boundary, 002
success/failure/boundary plus stale-request supersede, 429-wait-then-retry, credential-lease,
registration, closed-code-set, 7 SHA-validation cases) against real disposable PostgreSQL and the
real API host with only the wire scripted; `npm ci`, `tsc` + `vite build` green. Name preflight
exit 0; `openspec validate --all --strict --no-interactive` 54/54 before and after the archive,
which used no `--skip-specs`. One test defect found by the run itself: a shared `JsonObject`
across two pages threw `The node already has a parent` — the fixture now builds one node per
page. Deployment stays `deployable: false`; there is no remote, no push and no publication.

**About that 2-warning baseline, recorded because HANDOFF's own numbers were wrong.** This file has
repeatedly claimed "0 warnings" for a build that never had them. The first full-gate run of
`local-git-evidence` printed both, on the *first* compile of `Chronicleaf.Core` and
`Chronicleaf.Infrastructure`; a second `dotnet build` in the same tree prints none, because nothing
recompiles. So a green `dotnet build` after a previous one is not evidence about warnings at all, and the
`--no-incremental` run above is the only form of that check that says anything. Both warnings are in
delivered, archived code and were not touched here.

**What the local-git stage is, and what it is not.** There is no double anywhere in it. The repository on
disk was created by a real `git` in a real temporary directory by a fixture with its own independent `git`
invocation (so a bug in the production runner cannot produce an empty repository that the scenario then
"proves" was read correctly), the application reads it through the production runner over the real
`GitCommandRunner`, and the only seam is the delegating head-mover used by one scenario. What it is not
evidence for: that Git works, that any particular repository is well formed, or that one distribution's Git
behaves as another's. `CHRONICLEAF_E2E=1` was **not** run, so there is no browser fixture for the new
panels and no Playwright evidence; `tsc` and `vite build` are the frontend evidence, and a suite is one
`scripts/e2e.sh` invocation plus its own seed away. Two other records: `git diff --check` reports the
trailing blank line on the generated canonical spec, which is the tool's archive output and is true of all
twenty-four; and eleven of the twenty-four canonical specs carry openspec's own `TBD - created by archiving`
purpose line, including the three before this one, so leaving it is the repository's existing behaviour
rather than a gap.

`cloud-object-storage` is archived (`bdfcec9`, canonical spec
`openspec/specs/cloud-object-storage/spec.md`). Read that package's own boundary before
extending it, and note what it decided that a later reader would otherwise have to rediscover.

**Release evidence (`cloud-object-storage`, commit `bdfcec9`, all run by the implementer
rather than taken on report).** `bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`) with the
platform-feed stage and the new `cloud object storage tests` stage alongside all existing
stages, each filtered stage asserted to select at least one test, including **11/11 cloud
object storage** run directly against the same real disposable PostgreSQL and the real API
host (S3-mode upload storing the decoded type and hash under the server-generated scoped key
with the row naming `s3` and the bytes asserted in MinIO under the same key; foreign-scope
read and presign both 404 with zero backend calls past the upload's own; 360s/zero/garbage
lifetimes 422 with zero presigns minted and a lawful mint carrying its expiry; 1s MinIO URL
fetchable now and 403 after the second passes; limit-1 migration flipping one row to `s3`
with the ledger's digest/key/state and the source bytes intact while each backend serves its
own rows, then finishing and idling; refused-write and corrupted-copy runs keeping `local`
with `failed` ledger entries and resuming to verified after the fault clears; Community host
with no S3 settings storing/serving/retiring locally, no `IS3BackendFactory` registered, and
presign 503; sealed `s3` document absent unaligned from ciphertext/nonce and from every text
column and message while the production bridge builds a working backend from it; github-slot
reference and missing reference both 503 with no secret in the message; referenced S3 image
409 `media_in_use` then retired with the object gone; interrupted S3 upload reconciled
idempotently through the recorded pending row). Neighbour suites re-run against the same
build: 9/9 local media storage, 32/32 credential vault, 9/9 cloud tenant boundary, 12/12
team project permissions. `npm ci`, `tsc` and `vite build` all green (no Web changes).
`dotnet build --nologo --no-incremental` 0 errors with the same 2 pre-existing warnings at
HEAD. Name preflight exit 0; `openspec validate --all --strict --no-interactive` 54/54 before
and after the archive, which used no `--skip-specs`. No new browser suite: the scenarios are
API-observable and the design names UI optional. Deployment stays `deployable: false`; there
is no remote, no push and no publication.

**What the S3 package owns, and what it deliberately does not.** The adopted
`Platform.Storage.S3` adapter is reached through a backend the factory builds per call over
a leased vault document, and the lease is returned before the first request - a backend
built once at start-up would hold keys for the life of the process. Tenants share one
bucket by key prefix, never by credential: the credential is the operator's single vault
reference, and isolation is the `scopes/{scope}/` prefix checked on every stored key, not a
second bucket per tenant. The ledger can resume work but can never declare bytes good: a
`verified` entry still re-verifies the target before the row flips. Rollback is
registration-only (S3 mode off serves everything from disk; rows, ledger and evidence stay),
and there is no migration route or schedule - the service is operator tooling behind the
same seam the orphan reconciler keeps. The `media.provider_id` column defaults `local`, so
every pre-existing row and every old INSERT reads exactly what it always read.

`hosted-archive-operations` is archived (`2db6039`, canonical spec
`openspec/specs/hosted-archive-operations/spec.md`). Read that package's own boundary before
extending it, and note what it decided that a later reader would otherwise have to rediscover.

**Release evidence (`hosted-archive-operations`, commit `2db6039`, all run by the implementer
rather than taken on report).** `bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`,
`set -o pipefail`) with the platform-feed stage and the new `hosted archive operations tests`
stage alongside all existing stages, each filtered stage asserted to select at least one test,
including **17/17 hosted archive operations** run directly against the same real disposable
PostgreSQL and the real API host (measured/recorded/rehearsed backup proving the live scope
still hashes to both recorded digests with the stamp reread from the row; unknown key 999,
unknown schema and media/content drift each refused without promotion; recording alone reading
unverified; foreign-scope rows moving neither digest with a foreign manifest id answering null
to this scope; confirmed delete suspending a live journey and an enabled schedule while
evidence, media bytes, the sealed credential and the project revision all still read back;
wrong-name 422, stale-revision 409, missing-If-Match 428, second-delete
`archive_already_deleted` 409, anonymous 401, never-deleted 404; restore inside the window
lifting the deletion with the erase request withdrawn and a second restore 404; restore past
the window `archive_restore_expired` 410 with the tombstone still live; foreign restore and
read the delivered 404 with no foreign id or name in the body and the foreign row untouched;
purge after the window retiring only the unreferenced media with the referenced picture still
serving, bundles/items/approvals retained and the receipt itemizing them; purge inside the
window, under a live approval and under a hold each skipping with their reason, the approval
then withdrawn and the hold then cleared both purging on the next pass). Neighbour suites
re-run against the same build by the full gate (all prior filtered stages green, unchanged).
`npm ci`, `tsc` and `vite build` all green (no Web changes). `dotnet build
--nologo --no-incremental` 0 errors with the same 2 pre-existing warnings at HEAD. Name
preflight exit 0; `openspec validate --all --strict --no-interactive` 54/54 before and after
the archive, which used no `--skip-specs`. No new browser suite: the scenarios are
API-observable and manifests are operator rows with no route by design. Deployment stays
`deployable: false`; there is no remote, no push and no publication.

**What the archive package owns, and what it deliberately does not.** Recovery is
byte-identical or it fails loudly: the rehearsal recomputes the media and content digests
from the live scope and compares all four values (schema, key, media, content), and any
drift keeps the old stamp rather than merging a new one. Deletion suspends rather than
erases: the tombstone, the revoked journey and the disabled schedule are the whole delete,
and units of work never nest — each suspension owns its own commit through its delivered
service, sequenced suspend-first/tombstone-last so a retry finishes rather than half-writes.
The purge keeps what it cannot prove is eligible: an open window, a hold, a live approval
or a live projection each skip with a receipted reason, and only unreferenced media retires
through the delivered store that refuses referenced bytes with `media_in_use`. Scope
credentials are never retired by a project deletion — they belong to the scope, and the
suite asserts one still resolves afterwards. Rollback is registration removal (no hosted
loop, no transport, no schedule); the operator's backup runner and purge pass are methods,
not loops. Five envelope codes (`archive_key_mismatch`, `archive_media_mismatch`,
`archive_schema_mismatch`, `archive_already_deleted`, `archive_restore_expired`) with
explicit middleware arms; the foreign refusal reuses the delivered 404.

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

`github-app-connection` is archived
(`f5a93d4`, canonical spec `openspec/specs/github-app-connection/spec.md`): scoped GitHub App
installation connections, with Community PAT behavior byte-identical. One
`github_app_installations` row per bound installation carrying the scope, the installation id
(globally unique: the first binding wins and a guessed id from another scope is
`installation_mismatch` 403), the account login, the selection and a live/removed status, plus
`auth_mode` (`pat` backfilled) and `installation_id` columns on `github_connections` under a
`pat|installation` check constraint. Owner `GET .../github-app/setup` issues a 32-byte
single-use state bound to the session with a 10-minute TTL; `POST .../github-app/connect
{installationId, repositoryId, state}` consumes it (replayed/expired/foreign is
`github_app_state_invalid` 400), reads the installation as the App itself through a BCL-only
JWT (`Chronicleaf.GitHub/App`, RS256, no JWT package), mints a token naming exactly one
`repository_ids` entry with no permission, and reads that repository through it before storing
anything (unknown installation 404, unselected repository 404). `IGitHubInstallationTokenProvider`
serves the token from a process-wide cache until 60s before GitHub expiry with a per-key gate,
so a concurrent refresh mints once; no token reaches a row, a log, a payload or an exception.
Collection through an installation reuses the delivered `milestone.github-sync` job and the
evidence append contract (revoked installation ends `github_job_connection_missing` permanent,
mint failure transient). The App PEM and webhook secret are delivered vault credentials under
new `github-app`/`github-app-webhook` slots, resolved by reference with no owner-scope
comparison; `POST /api/webhooks/github-app` verifies HMAC-SHA256 over the raw bytes (every
auth failure one 401) and a `deleted` delivery marks the row removed and deletes every bound
connection, so queued jobs fail without publishing. Read that package's own boundary before
extending it, and note the one defect verification found: concurrent refreshes through one
scope raced two reads on one DbContext (`NpgsqlOperationInProgressException`), so the binding
recheck moved inside the per-key gate and the single-mint test now shares one scope on purpose.
Evidence, all run by the implementer rather than taken on report: `bash scripts/verify.sh` exit
0 (`GATE_EXIT=0`, `set -o pipefail`) with the platform-feed stage and the new `github app
connection tests` stage (9/9 on three consecutive runs) alongside all existing stages (neighbors
re-run in isolation: 27/27 evidence, 40/40 candidates, 32/32 vault, 18/18 curation), `npm ci`,
`tsc` + `vite build`, `dotnet build --nologo --no-incremental` 0 errors with the same 2
pre-existing warnings at HEAD, name preflight exit 0, and `openspec validate --all --strict
--no-interactive` 54/54 before and after the archive, which used no `--skip-specs`. No Web UI
was shipped: the design's React panel is satisfied API-observable (setup/connect/read/disconnect
plus the delivery receipt), and no new browser suite was added. The opt-in real-GitHub run was
not performed: only the wire bytes are scripted and no claim is made about any real installation
or mint. Deployment stays `deployable: false`; there is no remote, no push and no publication.
`linkedin-copy-package` is archived
(`637a1fc`, canonical spec `openspec/specs/linkedin-copy-package/spec.md`): reviewed copy-ready
LinkedIn post as a manual content package, with Community behavior the only behavior (no Hosted
surface). Adapting an approved markdown-story renders deterministic plain-text copy (title bound
by the effective voice, summary + change + limitation citations, owner's verbatim CTA/hashtags,
footnote definitions) into a new `linkedin-copy` asset revision carrying the voice value
snapshot, with selected pictures in `linkedin_copy_media` (one row per picture per revision,
append-only, max 4, alt text 1..500); the derivative starts with no approval row and the
delivered edit/approve/review routes are reused unchanged. `GET export?format=linkedin` serves a
ZIP (`copy.txt` exact approved bytes, `images/*.png`, `manifest.json` with per-image digests)
under the same bindings the Markdown export re-checks; native publish is an unconditional 422
`publisher_unsupported` with no credential lease, no job, no audit row and no socket (there is
no HttpClient anywhere in the package). Length/hashtag rules are deterministic: hashtags
`#[A-Za-z][A-Za-z0-9_]*` max 5 exact-deduped, CTA max 280 chars, internal 10000-char ceiling
(`linkedin_copy_too_long` 422) with warning at 9000, and the composed bytes re-pass the package's
own grounding validator so an owner digit the evidence never stated refuses before storage. Read
that package's own boundary before extending it, and note the three defects verification found:
the migration shipped without the `[Migration]` discovery attribute (table silently absent,
adapt 500 — Designer added, model byte-identical since the table is raw-SQL-only); each new
envelope code needs an explicit middleware type-arm (a bare `ApiException` answers 500, so
`CopyTooLong` is its own exception type); and the summary schema caps change text at 8000 chars,
so the length fixtures use 4500/6000-char texts. Also recorded, not hidden: the access boundary
audits its own per-mutation antiforgery rotation, so the publish no-side-effect assertion counts
non-session audits. Evidence, all run by the implementer rather than taken on report:
`bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`) with the platform-feed stage
and the new `linkedin copy package tests` stage (10/10) alongside all existing stages (neighbors
re-run in isolation: variants/review-export/html-export/voice 53/53), `npm ci`, `tsc` + `vite
build`, `dotnet build --nologo --no-incremental` 0 errors with only the 6 pre-existing warnings
in untouched files, name preflight exit 0, and `openspec validate --all --strict
--no-interactive` 54/54 before and after the archive, which used no `--skip-specs`. No Web UI
was shipped: the design names it optional, the scenarios are API-observable, and no new browser
suite was added. No LinkedIn network exists to test against (structural: no transport), and the
opt-in real-provider/browser runs were not performed. Deployment stays `deployable: false`;
there is no remote, no push and no publication.
`medium-import-package` is archived
(`780b6fd`, canonical spec `openspec/specs/medium-import-package/spec.md`): manual Medium
article import material as an export-only package, with Community behavior the only behavior
(no Hosted surface). No new table and no migration: the package stores nothing, so the schema
is byte-identical and rollback is unregistering the one service. `GET export?format=medium`
re-serves the approved revision's exact bytes through the review package's own Markdown and
HTML exports - so never-approved is `approval_required`, an edited-then-unapproved head is
`approval_stale`, and missing picture bytes are `media_unavailable`, all re-checked rather
than redefined - then packs a deterministic ZIP (`article.html`, `article.md`, the
approval-covered `images/*` entries verbatim, `manifest.json` with per-file digests, and
`MEDIUM-IMPORT.txt` hand-carry instructions naming the asset, revision and body hash with
the official restriction that Medium accepts no new API integrations). The manifest
attributes an origin, never a destination: no URL, post id or publication status in any
entry, and the same revision packs byte-identically twice. Native publish
(`POST .../adaptations/medium/publish`) is an unconditional 422 `publisher_unsupported`
with no credential lease, no job, no audit row and no socket (there is no HttpClient
anywhere in the package); a foreign id stays the delivered 404. Behaviorally
supportsPublish=false/supportsDraft=false with exportFormats html|markdown, awaiting the
future publisher registry in `publisher-contract`, which this package does not anticipate in
code. Read that package's own boundary before extending it. Evidence, all run by the
implementer rather than taken on report: `bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`,
`set -o pipefail`) with the platform-feed stage and the new `medium import package tests`
stage (7/7) alongside all existing stages, `npm ci`, `tsc` + `vite build`, `dotnet build
--nologo --no-incremental` 0 errors with only the 6 pre-existing warning lines in untouched
files (2 src CS0649/CS0618 + 4 xUnit1030 in GithubAppConnectionTests.cs, doubled per
target), name preflight exit 0, and `openspec validate --all --strict --no-interactive`
54/54 before and after the archive, which used no `--skip-specs`. No Web UI was shipped:
the design's UI is optional, the scenarios are API-observable, and no new browser suite was
added. No Medium network exists to test against (structural: no transport, no scraping, no
browser posting). Deployment stays `deployable: false`; there is no remote, no push and no
publication.
Next: `current_spec` is `publisher-contract`, the next package in drain order after
`medium-import-package`.
`publisher-contract` is archived
(`8e53d58`, canonical spec `openspec/specs/publisher-contract/spec.md`): the contract all
six publisher adapters implement, with Community behavior the only behavior (Hosted adds
per-project read/publish grants through the delivered teams check). The adapter registry
(`IPublisherRegistry` plain singleton) classifies medium/linkedin as export-only; every
transport target is classified by its adapter's own `Describe()` where no descriptor was
contributed beside it, so the two can never drift. The reviewed-publication workflow
publishes only approved-while-bindings-hold content: preview is pure, publish freezes the
approval/body/evidence/preview bindings plus an explicit per-target confirmation into a
`publication_attempts` row (forward migration `20261019090000`, unique scope/key and
unique operation, terminal states immutable), the durable `publication.publish` run
re-checks every binding, leases the scope's `publisher` credential for the one call through
the vault-backed singleton source (deleted-between-request-and-run is the delivered
`credential_missing`), and records the receipt; an ambiguous timeout and a post-commit
crash both become `unknown` with exactly one dispatch and no retry (`publication_unknown`
on the attempt and the operation), and reconcile resolves where the target permits while a
target that cannot keeps `unknown` with a `manual` hint and a 422. Medium/LinkedIn publish
is refused with this package's own `publisher_target_export_only`, classified in the
registry rather than colliding with the export packages' `publisher_unsupported`. This
package ships no transport: the only adapter-shaped object is the test-local fake on
`test-fake`, proven test-only by the production host answering `publisher_unknown_target`
for its slot. Read that package's own boundary before extending it, and note the six
defects verification found, none worked around in a test: the registry first knew only its
two static descriptors, so the fake's slot answered `publisher_unknown_target` at preview
and at run time (adapter declarations are now authoritative where no descriptor exists,
export-only still winning over any declaration); the run path read the registry directly
and needed the same resolution; `TryCompleteAsync` only moved prepared/sending, so a
reconcile could never leave `unknown` (dedicated `TryReconcileAsync`, unknown to
published/remote-draft only); `ApprovedAssetSnapshot` already existed in the content
package, so the contract reuses the delivered snapshot with its picture bytes (missing
bytes are the delivered `media_unavailable`, read under the portable-artifact bound)
rather than duplicating it; the delivered conflict code is `operation_idempotency_conflict`
and `reconcileHint` serializes null rather than absent; and EF stamped the migration with
the machine clock (`20261006084950`), renamed to `20261019090000` so it sorts after the
LinkedIn package's. Evidence, all run by the implementer rather than taken on report:
`bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`) with the platform-feed
stage and the new `publisher contract tests` stage (18/18) alongside all existing stages,
`npm ci`, `tsc` + `vite build`, `dotnet build --nologo --no-incremental` 0 errors with
the same 12 pre-existing warning grep lines at HEAD (2 src + 4 xUnit1030 doubled, zero
added), name preflight exit 0, and `openspec validate --all --strict --no-interactive`
54/54 before and after the archive, which used no `--skip-specs`. No Web UI was shipped:
the design names it optional, the scenarios are API-observable, and no new browser suite
was added. The opt-in real-transport run was not performed (structural: no transport
exists) and no claim is made about any remote side. Deployment stays `deployable: false`;
there is no remote, no push and no publication.
Next: `current_spec` is `publisher-github-pages-adapter`, the next package in drain order
after `publisher-contract`.
`publisher-github-pages-adapter` is archived
(`6a4fb12`, canonical spec `openspec/specs/publisher-github-pages-adapter/spec.md`): the
first real transport behind the publisher contract, pushing the approved snapshot's
rendered files to the operator's explicit repository, branch and subdirectory through the
Git Data API. A new BCL-shaped leaf project `src/Chronicleaf.Publishers` (contract plus
`RepoRef` only, no vault/store/domain reach) holds the `github-pages` adapter, its
pinned-host transport, its explicit target options and its six redacted failure codes;
registration is demand-gated (absent `Chronicleaf:GitHubPages` section registers nothing
and the slot answers `publisher_unknown_target`, present-but-invalid fails startup) and
is the whole rollback lever. The preview is pure and binds the target line into its
warnings - which the contract hashes - so a post-preview reconfiguration is refused as
`publisher_preview_stale` before any job exists; the push is repository read, base read,
blobs, tree, commit and one `force=false` ref move, with the leased credential as the one
bearer header against the pinned host (owner/name segments, never a URL). Only the ref
move publishes: earlier transport failures are definitive `failed` receipts, and only a
lost ref-move answer becomes `unknown` carrying the content-addressed planned commit SHA,
which the credential-less reconciler resolves by asking the branch what it holds (a
private branch stays `unknown` rather than becoming a credentialed read). Draft mode is
unserved (`publisher_mode_unsupported`), a moved branch is `github_pages_upstream_moved`
with no retry, and no per-target table was added so no migration was needed (schema
unchanged). Read that package's own boundary before extending it, and note the two defects
verification found, both test-expectation corrections rather than product changes: the
commit-POST timeout was first asserted `unknown`, but dangling objects without a ref are
unreachable so it is definitively `failed` (ambiguity starts at the ref write, where the
planned SHA is persisted); and the picture push was asserted as three blobs, but one
picture plus one document is two. Evidence, all run by the implementer rather than taken
on report: `bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`) with the
platform-feed stage and the new `publisher github pages adapter tests` stage (12/12)
alongside all existing stages, `npm ci`, `tsc` + `vite build`, `dotnet build
--nologo --no-incremental` 0 errors with the same 12 pre-existing warning grep lines at
HEAD (zero added), name preflight exit 0, and `openspec validate --all --strict
--no-interactive` 54/54 before and after the archive, which used no `--skip-specs`. No
Web UI was shipped: no publisher UI exists to extend, the scenarios are API-observable,
and no new browser suite was added. The opt-in real-GitHub run was not performed: only
the far end of the wire is scripted and no claim is made about any real repository, any
Pages build, or any live-site readiness. Deployment stays `deployable: false`; there is
no remote, no push and no publication.
Next: `current_spec` is `publisher-devto-adapter`, the next package in drain order after
`publisher-github-pages-adapter`.
`publisher-devto-adapter` is archived
(`b880186`, canonical spec `openspec/specs/publisher-devto-adapter/spec.md`): the second
transport behind the publisher contract, creating one Forem article per confirmed publish -
`published: false` for a draft, `true` only for an explicitly confirmed publish - through
`POST https://dev.to/api/articles` with the leased key as the one `api-key` header (never a
bearer scheme, never a query parameter) and `Accept: application/vnd.forem.api-v1+json`
against the pinned host. A new `DevTo/` folder in the delivered `Chronicleaf.Publishers`
leaf holds the `devto` adapter, its pinned-host transport, its explicit tag options (at most
four lowercase-alphanumeric tags, validated until the provider reports support for more)
and its five redacted failure codes; registration is demand-gated (absent
`Chronicleaf:DevTo` section registers nothing and the slot answers
`publisher_unknown_target`, present-but-invalid fails startup) and is the whole rollback
lever. The preview is pure and binds the tag line into its warnings - which the contract
hashes - so a post-preview tag change is refused as `publisher_preview_stale` before any
job exists; pictures do not travel and are named in the warnings instead, so the confirmed
preview consents to a text-only article. Every publish is a create: the transport exposes
no PUT, because an update would overwrite remote text that may no longer be approved while
a duplicate create is at worst a second draft. Only the create answers ambiguously: every
answered status is the provider's verdict (`401/403` credential rejected, `422` invalid
payload, `429` rate limited, `3xx` redirect refused without a second hop, anything else
unavailable) with error bodies disposed unread, while a lost answer - and a `201` in JSON
this client does not understand - becomes `unknown` with a `manual` hint that no reconcile
can resolve (`SupportsReconcile: false`, the route answers
`publisher_reconcile_unavailable` with no further upstream call). No per-target table was
added so no migration was needed (schema unchanged). Read that package's own boundary
before extending it, and note the three decisions implementation held before green: the
options stay a flat comma string rather than a bound list, because the composition root's
`Configure` cannot fill a list from the one key operators set (parsing and validation live
in `Create`, re-run on every call); the transport owns its client as its own singleton
rather than behind a bare `HttpMessageHandler`, because that registration is already taken
by the GitHub Pages transport and a second one would make single-handler resolution
order-dependent; and an unreadable `201` returns null so the adapter records `unknown`
rather than `failed`, because the article may exist and `failed` would invite a second
create of the same text. Evidence, all run by the implementer rather than taken on report:
`bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`, 48 stages) with the
platform-feed stage and the new `publisher devto adapter tests` stage (11/11, filter
asserted to select 11) alongside all existing stages - including 18/18 publisher contract
and 12/12 GitHub Pages on isolated runs - `npm ci`, `tsc` + `vite build`, `dotnet build
--nologo --no-incremental` 0 errors with the same 12 pre-existing warning grep lines at
HEAD (zero added), name preflight exit 0, and `openspec validate --all --strict
--no-interactive` 54/54 before and after the archive, which used no `--skip-specs`. No
Web UI was shipped: no publisher UI exists to extend, the scenarios are API-observable,
and no new browser suite was added. The opt-in real-Dev.to run was not performed: only
the far end of the wire is scripted and no claim is made about any real article, any tag
beyond the validated bound, or any rate-limit reset behavior. Deployment stays
`deployable: false`; there is no remote, no push and no publication.
Next: `current_spec` is `publisher-hashnode-adapter`, the next package in drain order after
`publisher-devto-adapter`.
`publisher-hashnode-adapter` is archived
(`f1d990c`, canonical spec `openspec/specs/publisher-hashnode-adapter/spec.md`): the third
transport behind the publisher contract, creating one draft per confirmed publish under the
operator's explicit publication id through `POST https://gql-beta.hashnode.com/` - stopping
after `createDraft` for a draft, continuing to `publishDraft` for the created draft only on
an explicitly confirmed publish - with the leased personal access token as the one bearer
header against the pinned host. A new `Hashnode/` folder in the delivered
`Chronicleaf.Publishers` leaf holds the `hashnode` adapter, its pinned-host GraphQL
transport (both documents are fixed literals with values in variables only; no pagination
exists in either mutation so no page size is parameterized), its explicit publication-id
options and its seven redacted failure codes (`401`/UNAUTHENTICATED credential rejected,
`403`/non-plan FORBIDDEN publish forbidden because a contributor role never publishes,
plan-caused FORBIDDEN capability unavailable, `422`/validation invalid payload, `429`
rate limited, `3xx` redirect refused without a second hop, anything else unavailable, with a
GraphQL errors array on an HTTP 200 classified as an answered refusal and error bodies
disposed unread in every branch); registration is demand-gated (absent
`Chronicleaf:Hashnode` section registers nothing and the slot answers
`publisher_unknown_target`, present-but-invalid fails startup) and is the whole rollback
lever. The preview is pure and binds the publication line into its warnings - which the
contract hashes - so a post-preview publication change is refused as
`publisher_preview_stale` before any job exists; pictures do not travel and are named in
the warnings instead, so the confirmed preview consents to a text-only post, and the post
URL is derived from `post.url`, never guessed from a slug. Only the create answers
ambiguously without an id: a lost create answer becomes `unknown` with a `manual` hint no
reconcile can resolve (`SupportsReconcile: false`, the route answers
`publisher_reconcile_unavailable` with no further upstream call), while a publish-mode run
whose create succeeded keeps its draft - a lost publish answer becomes `unknown` carrying
the created draft id, and a refused publish mutation becomes `failed` carrying the refusal
code beside the draft id as a recoverable remote draft receipt - with no new create call
in either case, because the contract never reissues an attempt. No per-target table was
added so no migration was needed (schema unchanged). Read that package's own boundary
before extending it. Evidence, all run by the implementer rather than taken on report:
`bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`) with the platform-feed
stage and the new `publisher hashnode adapter tests` stage (14/14, filter asserted to
select 14) alongside all existing stages, `npm ci`, `tsc` + `vite build`, `dotnet build
--nologo --no-incremental` 0 errors with the same 12 pre-existing warning grep lines at
HEAD (zero added), name preflight exit 0, and `openspec validate --all --strict
--no-interactive` 54/54 before and after the archive, which used no `--skip-specs`. No
Web UI was shipped: no publisher UI exists to extend, the scenarios are API-observable,
and no new browser suite was added. The opt-in real-Hashnode run was not performed: only
the far end of the wire is scripted and no claim is made about any real draft, any real
post, any publication capability, or any rate-limit behavior. Deployment stays
`deployable: false`; there is no remote, no push and no publication.
Next: `current_spec` is `publisher-ghost-adapter`, the next package in drain order after
`publisher-hashnode-adapter`.
`publisher-ghost-adapter` is archived
(`49707ac`, canonical spec `openspec/specs/publisher-ghost-adapter/spec.md`): the fourth
transport behind the publisher contract, creating one Admin post per confirmed publish
under the operator's explicit HTTPS origin and API version through `POST
{origin}/ghost/api/admin/posts/?source=html` - status `draft` for a draft, `published`
only on an explicitly confirmed publish - with the approved title verbatim, the shared
HTML export's sanitized article body, the operator's tags, and one short-lived
`Authorization: Ghost` token signed per call (HS256, `kid` naming the integration id,
`iat` now, `exp` five minutes later, `aud` `/admin/`, over the hex-decoded secret of the
leased `id:secret` key; BCL-only, no JWT package). A new `Ghost/` folder in the delivered
`Chronicleaf.Publishers` leaf holds the `ghost` adapter, its origin-per-call transport
(no base address - every request URI is built absolute from the re-validated target;
redirects refused, no cookies, no bare-handler registration), its origin/version/tags
options, its short-lived token signer and its eight redacted failure codes (`401`
credential rejected - including a stored key that is not an `id:hex-secret` pair, refused
before any byte leaves; `403` publish forbidden; `400`/`415`/`422` invalid payload;
`409` update conflict; `429` rate limited; `3xx` redirect refused without a second hop;
anything else unavailable; plus `ghost_incompatible_target` for a well-formed but
unsupported API major, refused before any byte leaves rather than a guessed endpoint;
error bodies disposed unread in every branch); registration is demand-gated (absent
`Chronicleaf:Ghost` section registers nothing and the slot answers
`publisher_unknown_target`, present-but-invalid fails startup) and is the whole rollback
lever. The preview is pure and binds the origin, version and exact rendered HTML byte
count into its warnings - which the contract hashes - so a post-preview origin change is
refused as `publisher_preview_stale` before any job exists; pictures do not travel and
are named in the warnings instead, so the confirmed preview consents to an HTML-text-only
post rendered with an empty picture set (no packaged section, no relative image path),
and the post URL is derived from the post's `url`, never guessed from a slug. The
transport exposes no PUT: the contract dispatches each attempt exactly once, so every
publish is a create and the provider's `updated_at` guard stays provider-side. Only the
create answers ambiguously: a lost create answer becomes `unknown` with a `manual` hint
no reconcile can resolve (`SupportsReconcile: false`, the route answers
`publisher_reconcile_unavailable` with no further upstream call), and an answered success
in JSON this client does not understand becomes `unknown` rather than a second create.
No per-target table was added so no migration was needed (schema unchanged). Read that
package's own boundary before extending it. Evidence, all run by the implementer rather
than taken on report: `bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`,
run twice) with the platform-feed stage and the new `publisher ghost adapter tests` stage
(12/12, filter asserted to select 12) alongside all existing stages, `npm ci`, `tsc` +
`vite build`, `dotnet build --nologo --no-incremental` 0 errors with the same 12
pre-existing warning grep lines at HEAD (zero added), name preflight exit 0, and
`openspec validate --all --strict --no-interactive` 54/54 before and after the archive,
which used no `--skip-specs`. No Web UI was shipped: no publisher UI exists to extend,
the scenarios are API-observable, and no new browser suite was added. The opt-in
real-Ghost run was not performed: only the far end of the wire is scripted and no claim
is made about any real post, any real origin, any version beyond the scripted
`Accept-Version`, or any rate-limit behavior. Deployment stays `deployable: false`;
there is no remote, no push and no publication.
Next: `current_spec` is `publisher-custom-blog-adapter`, the next package in drain order
after `publisher-wordpress-adapter`.
`publisher-wordpress-adapter` is archived
(`e99205b`, canonical spec `openspec/specs/publisher-wordpress-adapter/spec.md`): the fifth
transport behind the publisher contract, creating one REST post per confirmed publish under
the operator's explicit HTTPS origin and username through `POST
{origin}/wp-json/wp/v2/posts` - status `draft` for a draft, `publish` only on an
explicitly confirmed publish - with the approved title verbatim, the shared HTML export's
sanitized article body, the operator's slug and taxonomy, and one Basic credential pair
per call from the configured user and the leased spaced application password (verbatim,
interior spaces untouched). A new `WordPress/` folder in the delivered
`Chronicleaf.Publishers` leaf holds the `wordpress` adapter, its origin-per-call transport
(no base address - every request URI is built absolute from the re-validated target;
redirects refused, no cookies, no bare-handler registration), its
origin/username/slug/categories/tags options (slug lowercase kebab, taxonomy two flat
comma-separated id lists, present-but-invalid fails startup) and its seven redacted
failure codes (`401` credential rejected - including a leased credential naming no
password, refused before any byte leaves; `403` publish forbidden; `400`/`415`/`422`
invalid payload; `409` update conflict; `429` rate limited; `3xx` redirect refused without
a second hop; anything else unavailable; error bodies disposed unread in every branch);
registration is demand-gated (absent `Chronicleaf:WordPress` section registers nothing and
the slot answers `publisher_unknown_target`) and is the whole rollback lever. The preview
is pure and binds the origin, user, slug, taxonomy and exact rendered HTML byte count into
its warnings - which the contract hashes - so a post-preview origin or taxonomy change is
refused as `publisher_preview_stale` before any job exists and before any password is
paired; pictures do not travel and are named in the warnings instead, so the confirmed
preview consents to an HTML-text-only post rendered with an empty picture set (no packaged
section, no relative image path), and the post URL is derived from the post's `link` (https
only), never guessed from a slug. The transport exposes no PUT: the contract dispatches
each attempt exactly once, so every publish is a create. Only the create answers
ambiguously: a lost create answer (timeout or reset after the bytes may have left) becomes
`unknown` with a `manual` hint no reconcile can resolve (`SupportsReconcile: false`, the
route answers `publisher_reconcile_unavailable` with no further upstream call), and an
answered success in JSON this client does not understand becomes `unknown` rather than a
second create; an identical resubmit returns the stored receipt with no second upstream
call. No per-target table was added so no migration was needed (schema unchanged). Read
that package's own boundary before extending it, and note what the inheriting worker kept
and fixed: two prior workers were killed mid-flight leaving uncommitted work (5 adapter
files, the 1590-line test file, the verify.sh stage, the Program.cs registration); every
one of the 7 ticked tasks was re-verified with real evidence and all held, so nothing was
unticked and no inherited file needed a defect fix - the only edits were ticking 4.2/4.3
in tasks.md itself. Evidence, all run by the implementer rather than taken on report:
`bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`, run twice) with the
platform-feed stage and the new `publisher wordpress adapter tests` stage (12/12, filter
asserted to select 12) alongside all existing stages, `npm ci`, `tsc` + `vite build`,
`dotnet build --nologo --no-incremental` 0 errors with the same 12 pre-existing warning
grep lines at HEAD (zero added, baseline confirmed via stash), name preflight exit 0, and
`openspec validate --all --strict --no-interactive` 54/54 before and after the archive,
which used no `--skip-specs`. No Web UI was shipped: no publisher UI exists to extend,
the scenarios are API-observable, and no new browser suite was added. The opt-in
real-WordPress run was not performed: only the far end of the wire is scripted and no
claim is made about any real post, any real site, any taxonomy beyond the scripted ids,
or any rate-limit behavior. Deployment stays `deployable: false`; there is no remote, no
push and no publication.
`publisher-custom-blog-adapter` is archived
(`7350a78`, canonical spec `openspec/specs/publisher-custom-blog-adapter/spec.md`): the sixth
and last transport behind the publisher contract, one `IPublisherAdapter` for the `custom-blog`
slot speaking the Chronicleaf Publisher HTTP v1 contract under the operator's explicit fixed
HTTPS origin (`Chronicleaf:CustomBlog`), sending the approved Markdown verbatim with one bearer
credential per call from the scope's leased API key. Demand-gated like its siblings: an absent
section registers nothing and the slot answers `publisher_unknown_target`; a present-but-invalid
section fails startup. The formats allowlist defaults to `markdown`, is normalized to lowercase
without duplicates, and must name `markdown` - the only format this adapter sends - so an
allowlist that could never authorize a send is a startup refusal. Every publish first probes
`GET /chronicleaf/v1/capabilities` under the same bearer credential and refuses an incompatible
server with `customblog_incompatible_remote_contract` before any post bytes leave (schemaVersion
1, idempotentWrites true, and a format on the allowlist required; a refused key, forbidden write
or redirect on the probe reports `credential_rejected`, `publish_forbidden` or `redirect_refused`
with the same precision as on the create). The create is then one
`POST /chronicleaf/v1/posts` carrying `{schemaVersion:1, operationId, contentHash, title, markdown,
mediaManifest:[], mode}` with `mode` draft or publish by the authorized mode and `Idempotency-Key`
naming the operation the server reconciles by; pictures do not travel and are named in the preview
warnings instead, so the confirmed preview consents to a Markdown-text-only post. The preview's
target line binds origin, allowlist and exact Markdown byte count, so an origin or formats change
after preview is refused as `publisher_preview_stale` before any job exists and before any key is
paired. A refused key, rejected body, forbidden write, hash conflict (409), incompatible contract
(412) and rate limit each map to a redacted `customblog_*` code with no secret and no moved
approval; a redirect is refused without a second hop; error bodies are disposed unread. Only the
create answers ambiguously: a lost create answer (timeout or reset after the bytes may have left)
and an answered success in JSON this client does not understand each become `unknown` with a
`manual` hint no reconcile can resolve (`SupportsReconcile: false`, the route answers
`publisher_reconcile_unavailable` with no further upstream call - the server's operations endpoint
is for a human reconciliation by hand, carrying the operation id the accept response already named,
because the reconciler holds no credential); an identical resubmit returns the stored receipt with
no second probe and no second post. A remote id longer than the attempt column or a non-https URL
is not a receipt: the id is kept only when storable, the URL only when https within its column,
otherwise the answer is unknown rather than a second create. The transport owns its own client
with no base address and refuses redirects, so no bare-handler registration can rewire a sibling
transport; no HTML export enters this registration because the sent body is the approved bytes
verbatim. No per-target table was added so no migration was needed (schema unchanged). Read that
package's own boundary before extending it. Evidence, all run by the implementer rather than taken
on report: `bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`) with the
platform-feed stage and the new `publisher custom blog adapter tests` stage (15/15, filter
asserted by the gate to select at least one test) alongside all existing stages, `npm ci`,
`tsc` + `vite build`, `dotnet build --nologo --no-incremental` 0 errors with the same 12
pre-existing warning grep lines at HEAD (zero added, baseline confirmed before changes), name
preflight exit 0, and `openspec validate --all --strict --no-interactive` 54/54 before and after
the archive, which used no `--skip-specs`. No Web UI was shipped: no publisher UI exists to
extend, the scenarios are API-observable, and no new browser suite was added. The opt-in real
server run was not performed: only the far end of the wire is scripted and no claim is made about
any real server, any real post, any capability beyond the scripted documents, or any rate-limit
behavior. Deployment stays `deployable: false`; there is no remote, no push and no publication.
No `current_spec` remains: this was the last unblocked spec in the drain. Two changes stay ACTIVE
in `openspec/changes` and are held, not named as current: `semantic-narrative-retrieval` is BLOCKED
on owner decisions (demand, embedding model/provider/dimension and consent policy must be selected;
owner Chronicleaf maintainer), and `hosted-managed-ai` is BLOCKED on owner decisions (Managed AI
provider/model/quota/unit weights and financial settlement policy; owner Chronicleaf maintainer).
`evolution-gif-export` is archived
(`95350b3`, canonical spec `openspec/specs/evolution-gif-export/spec.md`): reviewed
chronological GIF evolution over the delivered curation, evidence and media contracts, with
Community behavior unchanged. One `evolution_gifs` row per render carrying the
`evolution-gif-manifest/1` JSON (frame order, per-frame media/caption/evidence revisions,
durations, loop, canvas, byte length, selection and artifact hashes), the deterministic GIF
bytes (`bytea`), canvas, frame count, total duration, loop flag, draft/approved status and
idempotency key under check constraints (status vocabulary, SHA-256 hex, positive dimensions,
2..12 frames, 1..60000ms) with project FK Restrict and the scoped unique key index; the
bytes live in this row because the delivered media store is PNG-only by its own contract and
widening that vocabulary is that package's boundary, and row plus bytes land in one INSERT
so a failed render stores nothing. `POST /api/projects/{id}/evolution/gifs` renders 2..12
confirmed same-project milestones in chronological order (or the owner's explicit manual
order) from selected pictures only - a candidate is never depicted and naming one is 422,
a milestone with no selected picture is `insufficient_selected` 422, missing bytes are
`media_unavailable` 409, over-cap durations/dimensions are 422/413 - with per-frame
500..5000 whole-centisecond milliseconds (null, one, or one per frame; total at most 60s)
and loop default false; the BCL-only GIF89a encoder uses the fixed grey-ramp palette,
max-dimension canvas with neutral centering, GCE delays and the NETSCAPE switch only when
looping, so an unchanged selection rebuilds byte-identically. `GET` read/list,
`POST .../approve` binding the exact selection hash, reviewable `GET .../preview` (the
animation, or `?mode=static` serving the exact first frame as PNG for reduced-motion review)
and gated `GET .../download` serving the exact approved bytes only while the approval still
describes the stored selection (`approval_required`/`approval_stale` under the review
package's codes). Read that package's own boundary before extending it, and note the three
defects verification found: GIF LZW decoders must widen their codes one table entry earlier
than the encoder (the decoder learns each entry a read later - the shared-threshold decoder
silently desynced past 9-bit codes, proven against Pillow as ground truth, which reads the
encoder's bytes pixel-perfect with correct delays and loop); the test probe read the GCE
packed byte as part of the delay; and a single `[1000]` duration is the legal one-value
form, so the wrong-count probe is three values for two frames. Evidence, all run by the
implementer rather than taken on report: `bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`,
`set -o pipefail`, 53 stages, every filtered stage asserted to select at least one test)
with the platform-feed stage and the new `evolution gif export tests` stage (10/10) alongside
all existing stages, `npm ci`, `tsc` + `vite build`, `dotnet build --nologo --no-incremental`
0 errors with the same 12 pre-existing warning grep lines at HEAD (zero added, baseline
confirmed before changes), name preflight exit 0, and `openspec validate --all --strict
--no-interactive` 54/54 before and after the archive, which used no `--skip-specs`. The
first gate run hit the recorded GithubEvidenceTests class-cleanup `TaskCanceledException`
with all 27 tests passed and nothing from this package in that stage; the second run was
fully green. No Web UI was shipped: the design's React preview is satisfied API-observable
(preview plus the static first-frame mode, asserted pixel-equal to the animation's first
frame), and no new browser suite was added - the reduced-motion browser assertion was not
run on this host, where real capture stays `capture_sandbox_unavailable` behind the
delivered seam. Deployment stays `deployable: false`; there is no remote, no push and no
publication.
`xiaohongshu-content-package` is archived
(`75a8fba`, canonical spec `openspec/specs/xiaohongshu-content-package/spec.md`): reviewed
hand-posted Xiaohongshu note pack over the delivered evidence, voice, asset, review, media
and curation contracts, with Community behavior unchanged. One `xiaohongshu-pack` asset per
milestone with draft revisions rendered deterministically (`xiaohongshu-pack/1`) from the
approved story's accepted summary plus the owner's chosen title (exactly one of 1..5 derived
candidates, retitle appends a revision), tags (0..10), cover line, call to action and image
order; one `xiaohongshu_pack_media` row per picture carrying the zero-based position, so the
export serves the owner's arrangement. `POST /api/assets/{id}/adaptations/xiaohongshu`
adapts (unapproved source `approval_required`, moved evidence/voice heads 409, unknown
picture `media_unavailable`, title/tags/cover/CTA/selection violations 422 with nothing
stored); `GET /api/assets/{id}/export?format=xiaohongshu` serves the approved revision as a
deterministic ZIP (`copy.txt` exact bytes, `package.json` pack metadata, `images/` in stored
order, `manifest.json` with SHA-256 per picture; draft `approval_required`, edited
`approval_stale`, missing bytes `media_unavailable`); `POST .../publish` refuses
`publisher_unsupported` 422 with no operation, no audit row and no outbound request - no
account, no login, no cookie, no browser automation and no transport exist behind it. Source
approval never approves the derivative; a later voice edit cannot reach the stored snapshot;
owner free-text figures are refused per field when the evidence never stated them. Read that
package's own boundary before extending it, and note the three defects verification found:
the second select in the two-picture fixture re-selected the already-selected candidate (a
no-op appending no bundle revision), so the fixture now picks the not-yet-selected
candidate; the scaffolded EF timestamp sorted before existing migrations, so the migration
is `20261021090000` by hand; and the regenerated model snapshot also carries the
`evolution_gifs` entity the prior commit's hand-written Designer left out of it. Evidence,
all run by the implementer rather than taken on report: `bash scripts/verify.sh` exit 0
(`GATE_EXIT=0`, `set -o pipefail`) with the platform-feed stage and the new `xiaohongshu
content package tests` stage (12/12) alongside all existing stages, `npm ci`, `tsc` + `vite
build`, `dotnet build --nologo --no-incremental` 0 errors with the same 12 pre-existing
warning grep lines at HEAD (zero added, baseline confirmed before changes), name preflight
exit 0, and `openspec validate --all --strict --no-interactive` 54/54 before and after the
archive, which used no `--skip-specs`. Three gate runs were needed: two hit the recorded
environment flakes on untouched stages (a Testcontainers class-cleanup
`TaskCanceledException` with every test passed, then a Ryuk-startup socket failure while a
stale Ryuk from the aborted run was still up - the stage passes 23/23 in isolation), the
third run was fully green after removing the stale testcontainers leftovers. No Web UI was
shipped: the design's React preview is satisfied API-observable (adapt answers the
candidates, preview/edit/approval stay the review package's routes), and no new browser
suite was added. Deployment stays `deployable: false`; there is no remote, no push and no
publication.
Two further DEMAND-GATED changes (`agency-weekly-reports`, `video-story-scripts`) await
recorded demand before activation.
`video-story-scripts` is archived
(`cfaacc1`, canonical spec `openspec/specs/video-story-scripts/spec.md`): reviewed
evidence-based storyboard scripts for the 15, 30 and 60-second runtimes over the delivered
evidence, voice, asset, review, media and curation contracts, with Community behavior
unchanged. One `video-story` asset per milestone with draft revisions holding the canonical
storyboard JSON (`format=video-story`, shots with millisecond intervals covering the runtime
exactly, evidence citations, limitations); `POST /api/milestones/{id}/video-stories`
validates contiguity from zero, minimum 500ms per shot, at most 30 shots, cut|fade handoffs,
depicted pictures that are complete selected screenshots with readable bytes, cited evidence
the frozen bundle holds, shot-level voice and figure grounding, and current evidence/voice
heads (unapproved source `approval_required`, moved heads 409, anything else structural
`invalid_story_schema` 422 with nothing stored); `GET /api/assets/{id}/export?format=video-story`
serves the approved revision as a deterministic ZIP (`storyboard.json` exact approved bytes,
`narration.md`, `caption.srt`, `images/` in first-appearance order, `manifest.json` with
SHA-256 per file; draft `approval_required`, edited `approval_stale` - including a newer
unapproved revision and an explicit old-revision request - missing bytes
`media_unavailable`); `POST .../video-story/publish` refuses `publisher_unsupported` 422
with no operation, no audit row and no outbound request - no renderer, no encoder, no screen
recording and no transport exist behind it. Source approval never approves the derivative; a
later voice edit cannot reach the stored snapshot; owner free-text figures are refused per
shot when the evidence never stated them. Read that package's own boundary before extending
it, and note the one defect verification found: the first select names
`expectedBundleRevision: 1` (the manual bundle), not null - a null body value is 422, and the
suite's first run failed seven times on exactly that. Evidence, all run by the implementer
rather than taken on report: `bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`,
`set -o pipefail`, every filtered stage asserted to select at least one test) with the
platform-feed stage and the new `video story scripts tests` stage (7/7) alongside all
existing stages, `npm ci`, `tsc` + `vite build`, `dotnet build --nologo --no-incremental`
0 errors with the same 12 pre-existing warning grep lines at HEAD (zero added, baseline
confirmed before changes), name preflight exit 0, and `openspec validate --all --strict
--no-interactive` 54/54 before and after the archive, which used no `--skip-specs`. No Web
UI was shipped: the design's React timeline is satisfied API-observable (generate answers
the validated shot list, preview/edit/approval stay the review package's routes), and no
new browser suite was added. Deployment stays `deployable: false`; there is no remote, no
push and no publication.
`agency-weekly-reports` is archived
(`6457c2b`, canonical spec `openspec/specs/agency-weekly-reports/spec.md`): deterministic
evidence-based client weekly progress reports over the delivered agency, milestone, evidence,
media and HTML-export contracts, with Community behavior unchanged. No provider, no model,
no consent, no portal, no invoicing and no email sending anywhere: generation re-renders
confirmed window milestones, complete screenshot items and the owner's own explicit notes
through a fixed template. One `weekly_reports` row per generation carrying the client, the
local week, the UTC window, the project set, the attribution hash, the canonical source
refs, the inputs hash, the optional idempotency key, the body and its hash, plus the
approval stamp (revision, body/attribution/source bindings); a regeneration appends the next
revision, never rewrites one. `POST /api/clients/{id}/weekly-reports`
{projectIds, weekStart YYYY-MM-DD, timeZone IANA, inProgressNotes, nextWeekNotes,
idempotencyKey} bounds exactly seven local calendar dates converted per-bound to UTC (a
project outside the client is `weekly_report_project_outside_client` 422 before any render,
an unknown id the delivered 404, an archived client `client_archived` 409, a drifted key
`weekly_report_idempotency_conflict` 409); the approval binds the revision/body/attribution/
sources under If-Match (428 missing, 409 stale revision); `GET .../export?format=markdown|html`
serves the approved bytes (never approved `approval_required`, moved attribution/evidence
`approval_stale`), HTML through the shared sanitizing adapter with the approval-covered
pictures (missing bytes `media_unavailable`). Migration `20261022090000` adds the table with
hex/week/window/revision checks and the per-week plus keyed unique indexes; the store is raw
SQL like the LinkedIn/Xiaohongshu selection stores, so the EF model is untouched. Read that
package's own boundary before extending it, and note the one defect verification found: a
`DateTimeOffset` formatted with `O` renders `+00:00`, not `Z`, so the window assertions pin
the `+00:00` spelling. Evidence, all run by the implementer rather than taken on report:
`bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o pipefail`) with the platform-feed
stage and the new `agency weekly report tests` stage (11/11, filter asserted by the gate to
select 11) alongside all existing stages, `npm ci`, `tsc` + `vite build`,
`dotnet build --nologo --no-incremental` 0 errors with the same 12 pre-existing warning grep
lines at HEAD (zero added, baseline confirmed before changes plus one new-nullable warning
found and fixed back to baseline), name preflight exit 0, and `openspec validate --all
--strict --no-interactive` 54/54 before and after the archive, which used no `--skip-specs`.
No Web UI was shipped: the design's React editor is satisfied API-observable (report read
serves the full body with per-project sections and source refs), and no new browser suite
was added - the screenshots scenario runs on the capture package's own host with its
declared recording provider. Deployment stays `deployable: false`; there is no remote, no
push and no publication.
No `current_spec` remains: this was the last drainable spec. Two changes stay ACTIVE in
`openspec/changes` and are held, not named as current: `semantic-narrative-retrieval` is
BLOCKED on owner decisions (demand, embedding model/provider/dimension and consent policy
must be selected; owner Chronicleaf maintainer), and `hosted-managed-ai` is BLOCKED on
owner decisions (Managed AI provider/model/quota/unit weights and financial settlement
policy; owner Chronicleaf maintainer).
`feature-trigger-panels` is archived
(`de36bc7`, canonical spec `openspec/specs/feature-trigger-panels/spec.md`): reachable Web
trigger panels for the six delivered backends the 2026-10-07 trigger audit found unreachable
(team invitations/members, hosted tenancy, evolution image approve/preview/download, evolution
GIF approve/preview/download, publication status/reconcile plus asset publish entry, journey
publish plus the anonymous public journey view at `/journey/:slug`). Web-only: no migration,
no route, no envelope, no binding, no evidence rule; every panel calls only the delivered
routes named in its design through the existing `api.ts` per-feature convention, surfaces the
server's `{code,message,traceId,fieldErrors?}` envelope verbatim, and follows existing ad-hoc
panel conventions with no taste changes. Read that package's own boundary before extending
it, and note the one defect verification corrected during implementation: the journey PUT
carries its revision in the body (`expectedRevision`), not in If-Match — the service reads no
header there, and a journey that exists without one is the route's own 428. Evidence, all run
by the implementer rather than taken on report: `CHRONICLEAF_E2E=1 bash scripts/verify.sh`
exit 0 (`GATE_EXIT=0`, `set -o pipefail`) with the platform-feed stage and every existing
filtered stage alongside `npm ci`, `tsc` + `vite build`, then 16/16 Playwright across the six
new database-isolated browser suites (3/3 team, 3/3 tenancy, 2/2 evolution images, 2/2
evolution GIFs, 3/3 publications, 3/3 journey) plus the full pre-existing browser set
unmodified and green; name preflight exit 0; `git diff --check` clean; `openspec validate
--all --strict --no-interactive` 54/54 before and after the archive, which used no
`--skip-specs`. The suites assert empty/generic states only against the real API host with
the real database (Community installation: Hosted routes answer the server's own
`oidc_login_failed`, unknown receipts/journeys the delivered 404); no secret-bearing
captures. Deployment stays `deployable: false`; there is no remote, no push and no
publication.
No `current_spec` remains: the line is removed, not pointed elsewhere. `product-shell-ux`
is taste-blocked (four owner taste answers outstanding before it can start) and is not
selected.
