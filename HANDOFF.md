# Chronicleaf handoff

current_spec: agency-client-projects

## Current state — 2026-10-05

Twenty-six changes implemented, verified and archived, each with exactly two commits (implementation/
tests/archive, then this HANDOFF-only pointer). `current_spec` selects `content-asset-variants`, the
next POST-MVP package in ROADMAP order after `html-content-export`.

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
| 25 | `narrative-memory` | `a7d12f4` | `openspec/specs/narrative-memory/spec.md` |
| 26 | `html-content-export` | `aae5181` | `openspec/specs/html-content-export/spec.md` |
| 27 | `content-asset-variants` | `65309ac` | `openspec/specs/content-asset-variants/spec.md` |
| 28 | `screenshot-evolution-images` | `f2916e1` | `openspec/specs/screenshot-evolution-images/spec.md` |
| 29 | `static-blog-export` | `066e153` | `openspec/specs/static-blog-export/spec.md` |
| 30 | `public-project-journey` | `6bc7e8e` | `openspec/specs/public-project-journey/spec.md` |

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

Next: `current_spec` is `agency-client-projects`, the DEMAND-GATED package ROADMAP orders
after `public-project-journey`. `public-project-journey` is archived
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
