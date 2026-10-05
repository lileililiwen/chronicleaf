#!/usr/bin/env bash
# Chronicleaf native verification (SHELL-003, COMMUNITY-PERSISTENCE-001/002, COMMUNITY-ACCESS-001/002,
# PROJECTS-AND-MILESTONES-001/002, BACKGROUND-JOBS-001/002, LOCAL-MEDIA-STORAGE-001/002,
# MILESTONE-EVIDENCE-001/002, CREDENTIAL-VAULT-001/002, VOICE-PROFILES-001/002,
# LLM-PROVIDER-CONTRACT-001/002, GITHUB-EVIDENCE-001/002, LOCAL-GIT-EVIDENCE-001/002,
# LLM-OPENAI-ADAPTER-001/002,
# LLM-OLLAMA-ADAPTER-001/002,
# SCREENSHOT-CAPTURE-001/002, SCREENSHOT-CURATION-001/002, GROUNDED-MARKDOWN-DRAFTS-001/002,
# MILESTONE-TIMELINE-001/002, MARKDOWN-REVIEW-EXPORT-001/002,
# COMMUNITY-COMPOSE-RELEASE-001/002).
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
  # LLM-GEMINI-ADAPTER-001/002. That a valid nonstreaming generateContent response maps text, finish
  # state and whatever usage the provider reported; that a safety-blocked prompt, a safety-stopped
  # candidate, a truncation, a function call in a text-only contract, a stopped answer with no text
  # and three malformed bodies are each distinguishable and none of them is ever a draft; that absent
  # or nonsensical token counts are reported unknown rather than fabricated; that the outbound request
  # is exactly the consented payload at the fixed origin with no member asking for tools; that a 401
  # and a 403 are the same credential_rejected answer and neither the envelope nor the adapter's own
  # log lines carry the key or the provider's error prose; that a 429 and a 5xx are two different
  # codes; that a 302 is refused rather than followed; that a model outside the allowlist and an empty
  # allowlist are both refused before a credential is leased and before a socket is opened; that a
  # cancelled call throws and stores nothing; that a response over the 2 MiB cap is refused while its
  # bytes are arriving; and that the adapter is registered in production and published verbatim, and is
  # unreachable when it is not registered - all proven against the same real disposable PostgreSQL,
  # the real operator key file and the real API host, with the real GeminiProvider over a stub at the
  # far end of its HttpClient.
  #
  # One substitution, and it is the package's own declared boundary: the message handler underneath the
  # adapter's HttpClient. Everything above the socket is production's own code, including the adapter,
  # the vault lease, the header, the status mapping and the consent handshake - so a double
  # implementing ILLMProvider (as the contract stage needs) could not have asserted any of this. No
  # stage here may be read as evidence that Gemini works, that a named model exists or is available to
  # any owner, or that a paid call is cheap: the response bodies are transcribed from the provider's
  # published reference, which is a fact about a document rather than about a service, and the
  # credential-gated live smoke the design names is NOT performed and NOT claimed.
  step "llm gemini adapter tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~LlmGeminiAdapterTests"
  # LLM-ANTHROPIC-ADAPTER-001/002. That a valid nonstreaming messages response maps text, finish
  # state, the provider's own message id and whatever usage the provider reported; that a refusal, a
  # tool call in a text-only contract, a truncation, a finished answer with no text, a stop for a
  # condition nobody set and three malformed bodies are each distinguishable and none of them is ever
  # a draft; that absent or nonsensical token counts are reported unknown rather than fabricated;
  # that the outbound request is exactly the consented payload at the fixed origin - model, ceiling,
  # system instruction carrying the schema fallback, evidence as a labelled user text block - with no
  # member asking for tools and no schema member; that a 401 and a 403 are the same credential_rejected
  # answer and neither the envelope nor the adapter's own log lines carry the key
  # or the provider's error prose; that a 429 and a 5xx (including Anthropic's own 529) are two
  # different codes; that a 302 is refused rather than followed; that a model outside the allowlist
  # and an empty allowlist are both refused before a credential is leased and before a socket is
  # opened; that a cancelled call throws and stores nothing; that a response over the 2 MiB cap is
  # refused while its bytes are arriving; and that the adapter is registered in production and
  # published verbatim - native schema off, so the schema rides in the prompt and the text is
  # validated locally - and is unreachable when it is not registered - all proven against the same
  # real disposable PostgreSQL, the real operator key file and the real API host, with the real
  # AnthropicProvider over a stub at the far end of its HttpClient.
  #
  # One substitution, and it is the package's own declared boundary: the message handler underneath the
  # adapter's HttpClient. Everything above the socket is production's own code, including the adapter,
  # the vault lease, the header, the status mapping and the consent handshake - so a double
  # implementing ILLMProvider (as the contract stage needs) could not have asserted any of this. No
  # stage here may be read as evidence that Anthropic works, that a named model exists or is available
  # to any owner, or that a paid call is cheap: the response bodies are transcribed from the provider's
  # published reference, which is a fact about a document rather than about a service, and the
  # credential-gated live smoke the design names is NOT performed and NOT claimed.
  step "llm anthropic adapter tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~LlmAnthropicAdapterTests"
  # LLM-OPENROUTER-ADAPTER-001/002. That a valid nonstreaming chat completion maps text, finish
  # state, the provider's own generation id and whatever usage the provider reported; that a refusal,
  # a tool call in a text-only contract, a truncation - with or without a prefix - a finished answer
  # with no text, a finish for a condition nobody set and three malformed bodies are each
  # distinguishable and none of them is ever a draft; that absent or nonsensical token counts are
  # reported unknown rather than fabricated; that the outbound request is exactly the consented payload
  # at the fixed origin - model, ceiling, stream false, system instruction carrying the schema
  # fallback, evidence as a labelled user message - with no member asking for tools and no schema
  # member; that a 401 and a 403 are the same credential_rejected answer and neither the envelope nor
  # the adapter's own log lines carry the key or the provider's error prose; that a 429 and a 5xx are
  # two different codes; that a 302 is refused rather than followed; that a model outside the allowlist
  # and an empty allowlist are both refused before a credential is leased and before a socket is
  # opened; that a cancelled call throws and stores nothing; that a response over the 2 MiB cap is
  # refused while its bytes are arriving; and that the adapter is registered in production and
  # published verbatim - native schema off, so the schema rides in the prompt and the text is
  # validated locally - and is unreachable when it is not registered - all proven against the same
  # real disposable PostgreSQL, the real operator key file and the real API host, with the real
  # OpenRouterProvider over a stub at the far end of its HttpClient.
  #
  # One substitution, and it is the package's own declared boundary: the message handler underneath the
  # adapter's HttpClient. Everything above the socket is production's own code, including the adapter,
  # the vault lease, the bearer header, the status mapping and the consent handshake - so a double
  # implementing ILLMProvider (as the contract stage needs) could not have asserted any of this. No
  # stage here may be read as evidence that OpenRouter works, that a named model exists or is available
  # to any owner, or that a paid call is cheap: the response bodies are transcribed from the provider's
  # published reference, which is a fact about a document rather than about a service, and the
  # credential-gated live smoke the design names is NOT performed and NOT claimed.
  step "llm openrouter adapter tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~LlmOpenrouterAdapterTests"
  # LLM-OLLAMA-ADAPTER-001/002. That a valid nonstreaming chat response maps text, finish
  # state and whatever usage the daemon reported, with no invented request id; that a consent
  # without a credential is a keyless call rather than a refusal; that an unfinished generation,
  # a truncation, an empty answer, an unrecognised stop reason and three malformed bodies are
  # each distinguishable and none of them is ever a draft; that prose refusals arrive as text
  # the adapter cannot classify and are refused by the delivered local validator instead; that
  # absent or nonsensical token counts are reported unknown rather than fabricated; that the
  # outbound request is exactly the consented payload at the allowlisted origin's chat resource
  # - model, ceiling as num_predict, stream false, system instruction carrying the schema
  # fallback, evidence as a labelled user message - with no member asking for tools and no schema
  # member, and no auth header unless the consent named a credential; that a 401 and a 403 are
  # the same credential_rejected answer and neither the envelope nor the adapter's own log lines
  # carry the key or the provider's error prose; that a 429 and a 5xx are two different codes;
  # that a 302 is refused rather than followed; that a model or an endpoint outside the
  # allowlists - and an empty allowlist - are both refused before a credential is leased and
  # before a socket is opened; that a cancelled call throws and stores nothing; that a response
  # over the 2 MiB cap is refused while its bytes are arriving; and that the adapter is
  # registered in production and published verbatim - local endpoint, no credential required,
  # native schema off, so the schema rides in the prompt and the text is validated locally - and
  # is unreachable when it is not registered - all proven against the same real disposable
  # PostgreSQL, the real operator key file and the real API host, with the real OllamaProvider
  # over a stub at the far end of its HttpClient.
  #
  # One substitution, and it is the package's own declared boundary: the message handler underneath the
  # adapter's HttpClient. Everything above the socket is production's own code, including the adapter,
  # the endpoint allowlist, the vault lease, the optional bearer header, the status mapping and the
  # consent handshake - so a double implementing ILLMProvider (as the contract stage needs) could not
  # have asserted any of this. No stage here may be read as evidence that any Ollama daemon works, that
  # a named model exists or is available to any owner, or that a call is cheap: the response bodies are
  # transcribed from the provider's published reference, which is a fact about a document rather than
  # about a service, and the credential-gated live smoke the design names is NOT performed and NOT
  # claimed.
  step "llm ollama adapter tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~LlmOllamaAdapterTests"
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
  # GITHUB-GRAPHQL-ENRICHMENT-001/002. That readable associated pull requests are appended with
  # their ids, observed states and source URLs while the REST evidence stays intact; that errored
  # GraphQL fields become unavailable items carrying the package's own reason vocabulary and never
  # a sentence from a response body; that a repeated provider node across pages is appended once;
  # that only the declared repository is ever queried with the one fixed named operation; that a
  # missing permission ends the job as permission_denied with no fabricated fact; that a bundle
  # that moves during the query - or between the request and the run - is superseded rather than
  # overwritten; and that a 429 waits for the provider's own reset before the retry succeeds - are
  # all proven against the same real disposable PostgreSQL and the real API host, with only the
  # bytes on the wire scripted. No stage here may be read as evidence that GitHub works or that a
  # real repository was resolved.
  step "github graphql enrichment tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~GithubGraphqlEnrichmentTests"
  # LOCAL-GIT-EVIDENCE-001/002. That two selected commits are appended with their own immutable revisions and
  # bounded tracked differences, and that every later fact is read from two resolved SHAs rather than from
  # the directory; that an untracked file, a dirty edit and a committed secret-named file each reach no
  # evidence item, with the exclusions and the uncommitted count recorded rather than the bytes; that a head
  # that moves during the read ends the operation superseded with nothing appended; that a registered root is
  # collected from and the repository is byte-identical afterwards, `.git` included; that a path which leaves
  # its root through a symbolic link is refused before Git is started, and one that becomes a link after
  # registration is refused when the job runs; that a Hosted deployment refuses local filesystem discovery and
  # starts no process at all; that the runner will only run four read verbs and builds its process from an
  # argument vector with a sanitised environment; and that the database itself refuses a relative path that
  # leaves its root and a run row that is half-completed - are all proven against the same real disposable
  # PostgreSQL and the real API host, over REAL TEMPORARY GIT REPOSITORIES on a real filesystem, read by a
  # real `git` process started by the production runner.
  #
  # A simulation is refused as evidence here, and the refusal is specific rather than general. There is no
  # double anywhere in this stage. The claim under test is what this server does with a directory on a disk -
  # which paths it will read, which bytes it will not, which Git commands it may start, and what it leaves
  # behind - and a scripted transport substituted at the process boundary would leave the containment check,
  # the argument vector, the caps, the timeout and the read-only claim untested, which is the whole of the
  # package. So the repository on disk was created by a real `git` in a real directory, the application reads
  # it through its own runner, and the only seam these scenarios use is a runner that DELEGATES to the
  # production one and can move a head between two commands - and even that is used by exactly one scenario,
  # the race nothing outside the process could otherwise schedule.
  #
  # Nothing in this stage may be read as evidence that Git works, that a particular repository is well formed,
  # or that any particular distribution's Git behaves as another's does. What is proven is Chronicleaf's own
  # handling of what Git answers.
  step "local git evidence tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~LocalGitEvidenceTests"
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
  # SCREENSHOT-CURATION-001/002. That selecting a screenshot with a caption appends an evidence
  # revision referencing that media and caption, and that a caption edit appends a correcting
  # revision naming what it corrects; that a candidate never selected stays outside selected
  # evidence, that a rejected or unknown picture cannot be made primary, and that validation names
  # its field while writing nothing; that deselecting appends a revision without the picture while
  # earlier bundles keep their rows, and that a repeated unchanged selection is a no-op rather than
  # a revision; that a capture re-run offers new candidates and never moves a human selection, and
  # that changing the primary clears the old one in the same transaction with exactly one primary
  # left; that an order must name exactly the selected pictures once each and is stored and served
  # in that sequence without moving the bundle; and that two curations on one revision produce one
  # winner and one 409 - all proven against the same real disposable PostgreSQL, the real API host,
  # the real operation queue and the real media and evidence stores, with candidates produced by
  # real capture jobs behind the delivered provider seam.
  #
  # The same honesty bounds as the capture stage apply: no browser is launched and no page is
  # visited by these scenarios, so nothing here shows that a real page can be photographed, and
  # the PNG bytes behind the selections are the same labelled synthesised fixture.
  step "screenshot curation tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~ScreenshotCurationTests"
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
  # ILLMProvider this file's scenarios drive is a test double inserted before the production
  # registrations, so the delivered resolver hands back the double itself for the `ollama` slot -
  # which production also claims since llm-ollama-adapter landed, and
  # `the_double_shadows_the_production_adapter_for_the_slot_it_owns` fails the moment that stops
  # being true, because at that moment the double would be shadowed by the real adapter and every
  # scenario here would silently stop testing anything. What the double does not do is reach a provider:
  # it opens no socket and sends nothing, so no stage here may be read as evidence that any provider
  # works, that any model exists or is available to any owner, or that a paid call is cheap. The stored
  # draft bodies come from fixtures, and the grounding, citation, provenance and supersede claims are
  # about this system's handling of them.
  step "grounded markdown draft tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~GroundedMarkdownDraftsTests"
  # MILESTONE-TIMELINE-001/002. That 52 confirmed records across equal timestamps page oldest first
  # with no duplicated id and read identically twice; that candidates stay hidden until asked for
  # and then arrive labeled without promoting anything; that dismissed records are omitted in both
  # views; that a cursor from another project, another filter or the milestone list is refused with
  # a 422 naming cursor and no foreign record; that a backdated edit moves its row on the next
  # fresh read; and that an archived project's history stays readable - all proven against the same
  # real disposable PostgreSQL through the real API host. No new table is migrated: the timeline
  # is a projection of the milestone records the projects-and-milestones migration already holds.
  step "milestone timeline tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~MilestoneTimelineTests"
  # MARKDOWN-REVIEW-EXPORT-001/002. That approving the current draft binds the actor, the content
  # hash and the evidence revision; that an export without approval is a 409 approval_required and
  # an edit or an evidence append after approval makes it a 409 approval_stale until a new
  # approval; that a revision carrying deterministic findings cannot be approved and no
  # acknowledgment clears that; that the export serves the exact approved bytes as a Markdown
  # attachment with no upstream call; that unsafe markup is stored as the owner's words and
  # served as text the browser saves rather than as a document it runs; that a late approval of
  # a stale revision exports nothing unreviewed; that an edit without If-Match is a 428, a stale
  # head a 409 and an empty body a 422; and that withdrawing an approval ends exports while the
  # row stays as history - all proven against the same real disposable PostgreSQL through the
  # real API host, with the reviewed draft produced by a real generation job behind the delivered
  # provider seam.
  step "markdown review export tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~MarkdownReviewExportTests"
  # NARRATIVE-MEMORY-001/002. That SQLite and PostgreSQL history recorded before a refresh lands
  # in the summary as attributed transition lines citing owned evidence ids without reintroducing
  # the project as new; that missing feedback reads as unknown rather than invented users; that
  # a retrospective cutoff excludes later milestones from the body and the through milestone;
  # that a corrected source decision marks the prior summary stale until regeneration advances
  # the revision; that a failed regeneration keeps the prior body available labelled stale with
  # the build code on the operation; that private feedback without an explicit selection is
  # absent from the body and present with one; and that a stale writer loses with 409, a
  # missing If-Match is 428, and foreign evidence or a foreign project is 404 - all proven
  # against the same real disposable PostgreSQL through the real API host and the real
  # operation queue, with the one declared substitution the package names (a throwing summary
  # builder for the failure scenario; the delivered builder reaches no provider).
  step "narrative memory tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~NarrativeMemoryTests"
  # COMMUNITY-COMPOSE-RELEASE-001/002. That a clean install completes the
  # milestone-to-approved-Markdown flow with the timeline telling it back; that the compose
  # file describes exactly app plus postgres on the pinned digest with named volumes, a
  # loopback default and a read-only key mount and no third runtime; that all five provider
  # transports are installed in production; that a private capture destination is refused
  # before any job exists and a missing sandbox is a visible 503 rather than a silent
  # downgrade; that a restart on the retained database serves identical export and timeline
  # bytes; that readiness distinguishes a live process from a usable install; that export
  # without approval stays refused; and that no release script publishes the repository -
  # all proven against the same real disposable PostgreSQL through the real API host, with
  # the same two declared substitutions the owning packages name (a scripted provider
  # answer, no browser launched). The container bring-up, restart, backup and restore
  # cycle the file checks stand in for is scripts/verify-community.sh.
  step "community compose release tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~CommunityComposeReleaseTests"
  # GITHUB-RELEASE-CANDIDATES-001/002. That a verified release event produces one candidate identifying
  # its release source, and that an invalid HMAC - a wrong secret, a missing header, a SHA-1 header and
  # a body edited after signing - is 401 with nothing recorded; that a redelivery returns the original
  # receipt and no second candidate; that a star count records one candidate per crossed threshold of the
  # two declared; that an unmerged pull request, a draft release, a deleted release, an opened issue, a
  # created branch and a below-threshold star each get an ignored receipt naming why, with no milestone,
  # evidence item, draft or media written; and that an archived connection acknowledges a delivery as
  # ignored, keeps its receipts, and works again when re-subscribed. The poll is proven too: that a
  # repository is read on two bounded resources on the pinned host with no write, that the `since` floor
  # excludes an older release while the run log still counts it, that a second poll records no new
  # candidate because the source key already exists, and that a 404, a 401, a 403 and a 422 each fail the
  # job with its own code and record nothing. Also that a guessed connection id and a wrong signature are
  # the same 401 with the same message, that a signed but unreadable body is a distinguishable 422, that
  # an oversized body is a 413, that the outbox event names this package's own receipt row and a consumer
  # handed it twice proposes once, and that the database itself refuses a second candidate per source key,
  # a second receipt per delivery, a half-decided receipt, a reason outside the closed vocabulary and a
  # second subscription for one connection - all against the same real disposable PostgreSQL and the real
  # API host, over a real temporary operator key file.
  #
  # A simulation is refused as evidence here, and the refusal is specific. The shared fake is a real
  # HttpMessageHandler underneath the one HttpClient the production registration builds, so the pinned
  # host, the real handler settings, the real HMAC comparison, the real key builder, the real status
  # mapping and the real outbox consumer all still execute; only the bytes on the wire are the test's.
  # The webhook secret is really sealed by the delivered vault over a real key file and is asserted absent
  # from every table this package writes, unaligned. No stage here may be read as evidence that GitHub
  # works, that a repository is public, that a release has the shape these fixtures give it, or that a
  # secret GitHub holds matches one an owner stored: what these scenarios establish is Chronicleaf's own
  # handling of what GitHub answers.
  step "github release candidates tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~GithubReleaseCandidatesTests"
