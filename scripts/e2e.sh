#!/usr/bin/env bash
# Run the browser fixtures against the real built and published Chronicleaf application.
#
# The application is published and started from its own output; the Playwright fixtures drive a
# real browser against that server, and the server signs real sessions in a real disposable
# PostgreSQL. No mock, stub or dev server is involved.
#
# The access boundary is configured here the way a deployment configures it, because the browser
# fixtures sign in for real:
#   Chronicleaf__AccessTokenHash  SHA-256 hex of a token generated for this run only
#   Chronicleaf__PublicOrigin      the loopback origin the browser actually uses
#   ConnectionStrings__Chronicleaf the disposable PostgreSQL, migrated by the operator command
#
# The plaintext token is written to a file inside the publish directory, is never echoed, and is
# handed to the fixtures through the environment.
#
# Ten fixture groups run, each against its own published instance and its own disposable database,
# because one of them asserts something another one changes. tests/projects-and-milestones.spec.ts
# states that its installation starts with an empty project list - that is the claim it is making
# about a new installation - and a project cannot be removed once it exists, because archiving is
# the product's own answer. A milestone cannot exist without a project, so a fixture that needs a
# milestone cannot share that database with a fixture that is asserting nothing has been recorded.
# The credential fixture is the third because it is about a new installation too: it asserts that
# every provider reads as unconfigured before anything is stored, and a credential that was stored by
# an earlier fixture would make that claim false.
# The voice fixture is the fourth for the same reason. tests/voice-profiles.spec.ts asserts that a
# project's voice starts on the design's defaults, and then it saves one, turns emoji off and records
# a draft. A voice profile cannot be un-set through the UI - there is no reset control, because
# clearing an override is the owner's decision and not a side effect of opening a page - so a second
# run against the same installation would start on the profile the first run left behind and its
# first assertion would be false. Its own database is the only way that claim stays true on every run.
# The generation fixture is the fifth for the same reason again, and more sharply.
# tests/llm-provider-contract.spec.ts asserts that a new installation reads as unconfigured for every
# provider slot, that no slot reports an installed adapter, and that a selection nothing can call is
# refused before a redacted snapshot is frozen. A credential stored by the credential fixture would
# make the first claim false; a credential this fixture's own runs stored would make the second and third
# false on the next run, because a credential cannot be un-stored through the UI either. It is also
# the only suite that creates a milestone with recorded evidence, and an evidence bundle is append-only,
# so sharing a database with any other suite would put a second bundle where one suite expects one.
# Ten databases is the smallest way to keep each claim honest, and it means no group is run against
# state another one left behind.
# A sixth is what the GitHub fixture needs, for the same reason again: it asserts that a project starts
# with no repository connected, and a connection cannot be removed through the UI - there is no delete
# route, because the collection history on a milestone is not a thing the UI may throw away. It also
# needs outbound network to api.github.com, which is the one prerequisite in this file that is not a
# container.
# A seventh is what the capture fixture needs, for the same reason again: it creates a milestone and
# asserts what the capture panel reports for it, and a candidate cannot be removed through the UI
# either - curation owns selection and this feature deliberately offers no control that could throw one
# away. It needs no outbound network and no container beyond its own PostgreSQL, because a host that
# cannot confine a browser refuses the capture before any connection is made, and that refusal is what
# it asserts.
# An eighth is what the grounded-draft fixture needs, and for the same reason again: a stored draft
# cannot be removed through the UI, a project cannot be removed, and the suite needs one milestone WITH a
# draft and one without, which is the pair the empty state and the stored state are each proved against.
# A ninth is what the curation fixture needs, for the same reason again: a selection cannot be unmade
# through the UI - there is no undo for publishing a picture into evidence, because the bundle it
# entered is append-only - and the suite selects, rejects and reorders one milestone's
# pictures, which would leave a different choice behind for any suite that shared its database.
# A tenth is what the timeline fixture needs, for the same reason again: it records milestones into
# a project, and a project cannot be removed once it exists - so it reads its own installation
# rather than the one any other suite asserted empty.
# The credential vault reads a real operator key from a real file, generated for this run only, in a
# temporary directory that is removed when the run ends. The key is never written into the repository,
# into the published output or into a container image, is never echoed, and is passed to the
# application the way a deployment passes it: as Chronicleaf__EncryptionKeyFile.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_dir="${CHRONICLEAF_PUBLISH_DIR:-dist/e2e}"
base_url="${CHRONICLEAF_BASE_URL:-http://127.0.0.1:5081}"
evidence_base_url="${CHRONICLEAF_EVIDENCE_BASE_URL:-http://127.0.0.1:5082}"
credentials_base_url="${CHRONICLEAF_CREDENTIALS_BASE_URL:-http://127.0.0.1:5083}"
voice_base_url="${CHRONICLEAF_VOICE_BASE_URL:-http://127.0.0.1:5084}"
generation_base_url="${CHRONICLEAF_GENERATION_BASE_URL:-http://127.0.0.1:5085}"
github_base_url="${CHRONICLEAF_GITHUB_BASE_URL:-http://127.0.0.1:5086}"
capture_base_url="${CHRONICLEAF_CAPTURE_BASE_URL:-http://127.0.0.1:5087}"
drafts_base_url="${CHRONICLEAF_DRAFTS_BASE_URL:-http://127.0.0.1:5088}"
curation_base_url="${CHRONICLEAF_CURATION_BASE_URL:-http://127.0.0.1:5089}"
timeline_base_url="${CHRONICLEAF_TIMELINE_BASE_URL:-http://127.0.0.1:5090}"
log_file="$repo_root/$publish_dir/server.log"
evidence_log_file="$repo_root/$publish_dir/server-evidence.log"
credentials_log_file="$repo_root/$publish_dir/server-credentials.log"
voice_log_file="$repo_root/$publish_dir/server-voice.log"
generation_log_file="$repo_root/$publish_dir/server-generation.log"
github_log_file="$repo_root/$publish_dir/server-github.log"
capture_log_file="$repo_root/$publish_dir/server-capture.log"
drafts_log_file="$repo_root/$publish_dir/server-drafts.log"
curation_log_file="$repo_root/$publish_dir/server-curation.log"
timeline_log_file="$repo_root/$publish_dir/server-timeline.log"
key_file=""

