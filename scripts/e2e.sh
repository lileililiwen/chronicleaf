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
# Six fixtures groups run, each against its own published instance and its own disposable database,
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
# Five databases is the smallest way to keep each claim honest, and it means no group is run against
# state another one left behind.
# A sixth is what the GitHub fixture needs, for the same reason again: it asserts that a project starts
# with no repository connected, and a connection cannot be removed through the UI - there is no delete
# route, because the collection history on a milestone is not a thing the UI may throw away. It also
# needs outbound network to api.github.com, which is the one prerequisite in this file that is not a
# container.
#
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
log_file="$repo_root/$publish_dir/server.log"
evidence_log_file="$repo_root/$publish_dir/server-evidence.log"
credentials_log_file="$repo_root/$publish_dir/server-credentials.log"
voice_log_file="$repo_root/$publish_dir/server-voice.log"
generation_log_file="$repo_root/$publish_dir/server-generation.log"
github_log_file="$repo_root/$publish_dir/server-github.log"
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
    "$repo_root/$publish_dir/access-token-generation" "$repo_root/$publish_dir/access-token-github"
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
