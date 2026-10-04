#!/usr/bin/env bash
# Chronicleaf native verification (SHELL-003, COMMUNITY-PERSISTENCE-001/002, COMMUNITY-ACCESS-001/002,
# PROJECTS-AND-MILESTONES-001/002, BACKGROUND-JOBS-001/002, LOCAL-MEDIA-STORAGE-001/002,
# MILESTONE-EVIDENCE-001/002, CREDENTIAL-VAULT-001/002, VOICE-PROFILES-001/002,
# LLM-PROVIDER-CONTRACT-001/002, GITHUB-EVIDENCE-001/002, LLM-OPENAI-ADAPTER-001/002,
# GROUNDED-MARKDOWN-DRAFTS-001/002).
#
# Required chain, in order:
#   1. platform feed           - the committed packages/platform-feed is what the build resolves, from
#                                an empty package folder, with no sibling checkout involved
#   2. dotnet build            - .NET 10 Core/Application/Infrastructure/Api and test projects
#   3. dotnet test             - shell contract assertions, then the real-PostgreSQL integration
#                                tests of tests/Chronicleaf.Integration.Tests, one stage per package
#                                so a failure names the package that broke
#   4. npm ci                  - locked frontend dependencies
#   5. npm run build           - tsc then vite build
#
# Any failing step records its own failure and exits nonzero. Restore, NuGet and npm
# network failures are reported as failures; they are never skipped or tolerated.
#
# Stage 2 needs a running Docker daemon: the persistence and access scenarios use a real
# disposable PostgreSQL container and a mock or in-memory store is not accepted as evidence. When
# the daemon is unreachable this script FAILS with the reason instead of quietly skipping the
# scenarios. Set CHRONICLEAF_SKIP_DB_TESTS=1 only to run the stages that need no database, and
# treat that run as incomplete rather than as a passing gate.
#
# The real-browser stage drives a published application with a real database behind it, so it
# needs Docker and Playwright browsers as well:
# CHRONICLEAF_E2E=1 bash scripts/verify.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Asserts that a --filter expression actually selects tests. `dotnet test --filter` with a
# pattern that matches nothing exits 0, so a mistyped filter would turn a whole suite into a
# silent no-op that reports success. voice-profiles already hit this: the class is
# VoiceProfilesTests, a singular filter matched 0, and the gate stayed green.
assert_filter_matches() {
  local filter="$1"
  local matched
  matched="$(dotnet test Chronicleaf.sln --no-build --nologo --list-tests --filter "$filter" 2>/dev/null \
    | grep -cE '^ {4}' || true)"
  if [ "$matched" -eq 0 ]; then
    echo "FAILED: test filter matched no tests: ${filter}" >&2
    return 1
  fi
  echo "    filter ${filter} selects ${matched} test(s)"
}

step() {
  local name="$1"
  shift
  echo "==> ${name}: $*"
  # The status is captured in a || list because `set -e` must not abort before the
  # failing command is reported. Returning it aborts the script with the same code.
  local status=0
  local prev=''
  # Any stage that filters tests is checked first, so a filter that silently selects
  # nothing fails loudly instead of reporting a green no-op.
  local filter=''
  local arg
  for arg in "$@"; do
    if [ "$prev" = '--filter' ] || [ "$prev" = 'FullyQualifiedName~' ]; then
      filter="${arg}"
      break
    fi
    prev="$arg"
  done
  if [ -n "$filter" ]; then
    assert_filter_matches "$filter" || return 1
  fi
  "$@" || status=$?
  if [ "$status" -ne 0 ]; then
    echo "FAILED (exit ${status}): ${name}: $*" >&2
    return "$status"
  fi
}

require_docker() {
  if ! command -v docker >/dev/null 2>&1; then
    echo "FAILED (prerequisite unavailable): docker is not on PATH; the persistence and access scenarios need a real PostgreSQL container." >&2
    return 1
  fi
  if ! docker info >/dev/null 2>&1; then
    echo "FAILED (prerequisite unavailable): the docker daemon is not reachable; the persistence and access scenarios need a real PostgreSQL container." >&2
    return 1
  fi
}