# The operator key for this run: 32 random bytes in a temporary file outside the repository and
# outside the published output, so a leak has nowhere to land.
create_key_file() {
  key_file="$(mktemp -d)/chronicleaf-encryption.key"
  ( umask 077 && head -c 32 /dev/urandom >"$key_file" )
}

remove_key_file() {
  if [ -n "$key_file" ]; then
    rm -f "$key_file"
    rmdir "$(dirname "$key_file")" 2>/dev/null || true
    key_file=""
  fi
}

# The image is pinned; a floating tag would make this run unreproducible.
postgres_image="postgres:17.6-alpine"

server_pid=""
postgres_container=""

cleanup() {
  local exit_status=$?
  stop_suite
  # The tokens are secrets for the lifetime of this run and must not survive it.
  rm -f "$repo_root/$publish_dir/access-token" "$repo_root/$publish_dir/access-token-evidence" \
    "$repo_root/$publish_dir/access-token-credentials" "$repo_root/$publish_dir/access-token-voice" \
    "$repo_root/$publish_dir/access-token-generation" "$repo_root/$publish_dir/access-token-github" \
    "$repo_root/$publish_dir/access-token-capture" "$repo_root/$publish_dir/access-token-drafts" \
    "$repo_root/$publish_dir/access-token-curation" "$repo_root/$publish_dir/access-token-timeline"
  # So is the encryption key: it is removed whether the run passed or failed.
  remove_key_file
  exit "$exit_status"
}
trap cleanup EXIT

require_docker() {
  if ! command -v docker >/dev/null 2>&1; then
    echo "FAILED (prerequisite unavailable): docker is not on PATH; the browser fixtures need a real PostgreSQL container." >&2
    return 1
  fi
  if ! docker info >/dev/null 2>&1; then
    echo "FAILED (prerequisite unavailable): the docker daemon is not reachable; the browser fixtures need a real PostgreSQL container." >&2
    return 1
  fi
}
require_docker

# Stops the server and removes the container of whichever suite is running.
stop_suite() {
  if [ -n "$server_pid" ] && kill -0 "$server_pid" 2>/dev/null; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  server_pid=""
  if [ -n "$postgres_container" ]; then
    docker rm --force --volumes "$postgres_container" >/dev/null 2>&1 || true
    postgres_container=""
  fi
}

# start_suite <suffix> <origin> <log> <token file>
#
# Brings up a disposable PostgreSQL, applies the committed migrations, provisions the single
# installation, and starts the published application against it with a real access token hash and
# the origin the browser will actually use. The plaintext token is returned on stdout and never
# echoed.
start_suite() {
  local suffix="$1"
  local origin="$2"
  local log="$3"
  local token_path="$4"
  local database_name="chronicleaf_e2e_${suffix}"
  local container="chronicleaf-e2e-postgres-${suffix}-$$"
  local port=""

  postgres_container="$container"

  docker run --detach --rm \
    --name "$container" \
    --env POSTGRES_DB="$database_name" \
    --env POSTGRES_USER=chronicleaf \
    --env POSTGRES_PASSWORD=chronicleaf-e2e \
    --publish 127.0.0.1::5432 \
    "$postgres_image" >/dev/null

  port="$(docker port "$container" 5432/tcp | head -n 1 | sed 's/.*://')"
  if [ -z "$port" ]; then
    echo "the disposable PostgreSQL did not publish a port:" >&2
    docker logs "$container" >&2 || true
    return 1
  fi

  for _ in $(seq 1 60); do
    if docker exec "$container" pg_isready --username=chronicleaf --dbname="$database_name" >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done

  local connection_string="Host=127.0.0.1;Port=${port};Database=${database_name};Username=chronicleaf;Password=chronicleaf-e2e;Timeout=5;Command Timeout=10"

  (
    cd "$repo_root"
    ConnectionStrings__Chronicleaf="$connection_string" \
    Chronicleaf__AccessTokenHash="$(head -c 64 /dev/zero | tr '\0' '0' | sha256sum | cut -d' ' -f1)" \
      dotnet ef database update \
        --project src/Chronicleaf.Infrastructure \
        --startup-project src/Chronicleaf.Api \
        --no-build >/dev/null
  )

  # The singleton installation is operator-provisioned state, not something a visitor may claim.
  local installation_id
  installation_id="$(cat /proc/sys/kernel/random/uuid)"
  docker exec "$container" psql --username=chronicleaf --dbname="$database_name" --quiet --set=ON_ERROR_STOP=1 \
    --command "INSERT INTO installation (id, schema_version, created_at)
               VALUES ('${installation_id}', 1, now())
               ON CONFLICT (id) DO NOTHING;" >/dev/null

  # A token for this run only: 32 random bytes, hashed for the server and kept for the fixtures.
  local access_token access_token_hash
  access_token="$(openssl rand -hex 32)"
  access_token_hash="$(printf '%s' "$access_token" | sha256sum | cut -d' ' -f1)"
  printf '%s' "$access_token" >"$token_path"

  # exec replaces the subshell, so $! is the server process itself and the trap below
  # actually stops it instead of orphaning it on the port.
  (
    cd "$repo_root/$publish_dir" && exec env \
      ASPNETCORE_URLS="$origin" \
      ASPNETCORE_ENVIRONMENT=Production \
      ConnectionStrings__Chronicleaf="$connection_string" \
      Chronicleaf__AccessTokenHash="$access_token_hash" \
      Chronicleaf__PublicOrigin="$origin" \
      Chronicleaf__EncryptionKeyFile="$key_file" \
      dotnet Chronicleaf.Api.dll >"$log" 2>&1
  ) &
  server_pid=$!

  local ready=0
  for _ in $(seq 1 60); do
    if curl --fail --silent --show-error --max-time 2 "$origin/api/health" >/dev/null 2>&1; then
      ready=1
      break
    fi
    if ! kill -0 "$server_pid" 2>/dev/null; then
      echo "the published application exited before becoming ready:" >&2
      cat "$log" >&2
      return 1
    fi
    sleep 1
  done

  if [ "$ready" -ne 1 ]; then
    echo "the published application did not answer /api/health within 60s:" >&2
    cat "$log" >&2
    return 1
  fi
}