fi

step "frontend install" bash -euo pipefail -c 'cd src/Chronicleaf.Web && npm ci'
step "frontend build" bash -euo pipefail -c 'cd src/Chronicleaf.Web && npm run build'

if [ "${CHRONICLEAF_E2E:-0}" = "1" ]; then
  # Publishes the real application, starts a disposable PostgreSQL per suite, applies the committed
  # migrations, and drives the shell, access, projects-and-milestones, background-jobs,
  # milestone-evidence, credential-vault, voice-profiles, llm-provider-contract, github-evidence,
  # screenshot-capture, grounded-markdown-drafts, screenshot-curation, milestone-timeline,
  # markdown-review-export and narrative-memory fixtures in a real browser.
  # The suites after the shell are included by scripts/e2e.sh, which names each fixture file, so a
  # file that stops being matched shows up as a Playwright "no tests found" failure rather than as
  # a suite quietly passing with nothing in it; this stage
  # therefore fails if the browser cannot record a project, confirm a candidate, refuse a stale edit,
  # read a milestone's evidence and be told the truth by a 409 against a real database, turn emoji
  # off over a preset that asked for it and see the effective value change, be shown every provider
  # slot on a new installation with the truth about which of them can actually be called, see a
  # project admit it has no repository connected, be told in the server's own words when one is
  # refused, follow a real collection job to the status the operations route reports, read a
  # stored draft with its provenance, its citations and both finding lists and have that body rendered
  # as source rather than as HTML, or edit a stored draft into a new revision, approve the revision it
  # read, download the approved bytes exactly and see a later edit end the approval. The github suite needs outbound network to api.github.com: nothing in
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