# Ruling 12 of the workspace ARCHITECTURE.md: the adopted shared storage is consumed from .nupkg files
# committed in this repository, never from a sibling checkout, an absolute path or an environment
# variable. This stage proves that, and it is the FIRST one on purpose: a build that only succeeds on a
# machine with a warm NuGet cache would otherwise look green here and fail on the next person's.
step "platform feed" bash scripts/verify-platform-feed.sh
step "api build" dotnet build Chronicleaf.sln --nologo
step "shell tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~Chronicleaf.Api.Tests"

if [ "${CHRONICLEAF_SKIP_DB_TESTS:-0}" = "1" ]; then
  echo "WARNING: CHRONICLEAF_SKIP_DB_TESTS=1 - the real-PostgreSQL persistence and access tests did not run. This is not a complete gate." >&2
else
  step "docker prerequisite" require_docker
  step "persistence tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~CommunityPersistenceTests"
  # The access scenarios prove session, expiry, throttle and token rotation against the same real
  # PostgreSQL. A session row, an idle deadline or a login counter simulated in memory is refused
  # as evidence, so they are a separate stage with their own result.
  step "access tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~CommunityAccessTests"
  # PROJECTS-AND-MILESTONES-001/002. The milestone lifecycle, the revision conflict, the soft archive
  # and the restart are proven against the same real disposable PostgreSQL and the real API host. A
  # project or milestone simulated in memory, or an EF in-memory provider, is refused as evidence here
  # for the same reason it is for the stages above.
  step "project and milestone tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~ProjectsAndMilestonesTests"
  # BACKGROUND-JOBS-001/002. Durable execution is proven against the same real disposable PostgreSQL
  # and a real Hangfire server holding real connections to it. An in-memory job store, a fake queue,
  # an EF in-memory provider or a stubbed worker is refused as evidence here for the same reason it
  # is for the stages above: this package's whole claim is that a result survives a restart, and only
  # a real queue and a real database can be evidence for that.
  step "background job tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~BackgroundJobsTests"
  # LOCAL-MEDIA-STORAGE-001/002. Private scoped media, the two-phase upload, the traversal and symlink
  # boundary and the retire rules are proven against the same real disposable PostgreSQL AND a real
  # temporary filesystem, through the real API host. An in-memory storage provider, a stubbed store or
  # an EF in-memory provider is refused here for the same reason it is above: the claim under test is
  # that bytes on a disk stay consistent with rows in a database, and neither half can be simulated.
  step "local media storage tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~LocalMediaStorageTests"
  # MILESTONE-EVIDENCE-001/002. The immutable provenance snapshots, the idempotent append, the honest
  # unavailable and partial records, the revision conflict, the media reference check that actually
  # holds now, and the append-only triggers are proven against the same real disposable PostgreSQL AND
  # a real temporary filesystem, through the real API host. A scripted reference checker, a stubbed
  # store or an EF in-memory provider is refused here for the same reason it is above: the claim under
  # test is that stored evidence keeps bytes referenced and cannot be rewritten, and neither half can
  # be simulated.
  step "milestone evidence tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~MilestoneEvidenceTests"
  # CREDENTIAL-VAULT-001/002. That a stored credential's plaintext is absent from the raw bytea
  # columns, that a missing, wrong-length or unreadable key file fails closed with no key path in the
  # response or the log, that a row moved between scopes or providers no longer opens, that a stale
  # revision is a 409 which preserves the stored bytes, and that a queued job whose credential was
  # deleted fails credential_missing, are all proven against the same real disposable PostgreSQL AND a
  # real temporary operator key file, through the real API host and the real operation queue. A stubbed
  # cipher, an in-memory database or a simulated provider is refused here for the same reason it is
  # refused everywhere above: the claim under test is that a secret cannot be recovered from what was
  # stored, and a simulated key would prove nothing about that.
  step "credential vault tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~CredentialVaultTests"
  # VOICE-PROFILES-001/002. That an explicit override beats the preset for every field it names, that
  # an unknown preset is refused with nothing written, that the forbidden-term and structural findings
  # carry spans into the owner's own text, and - the claim nothing else in this file can make - that a
  # voice profile change after a draft was created leaves that draft's stored snapshot byte-identical,
  # are all proven against the same real disposable PostgreSQL through the real API host. The
  # byte-identity is asserted by reading the voice_snapshot column itself, not by comparing two values
  # this process built. A stubbed checker, a simulated store or an EF in-memory provider is refused
  # here for the same reason it is everywhere above: the claim is that a finding reports a span without
  # touching the text, and a checker that was never really run proves nothing about that.
  step "voice profile tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~VoiceProfilesTests"
  # LLM-PROVIDER-CONTRACT-001/002. That a completed call records text, finish state and usage without
  # touching project or milestone state; that a refusal and a malformed answer are two different codes
  # and neither becomes a draft; that a provider without native schema support is refused the
  # capability and every answer is checked locally instead; that the bytes sent are the saved preview
  # and carry no tool channel; that a changed, expired or foreign consent is a 409 or a 404 with the
  # transport never reached; that a timeout and a cancellation each produce exactly one attempt and no
  # approval; that a bounded retry re-sends the identical request and is counted; and that a
  # deployment with no adapter refuses a provider instead of reaching one - all proven against the
  # same real disposable PostgreSQL and a real temporary operator key file, through the real API host.
  #
  # A simulation is refused as evidence here, and the refusal is specific rather than general. The
  # only ILLMProvider in this repository is a test double, it is registered solely in the test host,
  # and these scenarios assert what Chronicleaf does with a request and a result: the consent, the
  # bounds, the error mapping and the local validation. A provider that answered in-process proves
  # nothing about any provider, so no stage here may be read as evidence that a model was called, that
  # a schema was enforced remotely, or that a paid call is cheap - and the stage that would tempt that
  # reading most, the one asserting a completed call, asserts the milestone's own status and revision
  # and the absence of any approval action alongside the text. The production-registration scenario
  # closes the loop from the other side: the same provider id on a host built from the production
  # composition root answers 422, because no configuration value can reach a double that was never
  # registered.
  step "llm provider contract tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~LlmProviderContractTests"
  # LLM-OPENAI-ADAPTER-001/002. That a valid nonstreaming response maps text, finish state and whatever
  # usage the provider reported; that a refusal, a content-filtered answer, a truncation, a tool call in
  # a text-only contract, an empty answer and three malformed bodies are each distinguishable and none of
  # them is ever a draft; that absent or nonsensical token counts are reported unknown rather than
  # fabricated; that the outbound request is exactly the consented payload at the fixed origin with no
  # member asking for tools or for a stored conversation; that a 401 and a 403 are the same
  # credential_rejected answer and neither the envelope nor the adapter's own log lines carry the key or
  # the provider's error prose; that a 429 and a 5xx are two different codes; that a 302 is refused
  # rather than followed; that a model outside the allowlist and an empty allowlist are both refused
  # before a credential is leased and before a socket is opened; that a cancelled call throws and stores
  # nothing; that a response over the 2 MiB cap is refused while its bytes are arriving; and that the
  # adapter is registered in production and published verbatim, and is unreachable when it is not
  # registered - all proven against the same real disposable PostgreSQL, the real operator key file and
  # the real API host, with the real OpenAIProvider over a stub at the far end of its HttpClient.
  #
  # One substitution, and it is the package's own declared boundary: the message handler underneath the
  # adapter's HttpClient. Everything above the socket is production's own code, including the adapter,
  # the vault lease, the header, the status mapping and the consent handshake - so a double
  # implementing ILLMProvider (as the stage above needs) could not have asserted any of this. No stage
  # here may be read as evidence that OpenAI works, that a named model exists or is available to any
  # owner, or that a paid call is cheap: the response bodies are transcribed from the provider's
  # published reference, which is a fact about a document rather than about a service, and the
  # credential-gated live smoke the design names is NOT performed and NOT claimed.
  step "llm openai adapter tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~LlmOpenaiAdapterTests"
  # GITHUB-EVIDENCE-001/002. That a collection resolves the selected refs to immutable revisions and
  # appends bounded evidence naming each source; that a repository that is absent or not public fails
  # with repository_unavailable rather than with invented material; that a binary or excluded file is
  # marked as missing or redacted diff evidence instead of being dropped; that a rate-limited answer
  # waits on the job policy's own bounded delay; that a bundle which moved mid-import ends superseded
  # without being overwritten; and that a compare past the documented cap marks truncation and stays
  # inside every cap - are all proven against the same real disposable PostgreSQL and a real temporary
  # operator key file, through the real API host and the real operation queue.
  #
  # A simulation is refused as evidence here, and the refusal is specific rather than general. GitHub
  # itself is the one boundary that is faked in this file, and it is faked with a Fake HttpMessageHandler
  # because the claim under test is never "GitHub works": it is that Chronicleaf records the resolved
  # SHAs, bounds the diff, refuses a repository it cannot read, and leaves the stored bytes alone. A
  # scripted or stubbed GitHub that ran inside the process would prove nothing about any of that and
  # would be a way for a whole package to pass without a database, a host or a queue. The fake handler is
  # not the same kind of thing and is accepted for a specific reason: it substitutes only the far end of
  # the wire, so the pinned host, the real HttpClient, the real vault lease, the real conditional append
  # and the real status mapping all still execute - and the scenario that would otherwise tempt the
  # reading most, the one that describes a public repository, is the opt-in run that reports
  # "prerequisite unavailable" unless CHRONICLEAF_GITHUB_REPOSITORY and CHRONICLEAF_GITHUB_TOKEN are
  # both set, and that run uses the real transport. No stage here may be read as evidence that a real
  # GitHub API was called, that a real repository was resolved, or that any specific GitHub response
  # shape is supported beyond what the transport's own pinned version documents.
  step "github evidence tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~GithubEvidenceTests"
  # SCREENSHOT-CAPTURE-001/002. That a private, local, loopback or cloud-metadata destination is refused
  # by the URL policy - including when the same address is written in IPv6 spelling as an
  # IPv4-mapped literal such as ::ffff:10.0.0.5, which the IPv6 rules would otherwise judge a public
  # page; that the policy is consulted before a job exists and again to the address the name resolves
  # to before the browser is asked about anything, so a refused destination is refused before bytes are
  # exchanged; that a job on a host which cannot confine a browser answers capture_sandbox_unavailable
  # AND starts no browser process; that candidates are written as candidates and never selected, made
  # primary, captioned or appended to evidence; and that a duplicate tuple retires the second run's
  # media through the delivered status transition - all proven against the same real disposable
  # PostgreSQL and a real temporary media root, through the real API host and the real operation queue.
  #
  # Two things this stage is NOT evidence for, and the comment says so because the temptation is real.
  # First, the substituted IScreenshotProvider is a legitimate DI seam - the delivered
  # IGitEvidenceSource substitutes its own far end the same way - but it means no browser is launched
  # and no page is visited by these scenarios, so nothing here shows that a real page can be
  # photographed, and the PNG bytes that exercise media storage, the candidate row, the PNG dimension
  # check and the digest are a labelled synthesised fixture rather than a captured page. Second, the
  # egress enforcement is a kernel mechanism: this host has no CAP_NET_ADMIN, no user namespaces, no
  # iptables and no nft, so the decision function is tested directly and the kernel refusing a
  # connection is NOT proven here. Chromium and Microsoft.Playwright are present, which is why the
  # sandbox probe reports a real answer - it is the enforcement underneath it that this host cannot
  # exercise.
  step "screenshot capture tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~ScreenshotCaptureTests"
  # GROUNDED-MARKDOWN-DRAFTS-001/002. That a valid summary is checked against the frozen evidence
  # snapshot before any Markdown exists; that a claim naming an id outside the snapshot, or a figure
  # appearing in no evidence item, fails grounding and writes nothing; that a summary broken twice -
  # including the single permitted repair - fails invalid_summary with exactly two calls; that a second
  # generation appends a revision and leaves the first byte-identical; that evidence or voice moving
  # before the commit supersedes the run and preserves the previous draft; that instruction-shaped
  # imported text produces no state change and no extra provider call; and that a stored revision
  # cannot be rewritten, cannot claim evidence revision zero and cannot carry a non-digest - all proven
  # against the same real disposable PostgreSQL, the real operator key file, the real API host and the
  # real operation queue, with a real sealed credential behind a real consent.
  #
  # A simulation is refused as evidence here, and the refusal is specific rather than general. The one
  # ILLMProvider in this file is a test double on the `anthropic` slot, a slot no production adapter
  # claims - the same discipline the delivered contract suite uses for the same reason, and
  # `the_slot_this_double_owns_is_claimed_by_no_production_adapter` fails the moment a future adapter
  # package claims it, because at that moment the double would be shadowed by the real adapter and every
  # scenario here would silently stop testing anything. What the double does not do is reach a provider:
  # it opens no socket and sends nothing, so no stage here may be read as evidence that any provider
  # works, that any model exists or is available to any owner, or that a paid call is cheap. The stored
  # draft bodies come from fixtures, and the grounding, citation, provenance and supersede claims are
  # about this system's handling of them.
  step "grounded markdown draft tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~GroundedMarkdownDraftsTests"