# run_suite <origin> <log> <token file> [playwright file filter...]
#
# The filters are a positive list, so a fixture file is only run when this script names it. The
# list is stated here rather than inferred, and a file that stops being matched shows up as a
# Playwright "no tests found" failure rather than as a suite quietly passing with nothing in it.
run_suite() {
  local origin="$1"
  local log="$2"
  local token_path="$3"
  shift 3

  (
    cd "$repo_root/src/Chronicleaf.Web" && \
      CHRONICLEAF_BASE_URL="$origin" \
      CHRONICLEAF_E2E_ACCESS_TOKEN="$(cat "$token_path")" \
      CHRONICLEAF_E2E_QUEUED_OPERATION="${CHRONICLEAF_E2E_QUEUED_OPERATION:-}" \
      npm run test:e2e -- "$@"
  )
}

bash "$repo_root/scripts/publish.sh" "$publish_dir" Release

# The vault's key is generated here, before any application is started, so every suite is configured
# the same way a deployment configures it.
create_key_file

# ---------------------------------------------------------------------------------------------
# The operations panel can only report operations that exist, and the only honest way to give the
# browser fixture some is to write real rows into the real database - the same way the integration
# scenarios do. Nothing here is simulated for the UI's benefit and no row invents a job that was
# never asked for: each is a genuine (scope, kind, key) submission the panel can read back through
# the real private route, and the queued rows carry no outbox event on purpose so the running
# application does not execute them and change what the fixture is asserting.
# ---------------------------------------------------------------------------------------------
seed_operations() {
  local container="$1"
  local database_name="$2"
  local installation_id="$3"
  local queued_operation cancelled_operation failed_operation succeeded_operation now

  queued_operation="$(cat /proc/sys/kernel/random/uuid)"
  cancelled_operation="$(cat /proc/sys/kernel/random/uuid)"
  failed_operation="$(cat /proc/sys/kernel/random/uuid)"
  succeeded_operation="$(cat /proc/sys/kernel/random/uuid)"
  now="$(date -u +%Y-%m-%dT%H:%M:%S.000000Z)"

  docker exec "$container" psql --username=chronicleaf --dbname="$database_name" --quiet --set=ON_ERROR_STOP=1 \
    --command "INSERT INTO operations (
                   id, scope_id, kind, target_id, input_revision, idempotency_key, fingerprint,
                   status, attempt, lease_recoveries, created_at, updated_at, revision)
                 VALUES
                   ('${queued_operation}',    '${installation_id}', 'test.queued',    gen_random_uuid(), 1, 'e2e-queued',    'e2e-queued',    'queued',    0, 0, '${now}', '${now}', 1),
                   ('${cancelled_operation}', '${installation_id}', 'test.cancelled', gen_random_uuid(), 1, 'e2e-cancelled', 'e2e-cancelled', 'cancelled', 1, 0, '${now}', '${now}', 2),
                   ('${failed_operation}',    '${installation_id}', 'test.failed',    gen_random_uuid(), 1, 'e2e-failed',    'e2e-failed',    'failed',    3, 0, '${now}', '${now}', 4),
                   ('${succeeded_operation}', '${installation_id}', 'test.succeeded', gen_random_uuid(), 1, 'e2e-succeeded', 'e2e-succeeded', 'succeeded', 1, 0, '${now}', '${now}', 3);
                 UPDATE operations
                 SET error_code = 'operation_transient_failure', completed_at = '${now}'
                 WHERE id = '${failed_operation}';
                 UPDATE operations
                 SET result_ref = 'e2e-result:export', completed_at = '${now}'
                 WHERE id = '${succeeded_operation}';
                 UPDATE operations
                 SET cancel_requested_at = '${now}', completed_at = '${now}'
                 WHERE id = '${cancelled_operation}';" >/dev/null

  CHRONICLEAF_E2E_QUEUED_OPERATION="$queued_operation"
  export CHRONICLEAF_E2E_QUEUED_OPERATION
}

# ---------------------------------------------------------------------------------------------
# The grounded-draft fixture: one real project, one real confirmed milestone, one real evidence
# bundle, and one stored draft whose TEXT is a fixture.
#
# Everything above the content tables is made through the running server's own routes with the real
# access token and a real antiforgery value, so the project, the milestone, its confirmation and its
# evidence are records the product itself created. Only the two content rows are inserted directly,
# and that is the same technique seed_operations above uses and for the same reason: a draft is produced
# by a provider call, and this installation has an adapter with an empty model allowlist, so there is
# nothing that could produce one honestly here.
#
# What the inserted rows are careful about:
#   - body_markdown is hashed with sha256sum and the digest stored, because the database's check
#     constraint refuses a row whose hash is not a hash and the panel shows that hash as provenance. A
#     seed that did not compute it would fail loudly rather than quietly store an unverified body.
#   - voice_snapshot is the delivered canonical form of the DEFAULT technical-blog voice, including a
#     fingerprint computed the way the delivered hasher computes it: SHA-256 over the values joined by
#     U+001F. A snapshot whose fingerprint did not match would be refused by the read route, so a
#     default that ever changes shows up as a failed suite and not as a blank page.
#   - summary is the accepted structured summary, and every claim cites the real evidence item, so the
#     grounding findings the panel shows are the ones the real bundle produces.
#   - The body contains a raw HTML tag on purpose. The requirement is that a draft is previewed as
#     source, and a fixture without one could not tell a rendering preview from a rendering that happens
#     to have no tags in it.
# ---------------------------------------------------------------------------------------------
seed_grounded_draft() {
  local origin="$1"
  local token="$2"
  local cookie_jar
  cookie_jar="$(mktemp -d)/cookies.txt"

  local antiforgery
  antiforgery="$(curl --silent --show-error --cookie-jar "$cookie_jar" --cookie "$cookie_jar" \
    --data "{\"accessToken\":\"${token}\"}" \
    -H 'Content-Type: application/json' \
    "${origin}/api/session" >/dev/null && \
    curl --silent --show-error --cookie-jar "$cookie_jar" --cookie "$cookie_jar" \
    -H 'Accept: application/json' "${origin}/api/session/antiforgery" | jq -r '.token')"

  if [ -z "$antiforgery" ] || [ "$antiforgery" = "null" ]; then
    echo "the grounded-draft seed could not read an antiforgery value from ${origin}" >&2
    return 1
  fi

  # Every mutation below carries the real origin header as well as the antiforgery value, because the
  # delivered boundary checks both and a request missing either is refused for the wrong reason.
  local project_id milestone_id
  project_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"name":"Ledger","description":"Double-entry accounting."}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects" | jq -r '.id')"

  milestone_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"title":"First reproducible release","version":"0.1.0","occurredAt":"2026-10-01T09:00:00Z","type":"feature","notes":"Recorded by the e2e seed.","knownLimitations":[],"nextSteps":[]}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects/${project_id}/milestones" | jq -r '.id')"

  if [ -z "$project_id" ] || [ "$project_id" = "null" ] || [ -z "$milestone_id" ] || [ "$milestone_id" = "null" ]; then
    echo "the grounded-draft seed could not create its project and milestone" >&2
    return 1
  fi

  # A manual milestone is confirmed by the act of saving it, so no confirm call is needed here - and
  # asserting that in a browser is suite 4's job rather than this script's.
  curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"notes":"The pipeline now reproduces from a clean checkout.","expectedRevision":0}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/milestones/${milestone_id}/evidence/manual" >/dev/null

  local bundle_id
  bundle_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    -H 'Accept: application/json' "${origin}/api/milestones/${milestone_id}/evidence" \
    | jq -r '.items[0].id')"

  local evidence_item_id
  evidence_item_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    -H 'Accept: application/json' "${origin}/api/milestones/${milestone_id}/evidence" \
    | jq -r '.items[0].items[0].id')"

  if [ -z "$evidence_item_id" ] || [ "$evidence_item_id" = "null" ]; then
    echo "the grounded-draft seed could not read the evidence bundle the product created" >&2
    return 1
  fi

  seed_content_rows "$postgres_container" "chronicleaf_e2e_drafts" \
    "$project_id" "$milestone_id" "$bundle_id" "$evidence_item_id"

  CHRONICLEAF_E2E_DRAFT_MILESTONE="$milestone_id"
  export CHRONICLEAF_E2E_DRAFT_MILESTONE
}

# The two content rows, inserted into the disposable database the drafts suite is using.
seed_content_rows() {
  local container="$1"
  local database_name="$2"
  local project_id="$3"
  local milestone_id="$4"
  local bundle_id="$5"
  local evidence_item_id="$6"

  local body summary voice_snapshot body_hash fingerprint now
  now="$(date -u +%Y-%m-%dT%H:%M:%S.000000Z)"

  # The body, with a raw tag in it so the preview claim is actually testable. Numbers are avoided so the
  # fixture cannot trip this package's own numeral rule - which is a real rule, and a fixture that
  # violated it would be testing the wrong thing.
  body=$'# Reproducible release pipeline\n\nThe pipeline now reproduces from a clean checkout.[^c1]\n\n## What changed\n\n- The pipeline runs from a clean checkout.[^c1]\n- The build produces the same digest each time.[^c2]\n\n## Problems solved\n\nA release could not be rebuilt from its source.[^c3]\n\n## Known limitations\n\nOnly Linux is covered.[^c4]\n\n## Next steps\n\nCover the other platform.[^c5]\n\nThe line below is a <b>not bold</b> tag in the source, and a preview renders it as these characters.\n\n[^c1]: The pipeline runs from a clean checkout.\n[^c2]: The build produces the same digest each time.\n[^c3]: A release could not be rebuilt from its source.\n[^c4]: Only Linux is covered.\n[^c5]: Cover the other platform.\n'

  body_hash="$(printf '%s' "$body" | sha256sum | cut -d' ' -f1)"

  # The delivered hasher's canonical form: the resolved values joined by U+001F, in the order
  # VoiceSnapshotHasher writes them. These are the default technical-blog values, and the read route
  # recomputes the fingerprint rather than trusting the stored one - so a default that ever changes makes
  # this suite fail rather than make the panel show a voice it did not check.
  fingerprint="$(printf 'zh\037restrained\037true\03760\037high\037low\037true\037革命性\037震撼\037game-changing\037exciting news' \
    | sha256sum | cut -d' ' -f1)"

  voice_snapshot="{\"presetId\":\"technical-blog\",\"profileRevision\":0,\"capturedAt\":\"${now}\",\"language\":\"zh\",\"tone\":\"restrained\",\"emoji\":true,\"titleMaxChars\":60,\"technicalDetail\":\"high\",\"marketing\":\"low\",\"firstPerson\":true,\"forbiddenTerms\":[\"革命性\",\"震撼\",\"game-changing\",\"exciting news\"],\"fingerprint\":\"${fingerprint}\"}"

  summary=$(jq -cn --arg e "$evidence_item_id" '{
    milestoneTitle: "Reproducible release pipeline",
    summary: "The pipeline now reproduces from a clean checkout.",
    changes: [
      { text: "The pipeline runs from a clean checkout.", evidenceIds: [$e] },
      { text: "The build produces the same digest each time.", evidenceIds: [$e] }
    ],
    problemsSolved: [
      { text: "A release could not be rebuilt from its source.", evidenceIds: [$e] }
    ],
    knownLimitations: [
      { text: "Only Linux is covered.", evidenceIds: [$e] }
    ],
    nextSteps: [
      { text: "Cover the other platform.", evidenceIds: [$e] }
    ]
  }')

  local asset_id revision_id scope_id
  asset_id="$(cat /proc/sys/kernel/random/uuid)"
  revision_id="$(cat /proc/sys/kernel/random/uuid)"
  scope_id="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" --quiet --tuples-only --no-align \
    --command 'SELECT id FROM installation LIMIT 1')"

  # One statement, so a failure leaves nothing half-written. The trigger on this table refuses UPDATE and
  # DELETE, which is the append-only contract, and an INSERT is what a completed generation would do.
  #
  # `-i` is load-bearing: `docker exec` only forwards this heredoc to psql's stdin when it is asked to,
  # and without it psql reads an empty script and exits 0 having inserted nothing. A seed that reports
  # success and stores no rows is worse than one that fails, so the rows are read back below and the
  # seed refuses to continue if they are not there.
  #
  # The three long values travel as psql variables and are interpolated with `:'name'`, which is psql's
  # own quoting. A body interpolated into the SQL text by the shell would have to be escaped by the
  # shell, and the two languages disagree about what a quote is: bash's ${var@Q} writes $'...' and
  # `'...'` for strings containing newlines, and neither is SQL.
  docker exec -i "$container" psql --username=chronicleaf --dbname="$database_name" --quiet \
    --set=ON_ERROR_STOP=1 --set=body="$body" --set=summary="$summary" --set=voice_snapshot="$voice_snapshot" \
    <<SQL >/dev/null