fi

step "frontend install" bash -euo pipefail -c 'cd src/Chronicleaf.Web && npm ci'
step "frontend build" bash -euo pipefail -c 'cd src/Chronicleaf.Web && npm run build'

if [ "${CHRONICLEAF_E2E:-0}" = "1" ]; then
  # Publishes the real application, starts a disposable PostgreSQL per suite, applies the committed
  # migrations, and drives the shell, access, projects-and-milestones, background-jobs,
  # milestone-evidence, credential-vault, voice-profiles, llm-provider-contract, github-evidence,
  # screenshot-capture and grounded-markdown-drafts fixtures in a real browser. The last eight
  # are included by playwright.config.ts, which already matches tests/**/*.spec.ts; this stage
  # therefore fails if the browser cannot record a project, confirm a candidate, refuse a stale edit,
  # read a milestone's evidence and be told the truth by a 409 against a real database, turn emoji
  # off over a preset that asked for it and see the effective value change, be shown every provider
  # slot on a new installation with the truth about which of them can actually be called, see a
  # project admit it has no repository connected, be told in the server's own words when one is
  # refused, follow a real collection job to the status the operations route reports, or read a
  # stored draft with its provenance, its citations and both finding lists and have that body rendered
  # as source rather than as HTML. The github suite needs outbound network to api.github.com: nothing in
  # it is faked, so a deployment without that route out fails this stage rather than passing on a stub.
  #
  # The capture fixture needs no outbound network, and that is deliberate rather than convenient: a
  # capture that could reach a page is a capture whose isolation this host cannot demonstrate, so the
  # suite asserts the honest reporting of the refusal - the panel names capture_sandbox_unavailable,
  # says no page was visited and no picture exists, and draws nothing - and separately asserts the
  # request-time refusal of a private destination, which is the half that never needed a browser. A
  # pass is evidence that this host's refusal is visible, not evidence that a screenshot was captured.
  step "browser fixtures" bash scripts/e2e.sh
fi

echo "verification passed"