INSERT INTO content_assets
  (id, scope_id, project_id, milestone_id, type, status, current_revision, created_at, updated_at)
VALUES
  ('${asset_id}', '${scope_id}', '${project_id}', '${milestone_id}', 'markdown-story', 'draft', 1, '${now}', '${now}');

INSERT INTO content_revisions
  (id, asset_id, scope_id, revision, evidence_bundle_id, evidence_revision, voice_revision,
   voice_snapshot, summary, body_markdown, body_hash, provider_id, model, prompt_version, schema_version, created_at)
VALUES
  ('${revision_id}', '${asset_id}', '${scope_id}', 1, '${bundle_id}', 1, 0,
   :'voice_snapshot'::jsonb, :'summary'::jsonb, :'body', '${body_hash}',
   'openai', 'seeded-not-called', 'grounded-markdown/1', 'grounded-summary/1', '${now}');
SQL

  # Read back what the database actually holds, and compare the stored digest with the one computed
  # above. psql exiting zero is not evidence that a statement ran, and a stored body whose hash is not
  # its own would be a fixture proving nothing: the panel shows that hash as provenance.
  local stored
  stored="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" \
    --quiet --tuples-only --no-align \
    --command "SELECT count(*) || ' ' || coalesce(max(body_hash), '') FROM content_revisions WHERE asset_id = '${asset_id}'")"

  if [ "$stored" != "1 ${body_hash}" ]; then
    echo "the grounded-draft seed stored '${stored}', which is not the one revision hashed as ${body_hash}" >&2
    return 1
  fi
}

# ---------------------------------------------------------------------------------------------
# The curation fixture: one real project, one real milestone, one real evidence bundle, real media
# bytes and three offered candidates pointing at them.
#
# Everything above the candidate rows is made through the running server's own routes with the real
# access token and a real antiforgery value, so the project, the milestone, its evidence, its
# media and the two capture operations are records the product itself created. The two capture jobs
# will fail with capture_sandbox_unavailable on this host - no browser can be confined here - and
# they write no candidates, which is exactly why the three rows are inserted directly: the
# operation ids they carry are real jobs this installation asked for, not invented provenance.
#
# What the inserted rows are careful about:
#   - the bytes are a real 1x1 PNG uploaded through the real media route, decoded and verified by
#     the delivered store. A file the store refused would fail loudly at upload rather than quietly
#     seed an image that cannot be served.
#   - the viewport column is the codec's own shape - {"name","width","height"} in camel case - so a
#     row the product cannot read back as a viewport it offers is never written.
#   - status, caption, primary flag, display order and revision are the constants a capture inserts,
#     because curation owns every move away from them and the seed makes no choice on its behalf.
#   - the rows are read back and the seed refuses to continue unless all three are stored: psql
#     exiting zero is not evidence that a statement ran.
# ---------------------------------------------------------------------------------------------
seed_curation() {
  local origin="$1"
  local token="$2"
  local cookie_jar
  cookie_jar="$(mktemp -d)/cookies.txt"

  local antiforgery
  antiforgery="$(curl --silent --show-error --cookie-jar "$cookie_jar" --cookie "$cookie_jar" \
    --data "{\"accessToken\":\"${token}\"}" \
    -H 'Content-Type: application/json' \
    "${origin}/api/session" >/dev/null && \
    curl --silent --show-error --cookie-jar "$cookie_jar" --cookie "$cookie_jar" \
    -H 'Accept: application/json' "${origin}/api/session/antiforgery" | jq -r '.token')"

  if [ -z "$antiforgery" ] || [ "$antiforgery" = "null" ]; then
    echo "the curation seed could not read an antiforgery value from ${origin}" >&2
    return 1
  fi

  local project_id milestone_id
  project_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"name":"Storefront","description":"The marketing site."}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects" | jq -r '.id')"

  milestone_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"title":"Pricing page refresh","version":"0.2.0","occurredAt":"2026-10-04T09:30:00Z","type":"feature","notes":"Both viewports agree on price.","knownLimitations":[],"nextSteps":[]}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects/${project_id}/milestones" | jq -r '.id')"

  if [ -z "$project_id" ] || [ "$project_id" = "null" ] || [ -z "$milestone_id" ] || [ "$milestone_id" = "null" ]; then
    echo "the curation seed could not create its project and milestone" >&2
    return 1
  fi

  # One hand-written evidence item, so every choice below is conditional on a bundle that has
  # something in it rather than on an empty revision that proves nothing.
  curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"notes":"The pricing page was refreshed.","expectedRevision":0}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/milestones/${milestone_id}/evidence/manual" >/dev/null

  # Two real capture requests, so the candidate rows below name jobs this installation asked for.
  # Both jobs will refuse with capture_sandbox_unavailable on a host that cannot confine a
  # browser, and neither writes a candidate - which the seed asserts no part of: the candidates
  # are the three rows below, and the jobs are their provenance.
  local operation_pricing operation_changelog
  operation_pricing="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"url":"https://example.com/pricing","viewports":["desktop","mobile"],"expectedBundleRevision":1,"idempotencyKey":"e2e-curation-pricing"}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 1' \
    "${origin}/api/milestones/${milestone_id}/captures" | jq -r '.operationId')"
  operation_changelog="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"url":"https://example.com/changelog","viewports":["desktop"],"expectedBundleRevision":1,"idempotencyKey":"e2e-curation-changelog"}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 1' \
    "${origin}/api/milestones/${milestone_id}/captures" | jq -r '.operationId')"

  if [ -z "$operation_pricing" ] || [ "$operation_pricing" = "null" ] || [ -z "$operation_changelog" ] || [ "$operation_changelog" = "null" ]; then
    echo "the curation seed could not request its capture jobs" >&2
    return 1
  fi

  # One real PNG through the real media route. It is a 1x1 fixture, decoded and verified by the
  # delivered store - and what the browser suite draws from it is the served bytes, never a claim
  # that a page was photographed.
  local png_file
  png_file="$(mktemp -d)/pixel.png"
  printf '%s' 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==' \
    | base64 --decode >"$png_file"

  local media_id
  media_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data-binary "@${png_file}" \
    -H 'Content-Type: image/png' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/media?projectId=${project_id}" | jq -r '.id')"
  rm -f "$png_file"

  if [ -z "$media_id" ] || [ "$media_id" = "null" ]; then
    echo "the curation seed could not upload its fixture image" >&2
    return 1
  fi

  seed_curation_rows "$postgres_container" "chronicleaf_e2e_curation" \
    "$project_id" "$milestone_id" "$media_id" "$operation_pricing" "$operation_changelog"

  CHRONICLEAF_E2E_CURATION_PROJECT="$project_id"
  export CHRONICLEAF_E2E_CURATION_PROJECT
  CHRONICLEAF_E2E_CURATION_MILESTONE="$milestone_id"
  export CHRONICLEAF_E2E_CURATION_MILESTONE
}

# The three offered candidates, inserted into the disposable database the curation suite is using.
seed_curation_rows() {
  local container="$1"
  local database_name="$2"
  local project_id="$3"
  local milestone_id="$4"
  local media_id="$5"
  local operation_pricing="$6"
  local operation_changelog="$7"

  local now candidate_a candidate_b candidate_c scope_id
  now="$(date -u +%Y-%m-%dT%H:%M:%S.000000Z)"
  candidate_a="$(cat /proc/sys/kernel/random/uuid)"
  candidate_b="$(cat /proc/sys/kernel/random/uuid)"
  candidate_c="$(cat /proc/sys/kernel/random/uuid)"
  scope_id="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" --quiet --tuples-only --no-align \
    --command 'SELECT id FROM installation LIMIT 1')"

  # One statement, so a failure leaves nothing half-written. The unique tuple is
  # (operation, source url, viewport): the two pricing rows share a job and differ by viewport,
  # and the changelog row is its own job.
  #
  # `-i` is load-bearing for the same reason the grounded-draft seed states: without it the
  # heredoc never reaches psql, psql exits 0 having inserted nothing, and the suite would run
  # against an empty choice it mistakes for an offered one. The rows are read back below.
  docker exec -i "$container" psql --username=chronicleaf --dbname="$database_name" --quiet \
    --set=ON_ERROR_STOP=1 \
    <<SQL >/dev/null
INSERT INTO screenshot_candidates
  (id, scope_id, project_id, milestone_id, media_id, operation_id, source_url, final_url,
   viewport, captured_at, status, caption, is_primary, display_order, revision, created_at)
VALUES
  ('${candidate_a}', '${scope_id}', '${project_id}', '${milestone_id}', '${media_id}', '${operation_pricing}',
   'https://example.com/pricing', 'https://example.com/pricing',
   '{"name":"desktop","width":1440,"height":900}', '${now}', 'candidate', '', false, 0, 1, '${now}'),
  ('${candidate_b}', '${scope_id}', '${project_id}', '${milestone_id}', '${media_id}', '${operation_pricing}',
   'https://example.com/pricing', 'https://example.com/pricing',
   '{"name":"mobile","width":390,"height":844}', '${now}', 'candidate', '', false, 0, 1, '${now}'),
  ('${candidate_c}', '${scope_id}', '${project_id}', '${milestone_id}', '${media_id}', '${operation_changelog}',
   'https://example.com/changelog', 'https://example.com/changelog',
   '{"name":"desktop","width":1440,"height":900}', '${now}', 'candidate', '', false, 0, 1, '${now}');
SQL

  # Read back what the database actually holds. A count that is not three, or a row whose
  # viewport the product cannot read back as a viewport it offers, refuses the suite: the panel
  # under test renders viewports, and a row with an unreadable one would prove nothing.
  local stored
  stored="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" \
    --quiet --tuples-only --no-align \
    --command "SELECT count(*) FROM screenshot_candidates WHERE milestone_id = '${milestone_id}' AND status = 'candidate'")"

  if [ "$stored" != "3" ]; then
    echo "the curation seed stored '${stored}' candidates, which is not the three offered rows" >&2
    return 1
  fi
}
# ---------------------------------------------------------------------------------------------
# Suite 1: the shell, the access boundary, the projects and milestones lifecycle, and the
# background operations panel. Its project list must be empty when it starts, which is why this
# suite has a database to itself.
# ---------------------------------------------------------------------------------------------
echo "==> starting the disposable PostgreSQL and published application at $base_url"
start_suite core "$base_url" "$log_file" "$repo_root/$publish_dir/access-token"
core_installation="$(docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf_e2e_core --quiet --tuples-only --no-align --command 'SELECT id FROM installation LIMIT 1')"

echo "==> recording the background operations the panel fixture reads"
seed_operations "$postgres_container" chronicleaf_e2e_core "$core_installation"

echo "==> npm run test:e2e (real browser against $base_url)"
run_suite "$base_url" "$log_file" "$repo_root/$publish_dir/access-token" \
  e2e/shell.spec.ts \
  tests/background-jobs.spec.ts \
  tests/community-access.spec.ts \
  tests/projects-and-milestones.spec.ts
stop_suite

# ---------------------------------------------------------------------------------------------
# Suite 2: the milestone evidence view. It needs a milestone, and a milestone needs a project, and
# a project is never removed - so it runs against its own installation and its own published
# instance rather than against the one above.
# ---------------------------------------------------------------------------------------------
echo "==> starting a second disposable PostgreSQL and published application at $evidence_base_url"
start_suite evidence "$evidence_base_url" "$evidence_log_file" "$repo_root/$publish_dir/access-token-evidence"

echo "==> npm run test:e2e (real browser against $evidence_base_url)"
run_suite "$evidence_base_url" "$evidence_log_file" "$repo_root/$publish_dir/access-token-evidence" \
  tests/milestone-evidence.spec.ts
stop_suite

# ---------------------------------------------------------------------------------------------
# Suite 3: the credential settings surface. It asserts that a new installation reads as
# unconfigured for every provider, and a stored credential cannot be un-stored by the UI, so it needs
# its own installation and its own published instance.
# ---------------------------------------------------------------------------------------------
echo "==> starting a third disposable PostgreSQL and published application at $credentials_base_url"
start_suite credentials "$credentials_base_url" "$credentials_log_file" "$repo_root/$publish_dir/access-token-credentials"

echo "==> npm run test:e2e (real browser against $credentials_base_url)"
run_suite "$credentials_base_url" "$credentials_log_file" "$repo_root/$publish_dir/access-token-credentials" \
  tests/credential-vault.spec.ts
stop_suite

# ---------------------------------------------------------------------------------------------
# Suite 4: the writing voice. It asserts that a project starts on the design's defaults, and it then
# changes them and records a draft under the changed voice - none of which it can undo through the
# UI. It therefore needs its own installation and its own published instance, and it is the fourth
# suite for exactly the reason suite 1 is the first.
# ---------------------------------------------------------------------------------------------
echo "==> starting a fourth disposable PostgreSQL and published application at $voice_base_url"
start_suite voice "$voice_base_url" "$voice_log_file" "$repo_root/$publish_dir/access-token-voice"

echo "==> npm run test:e2e (real browser against $voice_base_url)"
run_suite "$voice_base_url" "$voice_log_file" "$repo_root/$publish_dir/access-token-voice" \
  tests/voice-profiles.spec.ts
stop_suite

# ---------------------------------------------------------------------------------------------
# Suite 5: the bounded generation surface. It asserts that a new installation reads as unconfigured
# for every provider slot, that no slot reports an adapter, and that a selection nothing can call is
# refused before a snapshot is frozen - all of which a stored credential or an earlier run of this
# suite itself would make false. Its own installation, its own database and its own published instance.
# ---------------------------------------------------------------------------------------------
echo "==> starting a fifth disposable PostgreSQL and published application at $generation_base_url"
start_suite generation "$generation_base_url" "$generation_log_file" "$repo_root/$publish_dir/access-token-generation"

echo "==> npm run test:e2e (real browser against $generation_base_url)"
run_suite "$generation_base_url" "$generation_log_file" "$repo_root/$publish_dir/access-token-generation" \
  tests/llm-provider-contract.spec.ts
stop_suite

# ---------------------------------------------------------------------------------------------
# Suite 6: the GitHub repository connection and the collection a milestone asks it for. It needs a
# project with no connection to begin with, and a connection cannot be removed through the UI, so its
# own installation, its own database and its own published instance - for the same reason suite 5 is
# its own. It also asserts that a token this suite typed is nowhere in the delivered page afterwards,
# which is only a claim about a run that stored one itself.
#
# Nothing about GitHub is faked here and nothing is seeded, unlike the operations panel above: the
# connection is stored by the panel under test through the real route, and a real repository is read by
# the real server from GitHub's own host. That makes this the one suite in the file with an outbound
# prerequisite. Where api.github.com cannot be reached the collection scenario fails and says so, which
# is recorded as a prerequisite failure rather than quietly passed - and it is never replaced by a stub
# handler, because a fake GitHub would let a pass stand in for a result only a real one can give.
# ---------------------------------------------------------------------------------------------
echo "==> starting a sixth disposable PostgreSQL and published application at $github_base_url"
start_suite github "$github_base_url" "$github_log_file" "$repo_root/$publish_dir/access-token-github"

echo "==> npm run test:e2e (real browser against $github_base_url)"
run_suite "$github_base_url" "$github_log_file" "$repo_root/$publish_dir/access-token-github" \
  tests/github-evidence.spec.ts
stop_suite

# ---------------------------------------------------------------------------------------------
# Suite 7: bounded screenshot capture for a milestone. It creates a project and a milestone and then
# asserts what the capture panel reports, so it needs a milestone of its own - a milestone cannot
# exist without a project, and a project is never removed - and it must not share a database with any
# other suite. Its own installation, its own database and its own published instance, for the same
# reason suite 6 is its own.
#
# What this suite claims is worth stating, because it is narrower than the feature. Capturing a page
# means running a browser confined with no access to this machine's network. A host that cannot
# provide that isolation - no user namespaces, no CAP_NET_ADMIN, no iptables and no nft - answers
# capture_sandbox_unavailable before any browser is started, and the design treats native capture
# outside that sandbox as unavailable rather than as a less-secure fallback. So where the host cannot
# confine a browser, this suite asserts the refusal is VISIBLE: the panel names the code, says no page
# was visited and no picture exists, draws no candidate and offers nothing that looks like a retry.
# That is a real claim about the delivered product and it is what SCREENSHOT-CAPTURE-002 exists to
# protect. It is not a claim that a screenshot was captured, and a candidate image is only asserted
# when the server actually published one.
#
# The half that needs no browser at all is asserted unconditionally: a private destination is refused
# by the server's URL policy before a job is created, so the panel is shown refusing a page without any
# capture being attempted. Nothing here is seeded and nothing is faked - the candidate rows, the job
# and its stored failure code are all written by the real server and read back through its own routes.
# ---------------------------------------------------------------------------------------------
echo "==> starting a seventh disposable PostgreSQL and published application at $capture_base_url"
start_suite capture "$capture_base_url" "$capture_log_file" "$repo_root/$publish_dir/access-token-capture"

echo "==> npm run test:e2e (real browser against $capture_base_url)"
run_suite "$capture_base_url" "$capture_log_file" "$repo_root/$publish_dir/access-token-capture" \
  tests/screenshot-capture.spec.ts
stop_suite

# ---------------------------------------------------------------------------------------------
# Suite 8: the grounded-draft surface. It needs a milestone the browser can read, and it needs one
# with a stored draft, so it has its own installation and its own published instance for the same
# reason suites 1 to 7 each have one.
#
# What this suite claims, and what it does not. The project, the milestone, its confirmation and its
# evidence are created by the running server's own routes through the real access boundary, and the
# stored draft is inserted as two rows a completed generation would write - with the body hash computed
# from its own bytes and the voice snapshot in the delivered canonical form. So the panel under test is
# production's and the state it renders is real.
#
# No provider was called, and none could have been: this installation has an OpenAI adapter whose model
# allowlist is empty, so every provider is refused before a credential is leased. The fixture asserts
# that refusal in the browser rather than hiding it, which is why the empty state and the
# "no provider can be called" notice are scenarios here and not a gap. A pass is evidence that the panel
# reads a stored draft faithfully - its provenance, its citations, its findings, and its refusal to
# render the body as HTML - and is NOT evidence that a provider can be called, that a model exists, or
# that a draft was generated on this host. The .NET suite is where a real answer flows through both
# generation phases.
# ---------------------------------------------------------------------------------------------
echo "==> starting an eighth disposable PostgreSQL and published application at $drafts_base_url"
start_suite drafts "$drafts_base_url" "$drafts_log_file" "$repo_root/$publish_dir/access-token-drafts"

echo "==> recording the project, evidence and stored draft the panel reads"
seed_grounded_draft "$drafts_base_url" "$(cat "$repo_root/$publish_dir/access-token-drafts")"

echo "==> npm run test:e2e (real browser against $drafts_base_url)"
run_suite "$drafts_base_url" "$drafts_log_file" "$repo_root/$publish_dir/access-token-drafts" \
  tests/grounded-markdown-drafts.spec.ts
stop_suite

# ---------------------------------------------------------------------------------------------
# Suite 9: the screenshot picker. It needs a milestone the browser can choose from, and it needs
# offered candidates with real bytes behind them, so it has its own installation and its own
# published instance for the same reason suites 1 to 8 each have one.
#
# What this suite claims, and what it does not. The project, the milestone, its evidence, its
# media and its capture jobs are created by the running server's own routes through the real
# access boundary. The three candidate rows are inserted the way the grounded-draft suite inserts
# its rows - directly, with the rows read back and refused when they are not there - because a
# candidate is produced by a browser and this host cannot confine one. The bytes behind all three
# are one real 1x1 PNG uploaded through the real media route and verified by the delivered store:
# a fixture, never presented as something a browser photographed. So a pass is evidence that the
# picker records a human choice faithfully and publishes it into evidence - select with a caption,
# reject, primary, caption edit, reorder and the 409 for a stale change - and is NOT evidence that
# a screenshot was captured on this host. The .NET suite is where candidates are produced by real
# capture jobs behind the delivered provider seam.
# ---------------------------------------------------------------------------------------------
echo "==> starting a ninth disposable PostgreSQL and published application at $curation_base_url"
start_suite curation "$curation_base_url" "$curation_log_file" "$repo_root/$publish_dir/access-token-curation"

echo "==> recording the project, evidence, media and offered candidates the picker reads"
seed_curation "$curation_base_url" "$(cat "$repo_root/$publish_dir/access-token-curation")"

echo "==> npm run test:e2e (real browser against $curation_base_url)"
run_suite "$curation_base_url" "$curation_log_file" "$repo_root/$publish_dir/access-token-curation" \
  tests/screenshot-curation.spec.ts
stop_suite

# ---------------------------------------------------------------------------------------------
# Suite 10: the private timeline. It needs a project with milestones it recorded itself, and a
# project cannot be removed once it exists - so it has its own installation and its own published
# instance for the same reason suites 1 to 9 each have one.
#
# Nothing here is seeded, unlike the operations, drafts and curation fixtures above: every
# project and milestone is created by the same clicks an owner would make, and the one candidate
# is created through the delivered intake route with the browser's own session, because no
# product surface collects records from a source yet. A pass is evidence that the panel reads the
# confirmed history oldest first under its own dates, hides candidates until asked, labels them
# when shown, and opens a row's own milestone editor - and that the server says the same thing
# the panel does.
# ---------------------------------------------------------------------------------------------
echo "==> starting a tenth disposable PostgreSQL and published application at $timeline_base_url"
start_suite timeline "$timeline_base_url" "$timeline_log_file" "$repo_root/$publish_dir/access-token-timeline"

echo "==> npm run test:e2e (real browser against $timeline_base_url)"
run_suite "$timeline_base_url" "$timeline_log_file" "$repo_root/$publish_dir/access-token-timeline" \
  tests/milestone-timeline.spec.ts
stop_suite
