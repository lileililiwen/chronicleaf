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
# Thirty fixture groups run, each against its own published instance and its own database,
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
# Thirty databases is the smallest way to keep each claim honest, and it means no group is run against
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
# PG-ADOPT-004: the thirty databases above are TEMPLATE clones of ONE migrated seed database
# in ONE shared postgres container (start_shared_postgres + clone_suite_database), and the
# thirty suites run N-way parallel (CHRONICLEAF_E2E_JOBS, default 4) with per-suite logs and
# aggregated failure reporting. Isolation is unchanged — own database, own server, own port,
# own token per suite — and Playwright keeps workers: 1 inside each suite, because intra-suite
# parallelism would break empty-state claims sharing one database.
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
review_base_url="${CHRONICLEAF_REVIEW_BASE_URL:-http://127.0.0.1:5091}"
candidates_base_url="${CHRONICLEAF_CANDIDATES_BASE_URL:-http://127.0.0.1:5092}"
narrative_base_url="${CHRONICLEAF_NARRATIVE_BASE_URL:-http://127.0.0.1:5093}"
variants_base_url="${CHRONICLEAF_VARIANTS_BASE_URL:-http://127.0.0.1:5094}"
team_base_url="${CHRONICLEAF_TEAM_BASE_URL:-http://127.0.0.1:5095}"
tenancy_base_url="${CHRONICLEAF_TENANCY_BASE_URL:-http://127.0.0.1:5096}"
evolution_base_url="${CHRONICLEAF_EVOLUTION_BASE_URL:-http://127.0.0.1:5097}"
gif_base_url="${CHRONICLEAF_GIF_BASE_URL:-http://127.0.0.1:5098}"
publication_base_url="${CHRONICLEAF_PUBLICATION_BASE_URL:-http://127.0.0.1:5099}"
journey_base_url="${CHRONICLEAF_JOURNEY_BASE_URL:-http://127.0.0.1:5100}"
area_base_url="${CHRONICLEAF_AREA_BASE_URL:-http://127.0.0.1:5101}"
publish_variant_base_url="${CHRONICLEAF_PUBLISH_VARIANT_BASE_URL:-http://127.0.0.1:5102}"
static_blog_export_base_url="${CHRONICLEAF_STATIC_BLOG_EXPORT_BASE_URL:-http://127.0.0.1:5103}"
adapter_panels_base_url="${CHRONICLEAF_ADAPTER_PANELS_BASE_URL:-http://127.0.0.1:5104}"
content_pack_base_url="${CHRONICLEAF_CONTENT_PACK_BASE_URL:-http://127.0.0.1:5105}"
agency_base_url="${CHRONICLEAF_AGENCY_BASE_URL:-http://127.0.0.1:5106}"
video_story_base_url="${CHRONICLEAF_VIDEO_STORY_BASE_URL:-http://127.0.0.1:5107}"
wayfinding_base_url="${CHRONICLEAF_WAYFINDING_BASE_URL:-http://127.0.0.1:5108}"
a11y_roles_base_url="${CHRONICLEAF_A11Y_ROLES_BASE_URL:-http://127.0.0.1:5109}"
responsive_base_url="${CHRONICLEAF_RESPONSIVE_BASE_URL:-http://127.0.0.1:5110}"
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
review_log_file="$repo_root/$publish_dir/server-review.log"
candidates_log_file="$repo_root/$publish_dir/server-candidates.log"
narrative_log_file="$repo_root/$publish_dir/server-narrative.log"
variants_log_file="$repo_root/$publish_dir/server-variants.log"
team_log_file="$repo_root/$publish_dir/server-team.log"
tenancy_log_file="$repo_root/$publish_dir/server-tenancy.log"
evolution_log_file="$repo_root/$publish_dir/server-evolution.log"
gif_log_file="$repo_root/$publish_dir/server-gif.log"
publication_log_file="$repo_root/$publish_dir/server-publication.log"
journey_log_file="$repo_root/$publish_dir/server-journey.log"
area_log_file="$repo_root/$publish_dir/server-area.log"
publish_variant_log_file="$repo_root/$publish_dir/server-publish-variant.log"
static_blog_export_log_file="$repo_root/$publish_dir/server-static-blog-export.log"
adapter_panels_log_file="$repo_root/$publish_dir/server-adapter-panels.log"
content_pack_log_file="$repo_root/$publish_dir/server-content-pack.log"
agency_log_file="$repo_root/$publish_dir/server-agency.log"
video_story_log_file="$repo_root/$publish_dir/server-video-story.log"
wayfinding_log_file="$repo_root/$publish_dir/server-wayfinding.log"
a11y_roles_log_file="$repo_root/$publish_dir/server-a11y-roles.log"
responsive_log_file="$repo_root/$publish_dir/server-responsive.log"
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

# PG-ADOPT-004: ONE postgres container serves the whole run. Per-suite databases are
# TEMPLATE clones of a single migrated seed database, so per-suite isolation (own DB,
# own server, own port, own token) is unchanged while container boot and migration replay
# happen exactly once. Suite functions run in background subshells; each sets its own
# server_pid there, so the only cross-suite globals below are read-only after boot.
server_pid=""
shared_postgres_container=""
shared_postgres_port=""
seed_database="chronicleaf_e2e_seed"

# Read-only alias for the seed functions (seed_operations, seed_*_rows), which address
# the container by name: there is only one container now, so the name never changes.
postgres_container=""
# The suite's own database name, set by start_suite in each suite's subshell for the seeds
# that address the database by global name. Never set at top level: parallel suites must
# never share it.
postgres_database=""

cleanup() {
  local exit_status=$?
  stop_suite
  # PG-TIGHTEN-FU-001: kill every dotnet Chronicleaf.Api.dll this script may have
  # started, so a subsequent run does not see them as orphan port holders. pkill
  # exits 1 when nothing matches; that is the clean-run case and is ignored here.
  pkill -f "dotnet Chronicleaf.Api.dll" >/dev/null 2>&1 || true
  if [ -n "$shared_postgres_container" ]; then
    docker rm --force --volumes "$shared_postgres_container" >/dev/null 2>&1 || true
    shared_postgres_container=""
  fi
  # The tokens are secrets for the lifetime of this run and must not survive it.
  rm -f "$repo_root/$publish_dir/access-token" "$repo_root/$publish_dir/access-token-evidence" \
    "$repo_root/$publish_dir/access-token-credentials" "$repo_root/$publish_dir/access-token-voice" \
    "$repo_root/$publish_dir/access-token-generation" "$repo_root/$publish_dir/access-token-github" \
    "$repo_root/$publish_dir/access-token-capture" "$repo_root/$publish_dir/access-token-drafts" \
    "$repo_root/$publish_dir/access-token-curation" "$repo_root/$publish_dir/access-token-timeline" \
    "$repo_root/$publish_dir/access-token-review" "$repo_root/$publish_dir/access-token-candidates" \
    "$repo_root/$publish_dir/access-token-narrative" "$repo_root/$publish_dir/access-token-variants" \
    "$repo_root/$publish_dir/access-token-team" \
    "$repo_root/$publish_dir/access-token-tenancy" "$repo_root/$publish_dir/access-token-evolution" \
    "$repo_root/$publish_dir/access-token-gif" "$repo_root/$publish_dir/access-token-publication" \
    "$repo_root/$publish_dir/access-token-journey" "$repo_root/$publish_dir/access-token-area" \
    "$repo_root/$publish_dir/access-token-publish-variant" \
    "$repo_root/$publish_dir/access-token-static-blog-export" \
    "$repo_root/$publish_dir/access-token-adapter-panels" \
    "$repo_root/$publish_dir/access-token-content-pack" \
    "$repo_root/$publish_dir/access-token-agency" \
    "$repo_root/$publish_dir/access-token-video-story" \
    "$repo_root/$publish_dir/access-token-wayfinding" \
    "$repo_root/$publish_dir/access-token-a11y-roles" \
    "$repo_root/$publish_dir/access-token-responsive"
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

# Stops the server of whichever suite is running in this subshell. The shared container
# is removed only by cleanup at the end of the run, never per suite.
stop_suite() {
  if [ -n "$server_pid" ] && kill -0 "$server_pid" 2>/dev/null; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  server_pid=""
}

# PG-TIGHTEN-FU-001. Refuses to let a suite start when its origin port is already bound
# by a `dotnet` process - the orphan class that survives an interrupted prior run and
# masquerades as a ready server in start_suite's /api/health loop. Walks ss -tnlp for
# the port, prints the holder PID + command line when one exists, and exits non-zero so
# the gate fails loudly BEFORE any disposable database is cloned or any new server is
# launched. The readiness loop in start_suite is unchanged in shape: it runs only after
# this assertion has confirmed the port is free, so an orphan can no longer answer
# /api/health on behalf of a suite the script never started.
assert_suite_ports_free() {
  local origin port pid listener_cmd
  for origin in "$@"; do
    port="$(printf '%s' "$origin" | sed -n 's|^http://127.0.0.1:\([0-9][0-9]*\).*|\1|p')"
    if [ -z "$port" ]; then
      continue
    fi
    # ss -tnlp prints one row per accepted socket; the `users:` field carries
    # `("dotnet",pid=<N>,fd=<M>)` for dotnet listeners. Strip everything but the pid.
    pid="$(ss -tnlp "sport = :$port" 2>/dev/null \
      | sed -n 's|.*pid=\([0-9][0-9]*\).*|\1|p' \
      | head -n 1)"
    if [ -n "$pid" ] && [ -d "/proc/$pid" ]; then
      listener_cmd="$(tr '\0' ' ' </proc/"$pid"/cmdline 2>/dev/null || echo '<unknown>')"
      echo "port $port (origin $origin) is held by an orphan listener (pid=$pid cmd='$listener_cmd') - refusing to start a suite on a port another process owns" >&2
      return 1
    fi
  done
}

# start_suite <suffix> <origin> <log> <token file> [extra server env]
#
# The optional fifth argument is space-separated KEY=value pairs (values carry no spaces)
# appended to the server's environment. It exists for the suite whose installation needs a
# transport registered and a dispatch loop silenced; every other suite passes nothing and the
# server starts exactly as before.
#
# PG-TIGHTEN-FU-001. assert_suite_ports_free runs at the top of start_suite so a held port
# fails the gate before clone_suite_database opens any disposable database.
#
# start_shared_postgres
#
# Boots the ONE postgres container for the whole run and migrates the seed template
# database exactly once. Every suite clones that template (clone_suite_database),
# so container boot and migration replay happen once per run, not once per suite.
start_shared_postgres() {
  local container="chronicleaf-e2e-postgres-shared-$$"

  # Published for the seeds that write rows directly, under the same global name the
  # seeds have always used: there is only one container now, so the name never changes.
  shared_postgres_container="$container"
  postgres_container="$container"

  docker run --detach --rm \
    --name "$container" \
    --env POSTGRES_DB="$seed_database" \
    --env POSTGRES_USER=chronicleaf \
    --env POSTGRES_PASSWORD=chronicleaf-e2e \
    --publish 127.0.0.1::5432 \
    "$postgres_image" >/dev/null

  shared_postgres_port="$(docker port "$container" 5432/tcp | head -n 1 | sed 's/.*://')"
  if [ -z "$shared_postgres_port" ]; then
    echo "the shared PostgreSQL did not publish a port:" >&2
    docker logs "$container" >&2 || true
    return 1
  fi

  for _ in $(seq 1 60); do
    if docker exec "$container" pg_isready --username=chronicleaf --dbname="$seed_database" >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done

  (
    cd "$repo_root"
    ConnectionStrings__Chronicleaf="Host=127.0.0.1;Port=${shared_postgres_port};Database=${seed_database};Username=chronicleaf;Password=chronicleaf-e2e;Timeout=5;Command Timeout=10" \
    Chronicleaf__AccessTokenHash="$(head -c 64 /dev/zero | tr '\0' '0' | sha256sum | cut -d' ' -f1)" \
      dotnet ef database update \
        --project src/Chronicleaf.Infrastructure \
        --startup-project src/Chronicleaf.Api \
        --no-build >/dev/null
  )
}

# clone_suite_database <suffix>
#
# Clones the migrated seed template into the suite's own database and provisions the
# singleton installation there — the same database the old per-suite migrate + INSERT
# produced, without replaying migrations. Per-suite database names are unchanged.
clone_suite_database() {
  local suffix="$1"
  local database_name="chronicleaf_e2e_${suffix}"

  # CREATE DATABASE ... TEMPLATE refuses to run against a template with open
  # connections, and the seed migration above has exited, so no connection is open.
  docker exec "$shared_postgres_container" psql --username=chronicleaf --dbname=postgres --quiet --set=ON_ERROR_STOP=1 \
    --command "CREATE DATABASE \"${database_name}\" TEMPLATE \"${seed_database}\";" >/dev/null

  # The singleton installation is operator-provisioned state, not something a visitor may claim.
  local installation_id
  installation_id="$(cat /proc/sys/kernel/random/uuid)"
  docker exec "$shared_postgres_container" psql --username=chronicleaf --dbname="$database_name" --quiet --set=ON_ERROR_STOP=1 \
    --command "INSERT INTO installation (id, schema_version, created_at)
               VALUES ('${installation_id}', 1, now())
               ON CONFLICT (id) DO NOTHING;" >/dev/null
}

# start_suite <suffix> <origin> <log> <token file> [extra server env]
#
# The optional fifth argument is space-separated KEY=value pairs (values carry no spaces)
# appended to the server's environment. It exists for the suite whose installation needs a
# transport registered and a dispatch loop silenced; every other suite passes nothing and the
# server starts exactly as before.
#
# Clones the suite's database from the shared seed template, provisions the single
# installation, and starts the published application against it with a real access token hash and
# the origin the browser will actually use. The plaintext token is written to the token file,
# never echoed. server_pid is set in the caller's (suite-subshell) scope so stop_suite and
# the RETURN trap stop exactly this suite's server.
start_suite() {
  local suffix="$1"
  local origin="$2"
  local log="$3"
  local token_path="$4"
  local extra_env="${5:-}"
  local database_name="chronicleaf_e2e_${suffix}"

  # PG-TIGHTEN-FU-001: refuse to start on a port another process holds, so an orphan
  # dotnet Chronicleaf.Api.dll from a prior interrupted run cannot masquerade as a
  # ready server in the readiness loop below.
  assert_suite_ports_free "$origin"

  clone_suite_database "$suffix"

  # Published for the seeds that address the suite database by global name (the github
  # candidate seed and its row counters): suite-subshell scoped, so parallel suites
  # never share it.
  postgres_database="$database_name"

  local connection_string="Host=127.0.0.1;Port=${shared_postgres_port};Database=${database_name};Username=chronicleaf;Password=chronicleaf-e2e;Timeout=5;Command Timeout=10"

  # A token for this run only: 32 random bytes, hashed for the server and kept for the fixtures.
  local access_token access_token_hash
  access_token="$(openssl rand -hex 32)"
  access_token_hash="$(printf '%s' "$access_token" | sha256sum | cut -d' ' -f1)"
  printf '%s' "$access_token" >"$token_path"

  # exec replaces the subshell, so $! is the server process itself and the trap below
  # actually stops it instead of orphaning it on the port.
  #
  # shellcheck disable=SC2086: extra_env is space-separated KEY=value pairs by contract, and an
  # empty expansion vanishes rather than arriving as an empty argument.
  (
    cd "$repo_root/$publish_dir" && exec env \
      ASPNETCORE_URLS="$origin" \
      ASPNETCORE_ENVIRONMENT=Production \
      ConnectionStrings__Chronicleaf="$connection_string" \
      Chronicleaf__AccessTokenHash="$access_token_hash" \
      Chronicleaf__PublicOrigin="$origin" \
      Chronicleaf__EncryptionKeyFile="$key_file" \
      ${extra_env} \
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

# The single shared PostgreSQL and its migrated seed template: every suite below clones it.
echo "==> starting the one shared PostgreSQL and migrating the seed template"
start_shared_postgres

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
# The review fixture: one real project, two real milestones, one real evidence bundle each, and
# one stored draft each for the review panel to edit, approve and export.
#
# Everything above the content rows is made through the running server's own routes with the real
# access token and a real antiforgery value, so the project, the milestones and their evidence
# are records the product itself created. The two content rows are inserted the way the
# grounded-draft suite inserts its rows - directly, with the rows read back and refused when they
# are not there - because a draft is produced by a provider and this host calls none.
#
# What the inserted rows are careful about:
#   - the summary cites the real evidence item the seed created, so the revision carries no
#     grounding finding and the browser can actually approve it. A fixture with a finding in it
#     could not tell a blocked approval from a broken one.
#   - the voice snapshot declares English and the body is English, so the revision carries no
#     voice finding either, for the same reason. The fingerprint is the delivered hasher's
#     canonical form over the English values, and the read route recomputes it rather than
#     trusting the stored one.
#   - the body contains a raw HTML tag, an unsafe link and a safe link on purpose. The
#     requirement is that a preview never executes markup, and a fixture without any could not
#     tell a sanitizing renderer from one that happens to have nothing to refuse.
#   - two milestones, because the first browser test edits, approves and re-edits its milestone
#     and a milestone cannot be un-edited through the UI. The second test reads its own row
#     rather than state the first test left behind.
# ---------------------------------------------------------------------------------------------
seed_review() {
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
    echo "the review seed could not read an antiforgery value from ${origin}" >&2
    return 1
  fi

  local project_id
  project_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"name":"Fieldnotes","description":"Reviewed releases."}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects" | jq -r '.id')"

  if [ -z "$project_id" ] || [ "$project_id" = "null" ]; then
    echo "the review seed could not create its project" >&2
    return 1
  fi

  # The seeded bodies are English, so the voice an edit is judged under must be too: an edit
  # re-snapshots the project's current voice, and the default profile declares Chinese, which
  # would report language findings on every field and leave the browser with nothing it can
  # approve. A fixture with a finding in it could not tell a blocked approval from a broken one.
  # The stored language is read back, because a PUT that did not take would leave the same
  # un-approvable fixture behind.
  local voice_language
  voice_language="$(curl --silent --show-error --request PUT --cookie "$cookie_jar" \
    --data '{"presetId":"technical-blog","overrides":{"language":"en"}}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/projects/${project_id}/voice-profile" | jq -r '.effectiveValues.language')"

  if [ "$voice_language" != "en" ]; then
    echo "the review seed could not set the project's voice" >&2
    return 1
  fi

  seed_review_milestone "$origin" "$cookie_jar" "$antiforgery" "$project_id" "Reviewed release" || return 1
  seed_review_milestone "$origin" "$cookie_jar" "$antiforgery" "$project_id" "Second review" || return 1
}

seed_review_milestone() {
  local origin="$1"
  local cookie_jar="$2"
  local antiforgery="$3"
  local project_id="$4"
  local title="$5"

  # A manual milestone is confirmed by the act of saving it, so no confirm call is needed here.
  local milestone_id
  milestone_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data "{\"title\":\"${title}\",\"version\":\"0.1.0\",\"occurredAt\":\"2026-10-01T09:00:00Z\",\"type\":\"feature\",\"notes\":\"Recorded by the e2e seed.\",\"knownLimitations\":[],\"nextSteps\":[]}" \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects/${project_id}/milestones" | jq -r '.id')"

  if [ -z "$milestone_id" ] || [ "$milestone_id" = "null" ]; then
    echo "the review seed could not create its milestone" >&2
    return 1
  fi

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
    echo "the review seed could not read the evidence bundle the product created" >&2
    return 1
  fi

  seed_review_rows "$postgres_container" "chronicleaf_e2e_review" \
    "$project_id" "$milestone_id" "$bundle_id" "$evidence_item_id"
}

# The two content rows for one review milestone, inserted into the disposable database the review
# suite is using.

# ---------------------------------------------------------------------------------------------
# Suite 14 seed: one project, one confirmed milestone, one evidence bundle and one approved
# Markdown story. The story and its approval are inserted the way the review suite inserts its
# rows - directly, with the rows read back and refused when they are not there - because a story
# is produced by a provider and this host calls none.
#
# What the seed claims, and what it does not. The approval row binds the head revision, the body
# hash and the evidence head, exactly as a press would record them; the voice snapshot declares
# the English the body is written in, so neither finding list has anything to report and the
# browser can actually derive and approve. So a pass is evidence that the panel derives a format
# from an approved story, previews it as inert characters, approves it on its own revision, and
# names the readme-update proposal as one - and is NOT evidence that a draft was generated on
# this host. The .NET suite is where a real provider answer flows through generation into a
# variant's source.
# ---------------------------------------------------------------------------------------------
seed_variants() {
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
    echo "the variants seed could not read an antiforgery value from ${origin}" >&2
    return 1
  fi

  local project_id
  project_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"name":"Formats","description":"Reusable content formats."}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects" | jq -r '.id')"

  if [ -z "$project_id" ] || [ "$project_id" = "null" ]; then
    echo "the variants seed could not create its project" >&2
    return 1
  fi

  # The seeded body is English, so the voice a derivation is judged under must be too: the
  # default profile declares Chinese and would report language findings on every field, which
  # would leave the browser with nothing it can approve. The stored language is read back,
  # because a PUT that did not take would leave the same un-approvable fixture behind.
  local voice_language
  voice_language="$(curl --silent --show-error --request PUT --cookie "$cookie_jar" \
    --data '{"presetId":"technical-blog","overrides":{"language":"en"}}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/projects/${project_id}/voice-profile" | jq -r '.effectiveValues.language')"

  if [ "$voice_language" != "en" ]; then
    echo "the variants seed could not set the project's voice" >&2
    return 1
  fi

  local milestone_id
  milestone_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"title":"Variant source story","version":"0.1.0","occurredAt":"2026-10-01T09:00:00Z","type":"feature","notes":"Recorded by the e2e seed.","knownLimitations":[],"nextSteps":[]}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects/${project_id}/milestones" | jq -r '.id')"

  if [ -z "$milestone_id" ] || [ "$milestone_id" = "null" ]; then
    echo "the variants seed could not create its milestone" >&2
    return 1
  fi

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
    echo "the variants seed could not read the evidence bundle the product created" >&2
    return 1
  fi

  seed_variant_rows "$postgres_container" "chronicleaf_e2e_variants" \
    "$project_id" "$milestone_id" "$bundle_id" "$evidence_item_id"
}

seed_variant_rows() {
  local container="$1"
  local database_name="$2"
  local project_id="$3"
  local milestone_id="$4"
  local bundle_id="$5"
  local evidence_item_id="$6"

  local body summary voice_snapshot body_hash voice_hash media_hash fingerprint now
  now="$(date -u +%Y-%m-%dT%H:%M:%S.000000Z)"

  # The body, digit-free on purpose: the numeral rule refuses a figure that appears in no
  # evidence item, and a fixture that violated it would be testing the wrong thing - the
  # variant derivation re-checks the same rule before it stores anything.
  body=$'# Variant source story\n\nThe pipeline now reproduces from a clean checkout.[^c1]\n\n## What changed\n\n- The pipeline runs from a clean checkout.[^c1]\n\n## Problems solved\n\n- A release could not be rebuilt from its source.[^c2]\n\n## Known limitations\n\nOnly Linux is covered.[^c3]\n\n## Next steps\n\nCover the other platform.[^c4]\n\n[^c1]: The pipeline runs from a clean checkout.\n[^c2]: A release could not be rebuilt from its source.\n[^c3]: Only Linux is covered.\n[^c4]: Cover the other platform.\n'

  body_hash="$(printf '%s' "$body" | sha256sum | cut -d' ' -f1)"

  # The delivered hasher's canonical form over the English values this body is judged under: the
  # resolved values joined by U+001F, in the order VoiceSnapshotHasher writes them. The read
  # route recomputes the fingerprint rather than trusting the stored one - so a default that ever
  # changes makes this suite fail rather than make the panel show a voice it did not check.
  fingerprint="$(printf 'en\037restrained\037true\03760\037high\037low\037true\037革命性\037震撼\037game-changing\037exciting news' \
    | sha256sum | cut -d' ' -f1)"

  voice_snapshot="{\"presetId\":\"technical-blog\",\"profileRevision\":0,\"capturedAt\":\"${now}\",\"language\":\"en\",\"tone\":\"restrained\",\"emoji\":true,\"titleMaxChars\":60,\"technicalDetail\":\"high\",\"marketing\":\"low\",\"firstPerson\":true,\"forbiddenTerms\":[\"革命性\",\"震撼\",\"game-changing\",\"exciting news\"],\"fingerprint\":\"${fingerprint}\"}"

  voice_hash="$(printf '%s' "$voice_snapshot" | sha256sum | cut -d' ' -f1)"

  # No picture is selected on this bundle, so the approval binds the empty selection: the hash
  # of the empty string, which is a defined value rather than an absence.
  media_hash="$(printf '' | sha256sum | cut -d' ' -f1)"

  summary=$(jq -cn --arg e "$evidence_item_id" '{
    milestoneTitle: "Variant source story",
    summary: "The pipeline now reproduces from a clean checkout.",
    changes: [
      { text: "The pipeline runs from a clean checkout.", evidenceIds: [$e] }
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

  local asset_id revision_id approval_id scope_id
  asset_id="$(cat /proc/sys/kernel/random/uuid)"
  revision_id="$(cat /proc/sys/kernel/random/uuid)"
  approval_id="$(cat /proc/sys/kernel/random/uuid)"
  scope_id="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" --quiet --tuples-only --no-align \
    --command 'SELECT id FROM installation LIMIT 1')"

  # One statement per table, in dependency order: the story, its revision, then the live approval
  # binding the head revision, the body hash and the evidence head - exactly what a press records.
  # The triggers on content_revisions refuse UPDATE and DELETE, which is the append-only contract,
  # and an INSERT is what a completed generation would do.
  #
  # `-i` is load-bearing: `docker exec` only forwards this heredoc to psql's stdin when it is asked to,
  # and without it psql reads an empty script and exits 0 having inserted nothing. The rows are read
  # back below and the seed refuses to continue if they are not there.
  #
  # The long values travel as psql variables and are interpolated with `:'name'`, which is psql's
  # own quoting. A body interpolated into the SQL text by the shell would have to be escaped by the
  # shell, and the two languages disagree about what a quote is.
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

INSERT INTO content_approvals
  (id, scope_id, asset_id, content_revision, actor_id, payload_kind, body_hash,
   evidence_revision, voice_snapshot_hash, media_selection_hash, artifact_hash,
   approved_at, invalidated_at, reason)
VALUES
  ('${approval_id}', '${scope_id}', '${asset_id}', 1, NULL, 'text', '${body_hash}',
   1, '${voice_hash}', '${media_hash}', NULL, '${now}', NULL, NULL);
SQL

  # Read back what the database actually holds, and compare the stored digest with the one computed
  # above. psql exiting zero is not evidence that a statement ran: the approval binds that hash,
  # and a stored body whose hash is not its own would be a fixture proving nothing.
  local stored
  stored="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" \
    --quiet --tuples-only --no-align \
    --command "SELECT count(*) || ' ' || coalesce(max(body_hash), '') FROM content_revisions WHERE asset_id = '${asset_id}'")"

  if [ "$stored" != "1 ${body_hash}" ]; then
    echo "the variants seed stored '${stored}', which is not the one revision hashed as ${body_hash}" >&2
    return 1
  fi

  local live
  live="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" \
    --quiet --tuples-only --no-align \
    --command "SELECT count(*) FROM content_approvals WHERE asset_id = '${asset_id}' AND invalidated_at IS NULL")"

  if [ "$live" != "1" ]; then
    echo "the variants seed stored '${live}' live approvals, which is not the one the panel derives from" >&2
    return 1
  fi
}

# ---------------------------------------------------------------------------------------------
# Suite 22 seed: one project, one confirmed milestone, one evidence bundle, one approved
# Markdown story, and one sealed `publisher` credential. The story and its approval are
# inserted the way the variants suite inserts its rows - directly, with the rows read back
# and refused when they are not there - because a story is produced by a provider and this
# host calls none. The credential is stored through the running server's own vault route
# with a fake test value (never a real secret) and read back through the describe route,
# because the publish route refuses before a job exists when no credential is configured.
#
# What the seed claims, and what it does not. The browser derives a variant from the
# approved story through the shipped panel, approves it, and publishes it through the
# shared confirm flow: a pass is evidence that pick variant → pick target → preview →
# confirm reaches the delivered routes and the receipt names the server's answer. The
# dispatch loop is silenced for this installation (see the suite block), so the receipt
# stays at the acceptance the request committed; the run half - dispatch, remote write,
# terminal receipt - is the .NET suite's claim, not this one's, and no byte leaves this
# host for a remote side here.
# ---------------------------------------------------------------------------------------------
seed_publish_variant() {
  local origin="$1"
  local token="$2"
  # The suite's own database: suite 22 leaves the default, suite 24 names its own.
  local database_name="${3:-chronicleaf_e2e_publish_variant}"
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
    echo "the publish-variant seed could not read an antiforgery value from ${origin}" >&2
    return 1
  fi

  local project_id
  project_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"name":"Dispatches","description":"Reviewed publishing by clicking."}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects" | jq -r '.id')"

  if [ -z "$project_id" ] || [ "$project_id" = "null" ]; then
    echo "the publish-variant seed could not create its project" >&2
    return 1
  fi

  # The seeded body is English, so the voice a derivation and an approval are judged under
  # must be too: the default profile declares Chinese and would report language findings on
  # every field, which would leave the browser with nothing it can approve. The stored
  # language is read back, because a PUT that did not take would leave the same
  # un-approvable fixture behind.
  local voice_language
  voice_language="$(curl --silent --show-error --request PUT --cookie "$cookie_jar" \
    --data '{"presetId":"technical-blog","overrides":{"language":"en"}}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/projects/${project_id}/voice-profile" | jq -r '.effectiveValues.language')"

  if [ "$voice_language" != "en" ]; then
    echo "the publish-variant seed could not set the project's voice" >&2
    return 1
  fi

  local milestone_id
  milestone_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"title":"Dispatch source story","version":"0.1.0","occurredAt":"2026-10-01T09:00:00Z","type":"feature","notes":"Recorded by the e2e seed.","knownLimitations":[],"nextSteps":[]}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects/${project_id}/milestones" | jq -r '.id')"

  if [ -z "$milestone_id" ] || [ "$milestone_id" = "null" ]; then
    echo "the publish-variant seed could not create its milestone" >&2
    return 1
  fi

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
    echo "the publish-variant seed could not read the evidence bundle the product created" >&2
    return 1
  fi

  # The credential the contract leases, stored through the delivered vault route with a fake
  # test value. The 201 is asserted and the configured flag read back: a publish without it
  # is refused before a job exists, which would test the wrong thing here.
  local saved
  saved="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
    --request PUT --cookie "$cookie_jar" \
    --data '{"value":"e2e-publisher-key-never-a-secret"}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/settings/credentials/publisher")"

  if [ "$saved" != "201" ]; then
    echo "the publish-variant seed stored its credential with status ${saved}, not 201" >&2
    return 1
  fi

  local configured
  configured="$(curl --silent --show-error --cookie "$cookie_jar" \
    -H 'Accept: application/json' "${origin}/api/settings/credentials/publisher" \
    | jq -r '.configured')"

  if [ "$configured" != "true" ]; then
    echo "the publish-variant seed reads its credential as unconfigured" >&2
    return 1
  fi

  seed_publish_variant_rows "$postgres_container" "$database_name" \
    "$project_id" "$milestone_id" "$bundle_id" "$evidence_item_id"
}

seed_publish_variant_rows() {
  local container="$1"
  local database_name="$2"
  local project_id="$3"
  local milestone_id="$4"
  local bundle_id="$5"
  local evidence_item_id="$6"

  local body summary voice_snapshot body_hash voice_hash media_hash fingerprint now
  now="$(date -u +%Y-%m-%dT%H:%M:%S.000000Z)"

  # The body, digit-free on purpose: the numeral rule refuses a figure that appears in no
  # evidence item, and a fixture that violated it would be testing the wrong thing - the
  # variant derivation re-checks the same rule before it stores anything.
  body=$'# Dispatch source story\n\nThe pipeline now reproduces from a clean checkout.[^c1]\n\n## What changed\n\n- The pipeline runs from a clean checkout.[^c1]\n\n## Problems solved\n\n- A release could not be rebuilt from its source.[^c2]\n\n## Known limitations\n\nOnly Linux is covered.[^c3]\n\n## Next steps\n\nCover the other platform.[^c4]\n\n[^c1]: The pipeline runs from a clean checkout.\n[^c2]: A release could not be rebuilt from its source.\n[^c3]: Only Linux is covered.\n[^c4]: Cover the other platform.\n'

  body_hash="$(printf '%s' "$body" | sha256sum | cut -d' ' -f1)"

  # The delivered hasher's canonical form over the English values this body is judged under: the
  # resolved values joined by U+001F, in the order VoiceSnapshotHasher writes them. The read
  # route recomputes the fingerprint rather than trusting the stored one - so a default that ever
  # changes makes this suite fail rather than make the panel show a voice it did not check.
  fingerprint="$(printf 'en\037restrained\037true\03760\037high\037low\037true\037革命性\037震撼\037game-changing\037exciting news' \
    | sha256sum | cut -d' ' -f1)"

  voice_snapshot="{\"presetId\":\"technical-blog\",\"profileRevision\":0,\"capturedAt\":\"${now}\",\"language\":\"en\",\"tone\":\"restrained\",\"emoji\":true,\"titleMaxChars\":60,\"technicalDetail\":\"high\",\"marketing\":\"low\",\"firstPerson\":true,\"forbiddenTerms\":[\"革命性\",\"震撼\",\"game-changing\",\"exciting news\"],\"fingerprint\":\"${fingerprint}\"}"

  voice_hash="$(printf '%s' "$voice_snapshot" | sha256sum | cut -d' ' -f1)"

  # No picture is selected on this bundle, so the approval binds the empty selection: the hash
  # of the empty string, which is a defined value rather than an absence.
  media_hash="$(printf '' | sha256sum | cut -d' ' -f1)"

  summary=$(jq -cn --arg e "$evidence_item_id" '{
    milestoneTitle: "Dispatch source story",
    summary: "The pipeline now reproduces from a clean checkout.",
    changes: [
      { text: "The pipeline runs from a clean checkout.", evidenceIds: [$e] }
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

  local asset_id revision_id approval_id scope_id
  asset_id="$(cat /proc/sys/kernel/random/uuid)"
  revision_id="$(cat /proc/sys/kernel/random/uuid)"
  approval_id="$(cat /proc/sys/kernel/random/uuid)"
  scope_id="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" --quiet --tuples-only --no-align \
    --command 'SELECT id FROM installation LIMIT 1')"

  # One statement per table, in dependency order: the story, its revision, then the live approval
  # binding the head revision, the body hash and the evidence head - exactly what a press records.
  # The triggers on content_revisions refuse UPDATE and DELETE, which is the append-only contract,
  # and an INSERT is what a completed generation would do.
  #
  # `-i` is load-bearing: `docker exec` only forwards this heredoc to psql's stdin when it is asked to,
  # and without it psql reads an empty script and exits 0 having inserted nothing. The rows are read
  # back below and the seed refuses to continue if they are not there.
  #
  # The long values travel as psql variables and are interpolated with `:'name'`, which is psql's
  # own quoting. A body interpolated into the SQL text by the shell would have to be escaped by the
  # shell, and the two languages disagree about what a quote is.
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

INSERT INTO content_approvals
  (id, scope_id, asset_id, content_revision, actor_id, payload_kind, body_hash,
   evidence_revision, voice_snapshot_hash, media_selection_hash, artifact_hash,
   approved_at, invalidated_at, reason)
VALUES
  ('${approval_id}', '${scope_id}', '${asset_id}', 1, NULL, 'text', '${body_hash}',
   1, '${voice_hash}', '${media_hash}', NULL, '${now}', NULL, NULL);
SQL

  # Read back what the database actually holds, and compare the stored digest with the one computed
  # above. psql exiting zero is not evidence that a statement ran: the approval binds that hash,
  # and a stored body whose hash is not its own would be a fixture proving nothing.
  local stored
  stored="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" \
    --quiet --tuples-only --no-align \
    --command "SELECT count(*) || ' ' || coalesce(max(body_hash), '') FROM content_revisions WHERE asset_id = '${asset_id}'")"

  if [ "$stored" != "1 ${body_hash}" ]; then
    echo "the publish-variant seed stored '${stored}', which is not the one revision hashed as ${body_hash}" >&2
    return 1
  fi

  local live
  live="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" \
    --quiet --tuples-only --no-align \
    --command "SELECT count(*) FROM content_approvals WHERE asset_id = '${asset_id}' AND invalidated_at IS NULL")"

  if [ "$live" != "1" ]; then
    echo "the publish-variant seed stored '${live}' live approvals, which is not the one the panel derives from" >&2
    return 1
  fi
}

# ---------------------------------------------------------------------------------------------
# Suite 23 seed: one project, two confirmed milestones, one evidence bundle each, one
# approved story on the first and one never-approved story on the second.
#
# Everything above the content tables is made through the running server's own routes with
# the real access token and a real antiforgery value, so the project, the milestones, their
# confirmation and their evidence are records the product itself created. Only the content
# rows are inserted directly, and that is the same technique seed_operations uses and for
# the same reason: a story is produced by a provider call, and this installation has an
# adapter with an empty model allowlist, so there is nothing that could produce one
# honestly here. No credential is stored: the export contract leases none.
#
# What the inserted rows are careful about (same contract as seed_publish_variant_rows):
#   - body_markdown is hashed with sha256sum and the digest stored, because the database's
#     check constraint refuses a row whose hash is not a hash.
#   - voice_snapshot is the delivered canonical form of the English technical-blog voice,
#     with the fingerprint the delivered hasher computes over values joined by U+001F.
#   - The approved story's approval binds the head revision, the body hash, the evidence
#     head and the empty media selection hash; the second story carries no approval row at
#     all, so the review read answers `draft` and the export refuses `approval_required`.
#   - Bodies are digit-free on purpose: the numeral rule refuses a figure that appears in
#     no evidence item, and a fixture that violated it would be testing the wrong thing.
#
# `-i` is load-bearing on the docker exec heredocs below (see seed_publish_variant_rows),
# and the rows are read back with the seed refusing to continue otherwise.
# ---------------------------------------------------------------------------------------------
seed_static_blog_export() {
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
    echo "the static-blog-export seed could not read an antiforgery value from ${origin}" >&2
    return 1
  fi

  local project_id
  project_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"name":"StaticBlogSite","description":"Approved static export by clicking."}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects" | jq -r '.id')"

  if [ -z "$project_id" ] || [ "$project_id" = "null" ]; then
    echo "the static-blog-export seed could not create its project" >&2
    return 1
  fi

  # The seeded bodies are English, so the voice an approval is judged under must be too.
  local voice_language
  voice_language="$(curl --silent --show-error --request PUT --cookie "$cookie_jar" \
    --data '{"presetId":"technical-blog","overrides":{"language":"en"}}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/projects/${project_id}/voice-profile" | jq -r '.effectiveValues.language')"

  if [ "$voice_language" != "en" ]; then
    echo "the static-blog-export seed could not set the project's voice" >&2
    return 1
  fi

  local first second bare
  first="$(seed_static_blog_export_milestone "$origin" "$cookie_jar" "$antiforgery" "$project_id" \
    "Export source story" "The site now builds from approved stories.")"
  second="$(seed_static_blog_export_milestone "$origin" "$cookie_jar" "$antiforgery" "$project_id" \
    "Unexported draft story" "A story nobody approved stays out of every archive.")"
  bare="$(seed_static_blog_export_milestone "$origin" "$cookie_jar" "$antiforgery" "$project_id" \
    "Bare milestone" "A milestone with no draft states its empty case honestly.")"

  # shellcheck disable=SC2086: the helper prints exactly four space-separated ids.
  seed_static_blog_export_rows "$postgres_container" "chronicleaf_e2e_static_blog_export" \
    $first "Export source story" yes
  # shellcheck disable=SC2086: the helper prints exactly four space-separated ids.
  seed_static_blog_export_rows "$postgres_container" "chronicleaf_e2e_static_blog_export" \
    $second "Unexported draft story" no
  # The bare milestone keeps no content rows at all: no draft, no variant, nothing to select.
}

seed_static_blog_export_milestone() {
  local origin="$1"
  local cookie_jar="$2"
  local antiforgery="$3"
  local project_id="$4"
  local title="$5"
  local note="$6"

  local milestone_id
  milestone_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data "{\"title\":\"${title}\",\"version\":\"0.1.0\",\"occurredAt\":\"2026-10-01T09:00:00Z\",\"type\":\"feature\",\"notes\":\"Recorded by the e2e seed.\",\"knownLimitations\":[],\"nextSteps\":[]}" \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects/${project_id}/milestones" | jq -r '.id')"

  if [ -z "$milestone_id" ] || [ "$milestone_id" = "null" ]; then
    echo "the static-blog-export seed could not create its milestone '${title}'" >&2
    return 1
  fi

  curl --silent --show-error --cookie "$cookie_jar" \
    --data "{\"notes\":\"${note}\",\"expectedRevision\":0}" \
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
    echo "the static-blog-export seed could not read the evidence bundle the product created" >&2
    return 1
  fi

  printf '%s %s %s %s\n' "$project_id" "$milestone_id" "$bundle_id" "$evidence_item_id"
}

seed_static_blog_export_rows() {
  local container="$1"
  local database_name="$2"
  local project_id="$3"
  local milestone_id="$4"
  local bundle_id="$5"
  local evidence_item_id="$6"
  local title="$7"
  local approve="$8"
  # The evidence head the stored revision and its approval bind. Every older
  # caller stores against the first revision; a caller whose milestone moved
  # (a curation select appends a revision) names the head instead.
  local evidence_revision="${9:-1}"

  local body summary voice_snapshot body_hash voice_hash media_hash fingerprint now
  now="$(date -u +%Y-%m-%dT%H:%M:%S.000000Z)"

  body=$(printf '# %s\n\nThe site now builds from approved stories.[^c1]\n\n## What changed\n\n- The site builds from approved stories.[^c1]\n\n## Problems solved\n\n- An approved story had no portable page.[^c2]\n\n## Known limitations\n\nOnly Linux is covered.[^c3]\n\n## Next steps\n\nCover the other platform.[^c4]\n\n[^c1]: The site now builds from approved stories.\n[^c2]: An approved story had no portable page.\n[^c3]: Only Linux is covered.\n[^c4]: Cover the other platform.\n' "$title")

  body_hash="$(printf '%s' "$body" | sha256sum | cut -d' ' -f1)"

  fingerprint="$(printf 'en\037restrained\037true\03760\037high\037low\037true\037革命性\037震撼\037game-changing\037exciting news' \
    | sha256sum | cut -d' ' -f1)"

  voice_snapshot="{\"presetId\":\"technical-blog\",\"profileRevision\":0,\"capturedAt\":\"${now}\",\"language\":\"en\",\"tone\":\"restrained\",\"emoji\":true,\"titleMaxChars\":60,\"technicalDetail\":\"high\",\"marketing\":\"low\",\"firstPerson\":true,\"forbiddenTerms\":[\"革命性\",\"震撼\",\"game-changing\",\"exciting news\"],\"fingerprint\":\"${fingerprint}\"}"

  voice_hash="$(printf '%s' "$voice_snapshot" | sha256sum | cut -d' ' -f1)"

  # The media selection the stored approval binds. Every older caller stores against a head
  # with no selected picture, which is the empty hash; a caller whose milestone selected
  # pictures first names the delivered `mediaId|caption` hash instead, or the approval
  # reads back invalidated and the panel under test has nothing it can derive from.
  if [ -n "${10:-}" ]; then
    media_hash="${10}"
  else
    media_hash="$(printf '' | sha256sum | cut -d' ' -f1)"
  fi

  summary=$(jq -cn --arg e "$evidence_item_id" --arg t "$title" '{
    milestoneTitle: $t,
    summary: "The site now builds from approved stories.",
    changes: [
      { text: "The site builds from approved stories.", evidenceIds: [$e] }
    ],
    problemsSolved: [
      { text: "An approved story had no portable page.", evidenceIds: [$e] }
    ],
    knownLimitations: [
      { text: "Only Linux is covered.", evidenceIds: [$e] }
    ],
    nextSteps: [
      { text: "Cover the other platform.", evidenceIds: [$e] }
    ]
  }')

  local asset_id revision_id approval_id scope_id
  asset_id="$(cat /proc/sys/kernel/random/uuid)"
  revision_id="$(cat /proc/sys/kernel/random/uuid)"
  approval_id="$(cat /proc/sys/kernel/random/uuid)"
  scope_id="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" --quiet --tuples-only --no-align \
    --command 'SELECT id FROM installation LIMIT 1')"

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
  ('${revision_id}', '${asset_id}', '${scope_id}', 1, '${bundle_id}', '${evidence_revision}', 0,
   :'voice_snapshot'::jsonb, :'summary'::jsonb, :'body', '${body_hash}',
   'openai', 'seeded-not-called', 'grounded-markdown/1', 'grounded-summary/1', '${now}');
SQL

  if [ "$approve" = "yes" ]; then
    docker exec -i "$container" psql --username=chronicleaf --dbname="$database_name" --quiet \
      --set=ON_ERROR_STOP=1 <<SQL >/dev/null
INSERT INTO content_approvals
  (id, scope_id, asset_id, content_revision, actor_id, payload_kind, body_hash,
   evidence_revision, voice_snapshot_hash, media_selection_hash, artifact_hash,
   approved_at, invalidated_at, reason)
VALUES
  ('${approval_id}', '${scope_id}', '${asset_id}', 1, NULL, 'text', '${body_hash}',
   '${evidence_revision}', '${voice_hash}', '${media_hash}', NULL, '${now}', NULL, NULL);
SQL
  fi

  local stored
  stored="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" \
    --quiet --tuples-only --no-align \
    --command "SELECT count(*) || ' ' || coalesce(max(body_hash), '') FROM content_revisions WHERE asset_id = '${asset_id}'")"

  if [ "$stored" != "1 ${body_hash}" ]; then
    echo "the static-blog-export seed stored '${stored}', which is not the one revision hashed as ${body_hash}" >&2
    return 1
  fi

  local live
  live="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" \
    --quiet --tuples-only --no-align \
    --command "SELECT count(*) FROM content_approvals WHERE asset_id = '${asset_id}' AND invalidated_at IS NULL")"

  local expected_live="0"
  if [ "$approve" = "yes" ]; then
    expected_live="1"
  fi

  if [ "$live" != "$expected_live" ]; then
    echo "the static-blog-export seed stored '${live}' live approvals for '${title}', which is not the ${expected_live} the panel derives from" >&2
    return 1
  fi
}

# ---------------------------------------------------------------------------------------------
# Suite 25 seed: one project, one confirmed milestone with an approved story and one
# bare milestone with no draft at all.
#
# Everything above the content tables is made through the running server's own routes with
# the real access token and a real antiforgery value, so the project, the milestones,
# their confirmation and the first milestone's evidence are records the product itself
# created. The approved story rows are inserted directly with the same technique and
# the same read-back refusal as seed_static_blog_export_rows — a story is produced by
# a provider call, and this installation has an adapter with an empty model allowlist,
# so there is nothing that could produce one honestly here. No credential is stored:
# no pack contract leases one. Every pack derivation and every pack approval below
# goes through the shipped panels and the published server.
# ---------------------------------------------------------------------------------------------
seed_content_pack_panels() {
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
    echo "the content-pack seed could not read an antiforgery value from ${origin}" >&2
    return 1
  fi

  local project_id
  project_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"name":"Content packs","description":"Read and export panels for pack backends."}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects" | jq -r '.id')"

  if [ -z "$project_id" ] || [ "$project_id" = "null" ]; then
    echo "the content-pack seed could not create its project" >&2
    return 1
  fi

  # The seeded body is English, so the voice a derivation and an approval are judged under
  # must be too: the default profile declares Chinese and would report language findings on
  # every field, which would leave the browser with nothing it can approve. The stored
  # language is read back, because a PUT that did not take would leave the same
  # un-approvable fixture behind.
  local voice_language
  voice_language="$(curl --silent --show-error --request PUT --cookie "$cookie_jar" \
    --data '{"presetId":"technical-blog","overrides":{"language":"en"}}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/projects/${project_id}/voice-profile" | jq -r '.effectiveValues.language')"

  if [ "$voice_language" != "en" ]; then
    echo "the content-pack seed could not set the project's voice" >&2
    return 1
  fi

  local milestone_id
  milestone_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"title":"Pack source story","version":"0.1.0","occurredAt":"2026-10-01T09:00:00Z","type":"feature","notes":"Recorded by the e2e seed.","knownLimitations":[],"nextSteps":[]}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects/${project_id}/milestones" | jq -r '.id')"

  if [ -z "$milestone_id" ] || [ "$milestone_id" = "null" ]; then
    echo "the content-pack seed could not create its milestone" >&2
    return 1
  fi

  # A manual milestone is confirmed by the act of saving it, so no confirm call is needed here.
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
    echo "the content-pack seed could not read the evidence bundle the product created" >&2
    return 1
  fi

  seed_static_blog_export_rows "$postgres_container" "chronicleaf_e2e_content_pack" \
    "$project_id" "$milestone_id" "$bundle_id" "$evidence_item_id" \
    "Pack source story" "yes"

  # The bare milestone carries no evidence and no story: the pack panels on its row
  # prove the empty states rather than a second derivation.
  local bare_id
  bare_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"title":"Bare pack milestone","version":"0.1.0","occurredAt":"2026-10-02T09:00:00Z","type":"feature","notes":"Recorded by the e2e seed.","knownLimitations":[],"nextSteps":[]}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects/${project_id}/milestones" | jq -r '.id')"

  if [ -z "$bare_id" ] || [ "$bare_id" = "null" ]; then
    echo "the content-pack seed could not create its bare milestone" >&2
    return 1
  fi
}

# ---------------------------------------------------------------------------------------------
# Suite 27 seed: one project, one confirmed milestone with an approved story and one
# selected screenshot, plus one bare milestone with no story at all.
#
# Everything above the content tables is made through the running server's own routes with
# the real access token and a real antiforgery value, so the project, the milestone, its
# manual evidence, the uploaded picture and its selection are records the product itself
# created. The approved story rows are inserted directly with the same technique and the
# same read-back refusal as seed_static_blog_export_rows — a story is produced by a
# provider call, and this installation has an adapter with an empty model allowlist, so
# there is nothing that could produce one honestly here. No credential is stored: no
# video-story contract leases one. Every script generation and every script approval
# below goes through the shipped panel and the published server.
#
# Order matters: the screenshot is selected through the delivered curation route before
# the story rows are stored, because the selection appends the second bundle revision
# and the story's approval must bind the head the script will be grounded in. All
# seeded and typed prose is digit-free English under an English voice, for the same
# reason the drafts suite's own note is: the numeral rule refuses a figure that appears
# in no evidence item.
# ---------------------------------------------------------------------------------------------
seed_video_story_panel() {
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
    echo "the video-story seed could not read an antiforgery value from ${origin}" >&2
    return 1
  fi

  local project_id
  project_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"name":"Video story","description":"Storyboard read and export panel."}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects" | jq -r '.id')"

  if [ -z "$project_id" ] || [ "$project_id" = "null" ]; then
    echo "the video-story seed could not create its project" >&2
    return 1
  fi

  # The seeded body is English, so the voice a script and an approval are judged under
  # must be too: the default profile declares Chinese and would report language findings
  # on every field, which would leave the browser with nothing it can approve. The stored
  # language is read back, because a PUT that did not take would leave the same
  # un-approvable fixture behind.
  local voice_language
  voice_language="$(curl --silent --show-error --request PUT --cookie "$cookie_jar" \
    --data '{"presetId":"technical-blog","overrides":{"language":"en"}}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/projects/${project_id}/voice-profile" | jq -r '.effectiveValues.language')"

  if [ "$voice_language" != "en" ]; then
    echo "the video-story seed could not set the project's voice" >&2
    return 1
  fi

  local milestone_id
  milestone_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"title":"Storyboard source","version":"0.1.0","occurredAt":"2026-10-01T09:00:00Z","type":"feature","notes":"Recorded by the e2e seed.","knownLimitations":[],"nextSteps":[]}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects/${project_id}/milestones" | jq -r '.id')"

  if [ -z "$milestone_id" ] || [ "$milestone_id" = "null" ]; then
    echo "the video-story seed could not create its milestone" >&2
    return 1
  fi

  # A manual milestone is confirmed by the act of saving it, so no confirm call is needed here.
  curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"notes":"The pipeline now reproduces from a clean checkout.","expectedRevision":0}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/milestones/${milestone_id}/evidence/manual" >/dev/null

  # One real capture request, so the candidate row below names a job this installation asked
  # for. The job will refuse with capture_sandbox_unavailable on a host that cannot confine
  # a browser, and it writes no candidate — which the seed asserts no part of: the candidate
  # is the row below, and the job is its provenance.
  local operation_id
  operation_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"url":"https://example.com/pipeline","viewports":["desktop"],"expectedBundleRevision":1,"idempotencyKey":"e2e-video-story-pipeline"}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 1' \
    "${origin}/api/milestones/${milestone_id}/captures" | jq -r '.operationId')"

  if [ -z "$operation_id" ] || [ "$operation_id" = "null" ]; then
    echo "the video-story seed could not request its capture job" >&2
    return 1
  fi

  # One real PNG through the real media route. It is a 1x1 fixture, decoded and verified by
  # the delivered store — and what the browser suite draws from it is the served bytes,
  # never a claim that a page was photographed.
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
    echo "the video-story seed could not upload its fixture image" >&2
    return 1
  fi

  local candidate_id
  candidate_id="$(cat /proc/sys/kernel/random/uuid)"
  local now
  now="$(date -u +%Y-%m-%dT%H:%M:%S.000000Z)"
  local scope_id
  scope_id="$(docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf_e2e_video_story --quiet --tuples-only --no-align \
    --command 'SELECT id FROM installation LIMIT 1')"

  # `-i` is load-bearing: without it the heredoc never reaches psql, psql exits 0 having
  # inserted nothing, and the suite would run against an empty choice it mistakes for an
  # offered one. The row is read back below.
  docker exec -i "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf_e2e_video_story --quiet \
    --set=ON_ERROR_STOP=1 \
    <<SQL >/dev/null
INSERT INTO screenshot_candidates
  (id, scope_id, project_id, milestone_id, media_id, operation_id, source_url, final_url,
   viewport, captured_at, status, caption, is_primary, display_order, revision, created_at)
VALUES
  ('${candidate_id}', '${scope_id}', '${project_id}', '${milestone_id}', '${media_id}', '${operation_id}',
   'https://example.com/pipeline', 'https://example.com/pipeline',
   '{"name":"desktop","width":1440,"height":900}', '${now}', 'candidate', '', false, 0, 1, '${now}');
SQL

  local stored
  stored="$(docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf_e2e_video_story \
    --quiet --tuples-only --no-align \
    --command "SELECT count(*) FROM screenshot_candidates WHERE milestone_id = '${milestone_id}' AND status = 'candidate'")"

  if [ "$stored" != "1" ]; then
    echo "the video-story seed stored '${stored}' candidates, which is not the one offered row" >&2
    return 1
  fi

  # Selected through the delivered route, not by editing the row: the selection publishes
  # the picture into the evidence head in the same server transaction, which is the
  # second bundle revision the story approval below must bind.
  local bundle_revision
  bundle_revision="$(curl --silent --show-error --request PATCH --cookie "$cookie_jar" \
    --data '{"status":"selected","caption":"The pipeline digest view.","isPrimary":null,"expectedRevision":1,"expectedBundleRevision":1}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 1' \
    "${origin}/api/screenshots/${candidate_id}" | jq -r '.bundleRevision')"

  if [ "$bundle_revision" != "2" ]; then
    echo "the video-story seed selected '${bundle_revision}', which is not the second bundle revision" >&2
    return 1
  fi

  local bundle_id manual_item_id
  bundle_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    -H 'Accept: application/json' "${origin}/api/milestones/${milestone_id}/evidence" \
    | jq -r '.items[0].id')"
  manual_item_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    -H 'Accept: application/json' "${origin}/api/milestones/${milestone_id}/evidence" \
    | jq -r '.items[0].items[] | select(.kind == "manual") | .id' | head -n 1)"

  if [ -z "$manual_item_id" ] || [ "$manual_item_id" = "null" ]; then
    echo "the video-story seed could not read the carried manual item the story cites" >&2
    return 1
  fi

  # The story is stored against the second bundle revision: the selection above carried
  # every fact to new ids, so the summary cites the head's ids rather than the first
  # revision's, and the approval binds the head the script will be grounded in — including
  # the selected picture, or the approval reads back invalidated.
  story_media_hash="$(printf '%s|%s' "$media_id" 'The pipeline digest view.' | sha256sum | cut -d' ' -f1)"
  seed_static_blog_export_rows "$postgres_container" "chronicleaf_e2e_video_story" \
    "$project_id" "$milestone_id" "$bundle_id" "$manual_item_id" \
    "Storyboard source" "yes" 2 "$story_media_hash"

  local bound
  bound="$(docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf_e2e_video_story \
    --quiet --tuples-only --no-align \
    --command "SELECT count(*) FROM content_approvals WHERE asset_id IN (SELECT id FROM content_assets WHERE milestone_id = '${milestone_id}' AND type = 'markdown-story') AND evidence_revision = 2 AND invalidated_at IS NULL")"

  if [ "$bound" != "1" ]; then
    echo "the video-story seed bound '${bound}' live approvals to the second revision, which is not one" >&2
    return 1
  fi

  # The bare milestone carries no evidence and no story: the panel on its row
  # proves the empty states rather than a second script.
  local bare_id
  bare_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"title":"Bare storyboard milestone","version":"0.1.0","occurredAt":"2026-10-02T09:00:00Z","type":"feature","notes":"Recorded by the e2e seed.","knownLimitations":[],"nextSteps":[]}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects/${project_id}/milestones" | jq -r '.id')"

  if [ -z "$bare_id" ] || [ "$bare_id" = "null" ]; then
    echo "the video-story seed could not create its bare milestone" >&2
    return 1
  fi
}

# ---------------------------------------------------------------------------------------------
# The agency-area fixture: Client A with Agency Project 1 and Agency Project 2 (each with one
# confirmed in-window milestone carrying manual evidence), Client B with Agency Project 3, and
# one archived client — everything through the delivered routes with the real access token and
# a real antiforgery value, so the clients, the milestones, their confirmations, their evidence
# and their assignments are records the product itself created.
#
# What the seed deliberately does NOT create: no weekly-report row. A report is rendered by the
# delivered deterministic template, so the browser's own generate calls produce every row the
# suite asserts about — including the DST-week window, the empty week, the regeneration
# revision and the drifted evidence. A seed that inserted a row would prove the panel can read
# a fixture, not that it can drive the flow.
#
# The milestones fall in the DST week starting 2026-03-08 America/New_York (Mar 8 00:00 EST is
# 05:00Z, Mar 15 00:00 EDT is 04:00Z), so the suite's window assertion is about a real
# transition rather than a comment claiming one.
# ---------------------------------------------------------------------------------------------
seed_agency_area() {
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
    echo "the agency-area seed could not read an antiforgery value from ${origin}" >&2
    return 1
  fi

  seed_agency_project() {
    curl --silent --show-error --cookie "$cookie_jar" \
      --data "{\"name\":\"$1\",\"description\":\"$2\"}" \
      -H 'Content-Type: application/json' -H "Origin: ${origin}" \
      -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
      "${origin}/api/projects"
  }

  local project1 project1_revision project2 project2_revision project3 project3_revision
  project1="$(seed_agency_project "Agency Project 1" "First project of Client A." | jq -r '.id')"
  project2="$(seed_agency_project "Agency Project 2" "Second project of Client A." | jq -r '.id')"
  project3="$(seed_agency_project "Agency Project 3" "Only project of Client B." | jq -r '.id')"

  if [ -z "$project1" ] || [ "$project1" = "null" ] || [ -z "$project2" ] || [ "$project2" = "null" ] || [ -z "$project3" ] || [ "$project3" = "null" ]; then
    echo "the agency-area seed could not create its projects" >&2
    return 1
  fi

  seed_agency_milestone() {
    local milestone
    milestone="$(curl --silent --show-error --cookie "$cookie_jar" \
      --data "{\"title\":\"$2\",\"version\":\"0.1.0\",\"occurredAt\":\"$3\",\"type\":\"feature\",\"notes\":\"Recorded by the e2e seed.\",\"knownLimitations\":[],\"nextSteps\":[]}" \
      -H 'Content-Type: application/json' -H "Origin: ${origin}" \
      -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
      "${origin}/api/projects/$1/milestones" | jq -r '.id')"

    if [ -z "$milestone" ] || [ "$milestone" = "null" ]; then
      echo "the agency-area seed could not create milestone $2" >&2
      return 1
    fi

    # A manual milestone is confirmed by the act of saving it, so no confirm call is needed;
    # the manual note is the evidence the weekly template renders.
    curl --silent --show-error --cookie "$cookie_jar" \
      --data '{"notes":"Shipped behind the weekly-report seed.","expectedRevision":0}' \
      -H 'Content-Type: application/json' -H "Origin: ${origin}" \
      -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
      "${origin}/api/milestones/${milestone}/evidence/manual" >/dev/null
  }

  seed_agency_milestone "$project1" "Agency milestone one" "2026-03-10T12:00:00Z"
  seed_agency_milestone "$project2" "Agency milestone two" "2026-03-11T12:00:00Z"
  seed_agency_milestone "$project3" "Agency milestone three" "2026-03-12T12:00:00Z"

  seed_agency_client() {
    curl --silent --show-error --cookie "$cookie_jar" \
      --data "{\"name\":\"$1\",\"notes\":\"$2\"}" \
      -H 'Content-Type: application/json' -H "Origin: ${origin}" \
      -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
      "${origin}/api/clients"
  }

  local client_a client_b archived_client archived_revision
  client_a="$(seed_agency_client "Client A" "Two projects, reported weekly." | jq -r '.id')"
  client_b="$(seed_agency_client "Client B" "One project, reported weekly." | jq -r '.id')"
  archived_client="$(seed_agency_client "Archived Client" "Reads stay, new work refuses." | jq -r '.id')"
  archived_revision="$(curl --silent --show-error --cookie "$cookie_jar" \
    -H 'Accept: application/json' "${origin}/api/clients/${archived_client}" | jq -r '.revision')"

  if [ -z "$client_a" ] || [ "$client_a" = "null" ] || [ -z "$client_b" ] || [ "$client_b" = "null" ] || [ -z "$archived_client" ] || [ "$archived_client" = "null" ]; then
    echo "the agency-area seed could not create its clients" >&2
    return 1
  fi

  seed_agency_assign() {
    local revision
    revision="$(curl --silent --show-error --cookie "$cookie_jar" \
      -H 'Accept: application/json' "${origin}/api/projects/$1" | jq -r '.revision')"

    curl --silent --show-error --request PATCH --cookie "$cookie_jar" \
      --data "{\"clientId\":\"$2\"}" \
      -H 'Content-Type: application/json' -H "Origin: ${origin}" \
      -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H "If-Match: ${revision}" \
      "${origin}/api/projects/$1/client" >/dev/null
  }

  seed_agency_assign "$project1" "$client_a"
  seed_agency_assign "$project2" "$client_a"
  seed_agency_assign "$project3" "$client_b"

  curl --silent --show-error --request DELETE --cookie "$cookie_jar" \
    -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H "If-Match: ${archived_revision}" \
    "${origin}/api/clients/${archived_client}" >/dev/null

  # The seed reads its own rows back and refuses to continue unless the
  # hierarchy it built is the hierarchy that is stored.
  local members
  members="$(curl --silent --show-error --cookie "$cookie_jar" \
    -H 'Accept: application/json' "${origin}/api/clients/${client_a}/projects" | jq -r '.items | length')"

  if [ "$members" != "2" ]; then
    echo "the agency-area seed grouped ${members} projects under Client A, expected 2" >&2
    return 1
  fi
}

seed_review_rows() {
  local container="$1"
  local database_name="$2"
  local project_id="$3"
  local milestone_id="$4"
  local bundle_id="$5"
  local evidence_item_id="$6"

  local body summary voice_snapshot body_hash fingerprint now
  now="$(date -u +%Y-%m-%dT%H:%M:%S.000000Z)"

  # The body, with a raw tag and both kinds of link in it so the preview claim is actually
  # testable. Numbers are avoided so the fixture cannot trip the numeral rule - which is a real
  # rule, and a fixture that violated it would be testing the wrong thing.
  body=$'# Reproducible release pipeline\n\nThe pipeline now reproduces from a clean checkout.[^c1]\n\n## What changed\n\n- The pipeline runs from a clean checkout.[^c1]\n- The build produces the same digest each time.[^c2]\n\n## Problems solved\n\nA release could not be rebuilt from its source.[^c3]\n\n## Known limitations\n\nOnly Linux is covered.[^c4]\n\n## Next steps\n\nCover the other platform.[^c5]\n\nThe line below is a <b>not bold</b> tag in the source, and a preview renders it as these characters.\n\nAn [unsafe link](javascript:alert('"'"'review'"'"')) goes nowhere, and a [safe link](https://example.invalid/notes) stays a link.\n\n[^c1]: The pipeline runs from a clean checkout.\n[^c2]: The build produces the same digest each time.\n[^c3]: A release could not be rebuilt from its source.\n[^c4]: Only Linux is covered.\n[^c5]: Cover the other platform.\n'

  body_hash="$(printf '%s' "$body" | sha256sum | cut -d' ' -f1)"

  # The delivered hasher's canonical form over the English values this body is judged under: the
  # resolved values joined by U+001F, in the order VoiceSnapshotHasher writes them. The read
  # route recomputes the fingerprint rather than trusting the stored one - so a default that ever
  # changes makes this suite fail rather than make the panel show a voice it did not check.
  fingerprint="$(printf 'en\037restrained\037true\03760\037high\037low\037true\037革命性\037震撼\037game-changing\037exciting news' \
    | sha256sum | cut -d' ' -f1)"

  voice_snapshot="{\"presetId\":\"technical-blog\",\"profileRevision\":0,\"capturedAt\":\"${now}\",\"language\":\"en\",\"tone\":\"restrained\",\"emoji\":true,\"titleMaxChars\":60,\"technicalDetail\":\"high\",\"marketing\":\"low\",\"firstPerson\":true,\"forbiddenTerms\":[\"革命性\",\"震撼\",\"game-changing\",\"exciting news\"],\"fingerprint\":\"${fingerprint}\"}"

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

  # One statement per table, in dependency order. The trigger on content_revisions refuses UPDATE
  # and DELETE, which is the append-only contract, and an INSERT is what a completed generation
  # would do.
  #
  # `-i` is load-bearing: `docker exec` only forwards this heredoc to psql's stdin when it is asked to,
  # and without it psql reads an empty script and exits 0 having inserted nothing. The rows are read
  # back below and the seed refuses to continue if they are not there.
  #
  # The three long values travel as psql variables and are interpolated with `:'name'`, which is psql's
  # own quoting. A body interpolated into the SQL text by the shell would have to be escaped by the
  # shell, and the two languages disagree about what a quote is.
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
  # its own would be a fixture proving nothing: the approval binds that hash.
  local stored
  stored="$(docker exec "$container" psql --username=chronicleaf --dbname="$database_name" \
    --quiet --tuples-only --no-align \
    --command "SELECT count(*) || ' ' || coalesce(max(body_hash), '') FROM content_revisions WHERE asset_id = '${asset_id}'")"

  if [ "$stored" != "1 ${body_hash}" ]; then
    echo "the review seed stored '${stored}', which is not the one revision hashed as ${body_hash}" >&2
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
# seed_candidates creates the project the candidate panel is read on, connects it to a repository,
# stores a webhook secret through the delivered vault, points the connection at it, and then writes
# the candidates and receipts a run of real deliveries would have produced.
#
# Every step through a product route is read back and refused when it did not take, and the rows written
# directly are counted back afterwards. That is the same rule the review seed follows and for the same
# reason: a seed that quietly wrote nothing leaves a green suite over an empty panel, which is the same
# failure class as a test filter that matches nothing, one layer down.
#
# The candidates are inserted directly, and that is stated rather than hidden. A candidate is produced by
# a signed GitHub delivery, and a browser cannot send one: the HMAC is over bytes a server received from
# GitHub, not over anything a page can hold. GithubReleaseCandidatesTests is where real signed bodies go
# through the production comparison and the production outbox consumer; what this seed does is put the
# rows those flows would have written in front of the panel, so the browser assertions are about the
# panel and the routes rather than about GitHub.
#
# More candidates are written than the panel asks for in one page, so a panel that reported the page as
# the total would be caught here. That distinction is the easiest thing about a bounded list to get wrong
# and the hardest to notice until somebody has lost a hundred proposals to it.
# ---------------------------------------------------------------------------------------------
seed_candidates() {
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
    echo "the candidate seed could not read an antiforgery value from ${origin}" >&2
    return 1
  fi

  local project_id
  project_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"name":"Fieldnotes","description":"Releases worth recording."}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects" | jq -r '.id')"

  if [ -z "$project_id" ] || [ "$project_id" = "null" ]; then
    echo "the candidate seed could not create its project" >&2
    return 1
  fi

  # The connection is stored by the delivered route, which describes the repository over the real
  # transport at api.github.com. This suite therefore has the same outbound prerequisite suite 6 has, and
  # it is stated rather than worked around: where that route cannot be reached the seed fails loudly,
  # because a stubbed GitHub would let a pass stand in for a result only a real one can give. The
  # repository is the same real public one suite 6 connects to: the route refuses a repository GitHub
  # does not serve, so a fictional fixture name is a 422 here rather than a connection. The connection
  # is what makes a candidate possible at all, so no fixture avoids it without also removing
  # the thing under test.
  local connection_id
  connection_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"owner":"octocat","repository":"Hello-World"}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/projects/${project_id}/github-connection" | jq -r '.id')"

  if [ -z "$connection_id" ] || [ "$connection_id" = "null" ]; then
    echo "the candidate seed could not connect a repository" >&2
    return 1
  fi

  # A webhook secret, stored through the delivered vault under this package's own provider. Nothing ever
  # reads the value back: the delivered route publishes whether a provider is configured and not the
  # value, which is what lets the panel offer to start listening without ever holding a secret. The
  # revision presented is 0 because this installation has nothing stored for the provider yet, and the
  # stored revision is read back rather than assumed.
  local secret_id
  secret_id="$(curl --silent --show-error --cookie "$cookie_jar" --request PUT \
    --data '{"value":"whsec-chronicleaf-e2e-candidates-sentinel-5d7c1a3e9b0f","expectedRevision":0}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/settings/credentials/github-webhook" | jq -r '.id')"

  if [ -z "$secret_id" ] || [ "$secret_id" = "null" ]; then
    echo "the candidate seed could not store a webhook secret" >&2
    return 1
  fi

  # The project's own revision, read back rather than assumed: storing a connection did not move it, and
  # the subscription is conditional on the project as the route last published it.
  local revision
  revision="$(curl --silent --show-error --cookie "$cookie_jar" \
    -H 'Accept: application/json' "${origin}/api/projects/${project_id}" | jq -r '.revision')"

  local listening
  listening="$(curl --silent --show-error --cookie "$cookie_jar" --request PUT \
    --data "{\"credentialRef\":\"${secret_id}\"}" \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H "If-Match: ${revision}" \
    "${origin}/api/projects/${project_id}/github-webhook" | jq -r '.enabled')"

  if [ "$listening" != "true" ]; then
    echo "the candidate seed could not subscribe the connection" >&2
    return 1
  fi

  seed_candidate_rows "$postgres_container" "$postgres_database" "$project_id" "$connection_id"
}

# Writes the candidates and the receipts a run of real deliveries would have produced, and then counts
# every one of them back.
#
# The values are the ones the store would write: the closed kind and source vocabularies, the three
# disjoint key prefixes, a resolved commit rather than a branch name in a revision column, and an ignored
# receipt that names its reason. One delivery produced no candidate at all - an unmerged pull request -
# and its receipt is here for that reason: a delivery that produced nothing is still a record that it
# arrived, and a panel that listed it would be proposing a reversal as an achievement.
seed_candidate_rows() {
  local container="$1"
  local database="$2"
  local project_id="$3"
  local connection_id="$4"

  docker exec -i "$container" psql --username=chronicleaf --dbname="$database" --quiet \
    --set=ON_ERROR_STOP=1 >/dev/null <<SQL
INSERT INTO github_webhook_deliveries
    (id, scope_id, connection_id, project_id, delivery_id, event, action, status, reason,
     candidate_count, received_at, proposals)
SELECT gen_random_uuid(), p.scope_id, c.id, p.id, d.delivery_id, d.event, d.action, d.status, d.reason,
       d.candidate_count, timestamptz '2026-09-30 08:00:00+00', '[]'::jsonb
FROM projects p
JOIN github_connections c ON c.project_id = p.id
CROSS JOIN (VALUES
    ('e2e-release',  'release',       'published', 'recorded', NULL,                1),
    ('e2e-tag',      'create',        NULL,       'recorded', NULL,                1),
    ('e2e-pull',     'pull_request',  'closed',   'recorded', NULL,                1),
    ('e2e-star',     'star',          'created',  'recorded', NULL,                1),
    ('e2e-unmerged', 'pull_request',  'closed',   'ignored',   'unsupported-action', 0)
) AS d(delivery_id, event, action, status, reason, candidate_count)
WHERE p.id = '${project_id}';

INSERT INTO github_candidates
    (id, scope_id, connection_id, project_id, source_key, kind, title, revision, source,
     delivery_id, received_at)
SELECT gen_random_uuid(), p.scope_id, c.id, p.id, k.source_key, k.kind, k.title, k.revision, 'webhook',
       k.delivery, timestamptz '2026-09-30 08:00:00+00'
FROM projects p
JOIN github_connections c ON c.project_id = p.id
CROSS JOIN (VALUES
    ('release:9301', 'release', 'Fifth release',
     '3333333333333333333333333333333333333333', 'e2e-release'),
    ('tag:v6.0.0:3333333333333333333333333333333333333333', 'tag', 'v6.0.0',
     '3333333333333333333333333333333333333333', 'e2e-tag'),
    ('event:pull_request:7001:2026-09-30T08:00:00.0000000+00:00', 'pull-request',
     'Add the reproducible pipeline', '2026-09-30T08:00:00.0000000+00:00', 'e2e-pull'),
    ('event:star:4242:100', 'star', '100 stars', '100', 'e2e-star')
) AS k(source_key, kind, title, revision, delivery)
WHERE p.id = '${project_id}';

-- Twenty-two more, so the stored total is twenty-six and the panel's page is twenty-five. A panel that
-- reported the page as the total would tell this owner they had lost one proposal; a panel that
-- reported twenty-six rows would be ignoring a bound the server set.
INSERT INTO github_candidates
    (id, scope_id, connection_id, project_id, source_key, kind, title, revision, source,
     delivery_id, received_at)
SELECT gen_random_uuid(), p.scope_id, c.id, p.id,
       'release:' || (8000 + n)::text, 'release',
       'Maintenance release ' || n::text,
       '4444444444444444444444444444444444444444', 'poll', NULL,
       timestamptz '2026-09-01 08:00:00+00' + (n || ' minutes')::interval
FROM projects p
JOIN github_connections c ON c.project_id = p.id
CROSS JOIN generate_series(1, 22) AS n
WHERE p.id = '${project_id}';
SQL

  # Every row is counted back, and the seed refuses unless each count is what it wrote. A seed that wrote
  # nothing would leave a green suite over an empty panel, which is a green run proving nothing.
  local candidates
  candidates="$(count_rows "$container" "$database" "SELECT count(*) FROM github_candidates WHERE project_id = '${project_id}'")"
  if [ "$candidates" != "26" ]; then
    echo "the candidate seed stored ${candidates} candidates, expected 26" >&2
    return 1
  fi

  local recorded
  recorded="$(count_rows "$container" "$database" "SELECT count(*) FROM github_webhook_deliveries WHERE project_id = '${project_id}' AND status = 'recorded'")"
  if [ "$recorded" != "4" ]; then
    echo "the candidate seed stored ${recorded} recorded receipts, expected 4" >&2
    return 1
  fi

  local ignored
  ignored="$(count_rows "$container" "$database" "SELECT count(*) FROM github_webhook_deliveries WHERE project_id = '${project_id}' AND status = 'ignored' AND reason = 'unsupported-action'")"
  if [ "$ignored" != "1" ]; then
    echo "the candidate seed stored ${ignored} ignored receipts, expected 1" >&2
    return 1
  fi

  local unlisted
  unlisted="$(count_rows "$container" "$database" "SELECT count(*) FROM github_candidates WHERE kind = 'pull-request' AND title = 'Abandon the pipeline'")"
  if [ "$unlisted" != "0" ]; then
    echo "the candidate seed stored a candidate for the unmerged pull request" >&2
    return 1
  fi
}

count_rows() {
  local container="$1"
  local database="$2"
  local sql="$3"

  docker exec "$container" psql --username=chronicleaf --dbname="$database" --quiet --tuples-only \
    --no-align --command "$sql" | tr -d '[:space:]'
}

# ---------------------------------------------------------------------------------------------
# seed_workflow_wayfinding creates the project the wayfinding suite walks: one project, one
# confirmed milestone with one manual evidence bundle, one GitHub connection row and two
# GitHub candidate rows.
#
# Every step a browser could take goes through the running server's own routes with the real
# access token and a real antiforgery value, so the project, the milestone, its confirmation
# and its evidence are records the product itself created. Only the connection and candidate
# rows are inserted directly, and that is stated rather than hidden: connecting describes
# the repository over the real api.github.com transport (suite 12's prerequisite, stated
# there), and a candidate is produced by a signed delivery whose HMAC is over bytes a server
# received from GitHub, not over anything a page can hold. GithubReleaseCandidatesTests is
# where real signed bodies go through the production comparison; what this seed does is put
# the rows those flows would have written in front of the panel, so the browser assertions
# are about the panel and the routes rather than about GitHub.
#
# The connection names the same real public repository suite 12 connects to, as plain text
# with no transport behind it — which is exactly what the list, import and schedule routes
# read. The schedule save the suite performs needs a readable connection, and this row is
# what makes it readable without any outbound prerequisite.
# ---------------------------------------------------------------------------------------------
seed_workflow_wayfinding() {
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
    echo "the wayfinding seed could not read an antiforgery value from ${origin}" >&2
    return 1
  fi

  local project_id
  project_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"name":"Wayfinding","description":"Walking the loop end to end."}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects" | jq -r '.id')"

  if [ -z "$project_id" ] || [ "$project_id" = "null" ]; then
    echo "the wayfinding seed could not create its project" >&2
    return 1
  fi

  # A manual milestone is confirmed by the act of saving it, so no confirm call is needed here.
  local milestone_id
  milestone_id="$(curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"title":"Wayfinding milestone","version":"0.1.0","occurredAt":"2026-10-01T09:00:00Z","type":"feature","notes":"Recorded by the e2e seed.","knownLimitations":[],"nextSteps":[]}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" \
    "${origin}/api/projects/${project_id}/milestones" | jq -r '.id')"

  if [ -z "$milestone_id" ] || [ "$milestone_id" = "null" ]; then
    echo "the wayfinding seed could not create its milestone" >&2
    return 1
  fi

  curl --silent --show-error --cookie "$cookie_jar" \
    --data '{"notes":"The loop is walked end to end.","expectedRevision":0}' \
    -H 'Content-Type: application/json' -H "Origin: ${origin}" \
    -H "X-Chronicleaf-Antiforgery: ${antiforgery}" -H 'If-Match: 0' \
    "${origin}/api/milestones/${milestone_id}/evidence/manual" >/dev/null

  # `-i` is load-bearing: `docker exec` only forwards this heredoc to psql's stdin when it is
  # asked to, and without it psql reads an empty script and exits 0 having inserted nothing.
  # The rows are counted back below and the seed refuses to continue if they are not there.
  docker exec -i "$postgres_container" psql --username=chronicleaf --dbname="$postgres_database" --quiet \
    --set=ON_ERROR_STOP=1 >/dev/null <<SQL
INSERT INTO github_connections
    (id, scope_id, project_id, owner, repo, credential_ref, default_branch, repository_id,
     created_at, updated_at)
SELECT gen_random_uuid(), p.scope_id, p.id, 'octocat', 'Hello-World', NULL, 'main', NULL,
       now(), now()
FROM projects p
WHERE p.id = '${project_id}';

INSERT INTO github_candidates
    (id, scope_id, connection_id, project_id, source_key, kind, title, revision, source,
     delivery_id, received_at)
SELECT gen_random_uuid(), p.scope_id, c.id, p.id, k.source_key, k.kind, k.title, k.revision, 'poll',
       NULL, timestamptz '2026-09-30 08:00:00+00'
FROM projects p
JOIN github_connections c ON c.project_id = p.id
CROSS JOIN (VALUES
    ('release:9301', 'release', 'Fifth release',
     '3333333333333333333333333333333333333333'),
    ('event:pull_request:7001:2026-09-30T08:00:00.0000000+00:00', 'pull-request',
     'Add the reproducible pipeline', '2026-09-30T08:00:00.0000000+00:00')
) AS k(source_key, kind, title, revision)
WHERE p.id = '${project_id}';
SQL

  # Read back what the database actually holds. A seed that reports success and stores no
  # rows is worse than one that fails, so the counts below are the seed's own acceptance.
  local connections
  connections="$(count_rows "$postgres_container" "$postgres_database" "SELECT count(*) FROM github_connections WHERE project_id = '${project_id}'")"
  if [ "$connections" != "1" ]; then
    echo "the wayfinding seed stored ${connections} connections, expected 1" >&2
    return 1
  fi

  local candidates
  candidates="$(count_rows "$postgres_container" "$postgres_database" "SELECT count(*) FROM github_candidates WHERE project_id = '${project_id}'")"
  if [ "$candidates" != "2" ]; then
    echo "the wayfinding seed stored ${candidates} candidates, expected 2" >&2
    return 1
  fi
}

# ---------------------------------------------------------------------------------------------
# Suite 1: the shell, the access boundary, the projects and milestones lifecycle, and the
# background operations panel. Its project list must be empty when it starts, which is why this
# suite has a database to itself.
# ---------------------------------------------------------------------------------------------
suite_core() (
  trap 'stop_suite' RETURN
  echo "==> starting the published application on a template-cloned database at $base_url"
  start_suite core "$base_url" "$log_file" "$repo_root/$publish_dir/access-token"
  core_installation="$(docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf_e2e_core --quiet --tuples-only --no-align --command 'SELECT id FROM installation LIMIT 1')"

  echo "==> recording the background operations the panel fixture reads"
  seed_operations "$postgres_container" chronicleaf_e2e_core "$core_installation"

  echo "==> npm run test:e2e (real browser against $base_url)"
  run_suite "$base_url" "$log_file" "$repo_root/$publish_dir/access-token" \
    e2e/shell.spec.ts \
    tests/background-jobs.spec.ts \
    tests/community-access.spec.ts \
    tests/projects-and-milestones.spec.ts \
    tests/product-shell-ux.spec.ts \
    tests/a11y-core.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 2: the milestone evidence view. It needs a milestone, and a milestone needs a project, and
# a project is never removed - so it runs against its own installation and its own published
# instance rather than against the one above.
# ---------------------------------------------------------------------------------------------
suite_evidence() (
  trap 'stop_suite' RETURN
  echo "==> starting a second published application on a template-cloned database at $evidence_base_url"
  start_suite evidence "$evidence_base_url" "$evidence_log_file" "$repo_root/$publish_dir/access-token-evidence"

  echo "==> npm run test:e2e (real browser against $evidence_base_url)"
  run_suite "$evidence_base_url" "$evidence_log_file" "$repo_root/$publish_dir/access-token-evidence" \
    tests/milestone-evidence.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 3: the credential settings surface. It asserts that a new installation reads as
# unconfigured for every provider, and a stored credential cannot be un-stored by the UI, so it needs
# its own installation and its own published instance.
# ---------------------------------------------------------------------------------------------
suite_credentials() (
  trap 'stop_suite' RETURN
  echo "==> starting a third published application on a template-cloned database at $credentials_base_url"
  start_suite credentials "$credentials_base_url" "$credentials_log_file" "$repo_root/$publish_dir/access-token-credentials"

  echo "==> npm run test:e2e (real browser against $credentials_base_url)"
  run_suite "$credentials_base_url" "$credentials_log_file" "$repo_root/$publish_dir/access-token-credentials" \
    tests/credential-vault.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 4: the writing voice. It asserts that a project starts on the design's defaults, and it then
# changes them and records a draft under the changed voice - none of which it can undo through the
# UI. It therefore needs its own installation and its own published instance, and it is the fourth
# suite for exactly the reason suite 1 is the first.
# ---------------------------------------------------------------------------------------------
suite_voice() (
  trap 'stop_suite' RETURN
  echo "==> starting a fourth published application on a template-cloned database at $voice_base_url"
  start_suite voice "$voice_base_url" "$voice_log_file" "$repo_root/$publish_dir/access-token-voice"

  echo "==> npm run test:e2e (real browser against $voice_base_url)"
  run_suite "$voice_base_url" "$voice_log_file" "$repo_root/$publish_dir/access-token-voice" \
    tests/voice-profiles.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 5: the bounded generation surface. It asserts that a new installation reads as unconfigured
# for every provider slot, that no slot reports an adapter, and that a selection nothing can call is
# refused before a snapshot is frozen - all of which a stored credential or an earlier run of this
# suite itself would make false. Its own installation, its own database and its own published instance.
# ---------------------------------------------------------------------------------------------
suite_generation() (
  trap 'stop_suite' RETURN
  echo "==> starting a fifth published application on a template-cloned database at $generation_base_url"
  start_suite generation "$generation_base_url" "$generation_log_file" "$repo_root/$publish_dir/access-token-generation"

  echo "==> npm run test:e2e (real browser against $generation_base_url)"
  run_suite "$generation_base_url" "$generation_log_file" "$repo_root/$publish_dir/access-token-generation" \
    tests/llm-provider-contract.spec.ts
  stop_suite
)


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
suite_github() (
  trap 'stop_suite' RETURN
  echo "==> starting a sixth published application on a template-cloned database at $github_base_url"
  start_suite github "$github_base_url" "$github_log_file" "$repo_root/$publish_dir/access-token-github"

  echo "==> npm run test:e2e (real browser against $github_base_url)"
  run_suite "$github_base_url" "$github_log_file" "$repo_root/$publish_dir/access-token-github" \
    tests/github-evidence.spec.ts
  stop_suite
)


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
suite_capture() (
  trap 'stop_suite' RETURN
  echo "==> starting a seventh published application on a template-cloned database at $capture_base_url"
  start_suite capture "$capture_base_url" "$capture_log_file" "$repo_root/$publish_dir/access-token-capture"

  echo "==> npm run test:e2e (real browser against $capture_base_url)"
  run_suite "$capture_base_url" "$capture_log_file" "$repo_root/$publish_dir/access-token-capture" \
    tests/screenshot-capture.spec.ts
  stop_suite
)


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
suite_drafts() (
  trap 'stop_suite' RETURN
  echo "==> starting an eighth published application on a template-cloned database at $drafts_base_url"
  start_suite drafts "$drafts_base_url" "$drafts_log_file" "$repo_root/$publish_dir/access-token-drafts"

  echo "==> recording the project, evidence and stored draft the panel reads"
  seed_grounded_draft "$drafts_base_url" "$(cat "$repo_root/$publish_dir/access-token-drafts")"

  echo "==> npm run test:e2e (real browser against $drafts_base_url)"
  run_suite "$drafts_base_url" "$drafts_log_file" "$repo_root/$publish_dir/access-token-drafts" \
    tests/grounded-markdown-drafts.spec.ts
  stop_suite
)


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
suite_curation() (
  trap 'stop_suite' RETURN
  echo "==> starting a ninth published application on a template-cloned database at $curation_base_url"
  start_suite curation "$curation_base_url" "$curation_log_file" "$repo_root/$publish_dir/access-token-curation"

  echo "==> recording the project, evidence, media and offered candidates the picker reads"
  seed_curation "$curation_base_url" "$(cat "$repo_root/$publish_dir/access-token-curation")"

  echo "==> npm run test:e2e (real browser against $curation_base_url)"
  run_suite "$curation_base_url" "$curation_log_file" "$repo_root/$publish_dir/access-token-curation" \
    tests/screenshot-curation.spec.ts
  stop_suite
)


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
suite_timeline() (
  trap 'stop_suite' RETURN
  echo "==> starting a tenth published application on a template-cloned database at $timeline_base_url"
  start_suite timeline "$timeline_base_url" "$timeline_log_file" "$repo_root/$publish_dir/access-token-timeline"

  echo "==> npm run test:e2e (real browser against $timeline_base_url)"
  run_suite "$timeline_base_url" "$timeline_log_file" "$repo_root/$publish_dir/access-token-timeline" \
    tests/milestone-timeline.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 11: human review. It needs a milestone with a stored draft the browser can edit, approve
# and export, and it needs that draft to carry no finding - because an approval is refused while
# findings remain - so it has its own installation and its own published instance for the same
# reason suites 1 to 10 each have one.
#
# What this suite claims, and what it does not. The project, the milestones and their evidence
# are created by the running server's own routes through the real access boundary. The two
# stored drafts are inserted the way the grounded-draft suite inserts its rows - directly, with
# the rows read back and refused when they are not there - because a draft is produced by a
# provider and this host calls none. The summary cites the real evidence item and the voice
# snapshot declares the English the body is written in, so neither finding list has anything to
# report and the browser can actually approve. The body carries a raw tag, an unsafe link and a
# safe link, so a pass is evidence that the panel renders markup as characters rather than
# running it. So a pass is evidence that the panel edits a draft into a new revision, binds an
# approval to a revision and a body hash, serves the approved bytes as a download, ends the
# approval on a later edit, and never approves on Enter - and is NOT evidence that a draft was
# generated on this host. The .NET suite is where a real provider answer flows through
# generation into review.
# ---------------------------------------------------------------------------------------------
suite_review() (
  trap 'stop_suite' RETURN
  echo "==> starting an eleventh published application on a template-cloned database at $review_base_url"
  start_suite review "$review_base_url" "$review_log_file" "$repo_root/$publish_dir/access-token-review"

  echo "==> recording the project, evidence and stored drafts the review panel reads"
  seed_review "$review_base_url" "$(cat "$repo_root/$publish_dir/access-token-review")"

  echo "==> npm run test:e2e (real browser against $review_base_url)"
  run_suite "$review_base_url" "$review_log_file" "$repo_root/$publish_dir/access-token-review" \
    tests/markdown-review-export.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 12: the tag- and event-derived Milestone candidates. It needs a project with a repository
# connection and stored candidates, and a connection cannot be removed through the UI and a
# credential cannot be written twice, so its own installation, its own database and its own published
# instance - for the same reason suites 5, 6, 7 and 11 each have one.
#
# What this suite claims is narrow and says so. The candidates are seeded, because a candidate is
# produced by a signed GitHub delivery and a browser cannot send one; the real signed path, the real HMAC
# comparison and the real outbox consumer are the .NET suite's, and this one asserts the panel. What a
# pass here establishes is that the panel reads the server's candidates, renders each as a proposal with
# its source and received instant, offers no control that could confirm one, tells a project with no
# repository connected apart from a project with nothing proposed, and reports the stored total beside
# the page rather than as the page. It is NOT evidence that GitHub sent a delivery, that a poll read a
# repository, or that a candidate can be recorded - and the last of those is asserted negatively.
# ---------------------------------------------------------------------------------------------
suite_candidates() (
  trap 'stop_suite' RETURN
  echo "==> starting a twelfth published application on a template-cloned database at $candidates_base_url"
  start_suite candidates "$candidates_base_url" "$candidates_log_file" "$repo_root/$publish_dir/access-token-candidates"

  echo "==> recording the project, connection, secret, subscription and candidates the panel reads"
  seed_candidates "$candidates_base_url" "$(cat "$repo_root/$publish_dir/access-token-candidates")"

  echo "==> npm run test:e2e (real browser against $candidates_base_url)"
  run_suite "$candidates_base_url" "$candidates_log_file" "$repo_root/$publish_dir/access-token-candidates" \
    tests/github-release-candidates.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 13: the project narrative. It needs a project it records history into, and neither a
# project nor a narrative entry can be removed through the UI - an entry is the owner's own
# words, and history somebody did not act on is still history - so its own installation, its own
# database and its own published instance, for the same reason suites 10 to 12 each have one.
#
# Nothing here is seeded: every project and every entry is recorded by the same clicks an owner
# would make, and every refresh runs in the delivered queue on the published server - the panel
# follows the job until the operations route settles it, which is what the suite asserts it does.
# What a pass here establishes is that a project with no history shows honest empty states, that
# a recorded entry reads as attributed history in its own section, that a refresh concludes a
# generated summary in a different section with its revision, that an edit marks the prior summary
# stale until the next refresh advances the revision, and that the server says the same thing the
# panel does. It is NOT evidence that any provider was called: the delivered summary builder is
# deterministic and reaches no provider, and a summary is an arrangement of attributed sources
# rather than generated prose.
# ---------------------------------------------------------------------------------------------
suite_narrative() (
  trap 'stop_suite' RETURN
  echo "==> starting a thirteenth published application on a template-cloned database at $narrative_base_url"
  start_suite narrative "$narrative_base_url" "$narrative_log_file" "$repo_root/$publish_dir/access-token-narrative"

  echo "==> npm run test:e2e (real browser against $narrative_base_url)"
  run_suite "$narrative_base_url" "$narrative_log_file" "$repo_root/$publish_dir/access-token-narrative" \
    tests/narrative-memory.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 14: reusable content formats. It needs a milestone with an approved story the browser
# can derive from, and neither a story nor an approval can be produced through the UI without a
# provider - so its own installation, its own database and its own published instance, for the
# same reason suites 10 to 13 each have one.
#
# Nothing the browser touches is seeded: every derivation and every variant approval below goes
# through the shipped panel and the published server, and the variant rows and the variant
# approval are records the product itself created. What a pass here establishes is that the panel
# lists every derived format, derives one with its defined sections and attributed citations, and
# approves it on its own revision - and that the readme-update format names its proposal as one.
# It is NOT evidence that a draft was generated on this host: the approved story is seeded, and
# the .NET suite is where a real provider answer flows through generation into a variant's source.
# ---------------------------------------------------------------------------------------------
suite_variants() (
  trap 'stop_suite' RETURN
  echo "==> starting a fourteenth published application on a template-cloned database at $variants_base_url"
  start_suite variants "$variants_base_url" "$variants_log_file" "$repo_root/$publish_dir/access-token-variants"

  echo "==> recording the project, evidence, approved story and approval the variant panel reads"
  seed_variants "$variants_base_url" "$(cat "$repo_root/$publish_dir/access-token-variants")"

  echo "==> npm run test:e2e (real browser against $variants_base_url)"
  run_suite "$variants_base_url" "$variants_log_file" "$repo_root/$publish_dir/access-token-variants" \
    tests/content-asset-variants.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 15: team invitations and members. It needs a signed-in owner on an installation that
# holds no invitation beyond what the suite itself attempts, and an invitation cannot be
# un-issued through the UI - so its own installation, its own database and its own published
# instance, for the same reason suites 1 to 14 each have one.
#
# Nothing here is seeded: the empty states are asserted before anything is attempted, and the
# failures assert the server's own Community refusal verbatim with no row invented. What a
# pass here establishes is that the triggers reach the delivered team routes and report what
# the server says. It is NOT evidence that an invitation was issued, a role was changed or a
# grant was stored on this host: this installation is Community single-tenant, and the .NET
# suite is where the Hosted flows are proven.
# ---------------------------------------------------------------------------------------------
suite_team() (
  trap 'stop_suite' RETURN
  echo "==> starting a fifteenth published application on a template-cloned database at $team_base_url"
  start_suite team "$team_base_url" "$team_log_file" "$repo_root/$publish_dir/access-token-team"

  echo "==> npm run test:e2e (real browser against $team_base_url)"
  run_suite "$team_base_url" "$team_log_file" "$repo_root/$publish_dir/access-token-team" \
    tests/team-panels.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 16: hosted tenancy. It needs a signed-in caller whose tenants the views list, and a
# tenant cannot be removed through the UI - so its own installation, its own database and its
# own published instance, for the same reason suite 15 is its own.
#
# Nothing here is seeded: this installation is Community single-tenant, so the server
# answers every tenancy route with its own generic refusal and the views show that answer
# verbatim with no tenant invented. What a pass here establishes is that the views call
# only the delivered tenancy routes and render whatever the server answers. It is NOT
# evidence that a tenant was created, selected or imported on this host, and the .NET suite
# is where the Hosted flows are proven.
# ---------------------------------------------------------------------------------------------
suite_tenancy() (
  trap 'stop_suite' RETURN
  echo "==> starting a sixteenth published application on a template-cloned database at $tenancy_base_url"
  start_suite tenancy "$tenancy_base_url" "$tenancy_log_file" "$repo_root/$publish_dir/access-token-tenancy"

  echo "==> npm run test:e2e (real browser against $tenancy_base_url)"
  run_suite "$tenancy_base_url" "$tenancy_log_file" "$repo_root/$publish_dir/access-token-tenancy" \
    tests/tenancy-views.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 17: evolution images. It needs a project the browser records itself, and a project
# cannot be removed once it exists - so its own installation, its own database and its own
# published instance, for the same reason suites 10 to 16 each have one.
#
# Nothing here is seeded: the empty state is asserted before anything is rendered, and the
# failure renders against an unknown milestone so the server refuses rather than draws.
# What a pass here establishes is that the panel lists, renders, approves and links what
# the server stores, with preview and download as route links rather than synthesised
# pictures. It is NOT evidence that a comparison depicts real screenshots on this host: no
# candidate exists here.
# ---------------------------------------------------------------------------------------------
suite_evolution() (
  trap 'stop_suite' RETURN
  echo "==> starting a seventeenth published application on a template-cloned database at $evolution_base_url"
  start_suite evolution "$evolution_base_url" "$evolution_log_file" "$repo_root/$publish_dir/access-token-evolution"

  echo "==> npm run test:e2e (real browser against $evolution_base_url)"
  run_suite "$evolution_base_url" "$evolution_log_file" "$repo_root/$publish_dir/access-token-evolution" \
    tests/evolution-images.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 18: evolution GIFs. It needs a project the browser records itself, and a project
# cannot be removed once it exists - so its own installation, its own database and its own
# published instance, for the same reason suite 17 is its own.
#
# Nothing here is seeded: the empty state is asserted before anything is rendered, and the
# failure renders against an unknown milestone so the server refuses rather than draws.
# What a pass here establishes is that the panel lists, renders, approves and links what
# the server stores, with preview, first-frame and download as route links rather than any
# client-side animation. It is NOT evidence that an animation depicts real screenshots on
# this host: no candidate exists here.
# ---------------------------------------------------------------------------------------------
suite_gif() (
  trap 'stop_suite' RETURN
  echo "==> starting an eighteenth published application on a template-cloned database at $gif_base_url"
  start_suite gif "$gif_base_url" "$gif_log_file" "$repo_root/$publish_dir/access-token-gif"

  echo "==> npm run test:e2e (real browser against $gif_base_url)"
  run_suite "$gif_base_url" "$gif_log_file" "$repo_root/$publish_dir/access-token-gif" \
    tests/evolution-gifs.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 19: publications. It needs a project the browser records itself, and a project
# cannot be removed once it exists - so its own installation, its own database and its own
# published instance, for the same reason suites 17 and 18 each have one.
#
# Nothing here is seeded: the empty state is asserted before anything is named, and the
# failures read and reconcile an unknown attempt so the server refuses in its own words.
# What a pass here establishes is that the panel reads and reconciles what the server
# stores and that a preview authorizes nothing. It is NOT evidence that anything was sent
# anywhere: no publish is attempted here and no transport is contacted.
# ---------------------------------------------------------------------------------------------
suite_publication() (
  trap 'stop_suite' RETURN
  echo "==> starting a nineteenth published application on a template-cloned database at $publication_base_url"
  start_suite publication "$publication_base_url" "$publication_log_file" "$repo_root/$publish_dir/access-token-publication"

  echo "==> npm run test:e2e (real browser against $publication_base_url)"
  run_suite "$publication_base_url" "$publication_log_file" "$repo_root/$publish_dir/access-token-publication" \
    tests/publications.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 20: journey publish and the public view. It needs a project the browser records
# itself, and a project cannot be removed once it exists - so its own installation, its own
# database and its own published instance, for the same reason suites 17 to 19 each have
# one.
#
# Nothing here is seeded: the empty state is asserted before anything is previewed, the
# preview refusal proves unapproved content cannot be published, and the unknown slug is
# the route's own 404 page with no redirect and no guessed content. What a pass here
# establishes is that the private triggers reach the delivered journey routes and the
# public view shows only what the projection route returns. It is NOT evidence that a
# journey was published on this host: nothing here is approved, so nothing can be.
# ---------------------------------------------------------------------------------------------
suite_journey() (
  trap 'stop_suite' RETURN
  echo "==> starting a twentieth published application on a template-cloned database at $journey_base_url"
  start_suite journey "$journey_base_url" "$journey_log_file" "$repo_root/$publish_dir/access-token-journey"

  echo "==> npm run test:e2e (real browser against $journey_base_url)"
  run_suite "$journey_base_url" "$journey_log_file" "$repo_root/$publish_dir/access-token-journey" \
    tests/journey-publish.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 21: project area information architecture. It creates its own project by name and
# never asserts global emptiness, so it has to run against its own installation and its own
# published instance rather than against the one the projects-and-milestones suite started
# empty - otherwise the first group's empty-state assertions would already see a project.
# ---------------------------------------------------------------------------------------------
suite_area() (
  trap 'stop_suite' RETURN
  echo "==> starting a twenty-first published application on a template-cloned database at $area_base_url"
  start_suite area "$area_base_url" "$area_log_file" "$repo_root/$publish_dir/access-token-area"

  echo "==> npm run test:e2e (real browser against $area_base_url)"
  run_suite "$area_base_url" "$area_log_file" "$repo_root/$publish_dir/access-token-area" \
    tests/project-area-ia.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 22: publish from variant. It needs a project with an approved story, a sealed
# `publisher` credential and a registered transport - and a publish, once accepted, can
# never be un-accepted - so its own installation, its own database and its own published
# instance, for the same reason suites 2 to 21 each have one.
#
# Two deployment facts are configured for this installation only. The GitHub Pages
# transport registers solely because an explicit repository names it, and its preview
# renders the site files locally - CPU only, no credential lease, no row, no remote
# call - so previewing proves the picker aims at a real classification. The dispatch
# loop is silenced, so the receipt the suite reads stays at the acceptance the request
# committed: what a pass establishes is that pick variant → pick target → preview →
# confirm reaches the delivered routes and the receipt names the server's answer. It is
# NOT evidence that anything was sent anywhere: no run executes here (the configured
# repository is a name, not a destination), and the dispatch-to-terminal half is the
# .NET suite's claim, not this one's.
# ---------------------------------------------------------------------------------------------
suite_publish_variant() (
  trap 'stop_suite' RETURN
  echo "==> starting a twenty-second published application on a template-cloned database at $publish_variant_base_url"
  start_suite publish_variant "$publish_variant_base_url" "$publish_variant_log_file" \
    "$repo_root/$publish_dir/access-token-publish-variant" \
    "Chronicleaf__Operations__Enabled=false Chronicleaf__GitHubPages__RepositoryOwner=e2e-owner Chronicleaf__GitHubPages__RepositoryName=e2e-docs Chronicleaf__GitHubPages__Branch=main"

  echo "==> seeding one approved story and one sealed publisher credential"
  seed_publish_variant "$publish_variant_base_url" "$(cat "$repo_root/$publish_dir/access-token-publish-variant")"

  echo "==> npm run test:e2e (real browser against $publish_variant_base_url)"
  run_suite "$publish_variant_base_url" "$publish_variant_log_file" "$repo_root/$publish_dir/access-token-publish-variant" \
    tests/publish-from-variant.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 23: static blog export UI. It needs a project with one approved story and one
# never-approved story - and neither an approval nor a story can be produced through the UI
# without a provider, while an approval, once recorded, cannot be un-recorded - so its own
# installation, its own database and its own published instance, for the same reason suites
# 2 to 22 each have one.
#
# Jobs stay enabled for this installation (the default start_suite path): the claim under
# test is that the worker really builds the archive the panel offers for download, so a
# silenced dispatch loop would test the wrong thing here. What a pass establishes is that
# pick milestone → check asset → Export reaches the delivered routes, the panel watches
# the job to its terminal word with no invented progress, the offered download serves
# bytes byte-equal to the delivered download route, an unapproved selection is refused
# in the server's own words with no download offered, and nothing exported before the
# owner acted. It is NOT evidence that the archive renders any particular page: the bytes
# inside the ZIP are the .NET suite's claim, not this one's.
# ---------------------------------------------------------------------------------------------
suite_static_blog_export() (
  trap 'stop_suite' RETURN
  echo "==> starting a twenty-third published application on a template-cloned database at $static_blog_export_base_url"
  start_suite static_blog_export "$static_blog_export_base_url" "$static_blog_export_log_file" \
    "$repo_root/$publish_dir/access-token-static-blog-export"

  echo "==> seeding one approved story and one never-approved story"
  seed_static_blog_export "$static_blog_export_base_url" "$(cat "$repo_root/$publish_dir/access-token-static-blog-export")"

  echo "==> npm run test:e2e (real browser against $static_blog_export_base_url)"
  run_suite "$static_blog_export_base_url" "$static_blog_export_log_file" "$repo_root/$publish_dir/access-token-static-blog-export" \
    tests/static-blog-export-ui.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 24: publisher adapter panels. It needs a project with an approved story, a sealed
# `publisher` credential and every transport registered - and a publish, once accepted, can
# never be un-accepted - so its own installation, its own database and its own published
# instance, for the same reason suites 2 to 23 each have one.
#
# Six deployment facts are configured for this installation only. Each transport registers
# solely because an explicit section names it, and each preview renders CPU-only - no
# credential lease, no row, no remote call - so previewing proves the picker aims at a real
# classification through the repaired production binding. The dispatch loop is silenced, so
# every receipt the suite reads stays at the acceptance the request committed: what a pass
# establishes is that pick target → adapter surface → preview naming the bound
# configuration → confirm reaches the delivered routes and the receipt names the server's
# answer, with the export-only medium and linkedin previews refused in the server's own
# words. It is NOT evidence that anything was sent anywhere: no run executes here (every
# configured origin is a name, not a destination), and the dispatch-to-terminal half is the
# .NET suites' claim, not this one's.
# ---------------------------------------------------------------------------------------------
suite_adapter_panels() (
  trap 'stop_suite' RETURN
  echo "==> starting a twenty-fourth published application on a template-cloned database at $adapter_panels_base_url"
  start_suite adapter_panels "$adapter_panels_base_url" "$adapter_panels_log_file" \
    "$repo_root/$publish_dir/access-token-adapter-panels" \
    "Chronicleaf__Operations__Enabled=false Chronicleaf__GitHubPages__RepositoryOwner=e2e-owner Chronicleaf__GitHubPages__RepositoryName=e2e-docs Chronicleaf__GitHubPages__Branch=main Chronicleaf__DevTo__Tags=e2e,chronicleaf Chronicleaf__Hashnode__PublicationId=e2e-publication-01 Chronicleaf__Ghost__Origin=https://ghost.example.invalid Chronicleaf__Ghost__ApiVersion=v5.0 Chronicleaf__Ghost__Tags=e2e Chronicleaf__WordPress__Origin=https://wp.example.invalid Chronicleaf__WordPress__Username=e2euser Chronicleaf__CustomBlog__Origin=https://blog.example.invalid Chronicleaf__CustomBlog__Formats=markdown"

  echo "==> seeding one approved story and one sealed publisher credential"
  seed_publish_variant "$adapter_panels_base_url" "$(cat "$repo_root/$publish_dir/access-token-adapter-panels")" "chronicleaf_e2e_adapter_panels"

  echo "==> npm run test:e2e (real browser against $adapter_panels_base_url)"
  run_suite "$adapter_panels_base_url" "$adapter_panels_log_file" "$repo_root/$publish_dir/access-token-adapter-panels" \
    tests/publisher-adapter-panels.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 25: content pack panels. It needs a project with one approved story and one bare
# milestone - and neither a story nor an approval can be produced through the UI without
# a provider, while an approval, once recorded, cannot be un-recorded - so its own
# installation, its own database and its own published instance, for the same reason
# suites 2 to 24 each have one.
#
# Jobs stay enabled for this installation (the default start_suite path), although no
# pack route enqueues one: every derivation below answers 201 synchronously and every
# export serves stored bytes. What a pass establishes is that derive (LinkedIn,
# Xiaohongshu) or select (Medium) → preview → approve → download serves bytes
# byte-equal to the delivered export route, that the native-publish attempt is refused
# in the server's own `publisher_unsupported` words with no account action, that the
# empty states are honest, and that nothing posts anywhere: an armed counter proves no
# POST leaves before its explicit control. It is NOT evidence that anything was sent
# to any pack host: no transport exists, and the suite asserts no request ever aims
# at one.
# ---------------------------------------------------------------------------------------------
suite_content_pack() (
  trap 'stop_suite' RETURN
  echo "==> starting a twenty-fifth published application on a template-cloned database at $content_pack_base_url"
  start_suite content_pack "$content_pack_base_url" "$content_pack_log_file" \
    "$repo_root/$publish_dir/access-token-content-pack"

  echo "==> seeding one approved story and one bare milestone"
  seed_content_pack_panels "$content_pack_base_url" "$(cat "$repo_root/$publish_dir/access-token-content-pack")"

  echo "==> npm run test:e2e (real browser against $content_pack_base_url)"
  run_suite "$content_pack_base_url" "$content_pack_log_file" "$repo_root/$publish_dir/access-token-content-pack" \
    tests/content-pack-panels.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 26: agency area. It needs Client A with two in-window evidenced
# milestones, Client B with one project, and one archived client — and neither
# an assignment nor an archive can be undone through the UI (clearing moves a
# project back to personal but never un-archives a client), while a weekly
# report, once generated, cannot be removed at all — so its own installation,
# its own database and its own published instance, for the same reason suites
# 2 to 25 each have one.
#
# Jobs stay enabled for this installation (the default start_suite path),
# although no agency route enqueues one: client CRUD, assignment, generation,
# approval and export all answer synchronously. What a pass establishes is
# that client CRUD → assign → generate (DST week) → per-project preview →
# approve → Markdown/HTML downloads serve bytes byte-equal to the delivered
# export route; that refusal codes are verbatim (`client_archived`,
# `weekly_report_project_outside_client`, `approval_required`,
# `approval_stale`, `revision_conflict`, `not_found`); that the archived
# client stays readable with assignment disabled; that regeneration appends a
# revision starting unapproved; that evidence drift ends the export until
# re-approval; that empty states are honest; and that nothing posts before its
# control and no request ever leaves loopback. It is NOT evidence of any
# portal, invoicing or email behavior: no such route or control exists.
# ---------------------------------------------------------------------------------------------
suite_agency() (
  trap 'stop_suite' RETURN
  echo "==> starting a twenty-sixth published application on a template-cloned database at $agency_base_url"
  start_suite agency "$agency_base_url" "$agency_log_file" \
    "$repo_root/$publish_dir/access-token-agency"

  echo "==> seeding Client A (two evidenced projects), Client B (one project), one archived client"
  seed_agency_area "$agency_base_url" "$(cat "$repo_root/$publish_dir/access-token-agency")"

  echo "==> npm run test:e2e (real browser against $agency_base_url)"
  run_suite "$agency_base_url" "$agency_log_file" "$repo_root/$publish_dir/access-token-agency" \
    tests/agency-area-ui.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 27: the video storyboard. It needs a project with a confirmed milestone
# carrying an approved story and one selected screenshot - and neither a story
# nor an approval can be produced through the UI without a provider, while a
# script, once generated, cannot be removed at all - so its own installation,
# its own database and its own published instance, for the same reason suites
# 2 to 26 each have one.
#
# Jobs stay enabled for this installation (the default start_suite path),
# although no video-story route enqueues one: generation answers 201
# synchronously and every export serves stored bytes. What a pass establishes
# is that pick duration + source material → Generate → shot-order preview →
# approve → download serves bytes byte-equal to the delivered export route;
# that the pre-approval download and the post-regenerate download are refused
# in the server's own `approval_required`/`approval_stale` words; that the
# native-publish attempt is refused in the server's own `publisher_unsupported`
# words with no account action; that a bare milestone states every empty case
# honestly; and that nothing fires before its explicit control and no request
# ever aims at a video host. It is NOT evidence that any video was
# synthesized, any footage was rendered, or anything was published: no
# renderer, encoder or transport exists, and the suite asserts no request
# ever aims at one.
# ---------------------------------------------------------------------------------------------
suite_video_story() (
  trap 'stop_suite' RETURN
  echo "==> starting a twenty-seventh published application on a template-cloned database at $video_story_base_url"
  start_suite video_story "$video_story_base_url" "$video_story_log_file" \
    "$repo_root/$publish_dir/access-token-video-story"

  echo "==> seeding one approved story with one selected screenshot, plus one bare milestone"
  seed_video_story_panel "$video_story_base_url" "$(cat "$repo_root/$publish_dir/access-token-video-story")"

  echo "==> npm run test:e2e (real browser against $video_story_base_url)"
  run_suite "$video_story_base_url" "$video_story_log_file" "$repo_root/$publish_dir/access-token-video-story" \
    tests/video-story-panel.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 28: workflow wayfinding. It needs one project with a confirmed milestone carrying
# manual evidence, one GitHub connection and two GitHub candidates — and neither a
# connection row nor a candidate row can be produced through the UI without reaching
# GitHub (connecting describes the repository over the real transport; a candidate is
# produced by a signed delivery a page cannot send), so its own installation, its own
# database and its own published instance, for the same reason suites 2 to 27 each have one.
#
# Everything a browser could produce goes through the product's own routes (project,
# milestone, manual evidence). Only the connection and candidate rows are inserted
# directly, counted back afterwards, because no page can hold them: the connection names
# a real public repository as plain text without describing it over any transport, and
# the candidates are the rows two real deliveries would have written. Suite 12 covers
# the real-transport connect; what this seed enables is the panel and the routes.
# What a pass establishes is that every next-step and cross-link lands on its real area
# route with no request fired by navigation; that a candidate row records a
# candidate-status milestone through the delivered import route and replays without
# duplicating; that the schedule form saves and is refused in the server's own words;
# that one capture submission carries two pages the server judges as one job (which on
# this host then fails capture_sandbox_unavailable, named by the panel); that the
# dashboard crumbs reach Team/Tenancy while Settings lists neither, per the recorded
# owner decision; and that nothing fires before its explicit control. It is NOT evidence
# that a page was captured, that GitHub was reached, or that anything was sent anywhere.
# ---------------------------------------------------------------------------------------------
suite_wayfinding() (
  trap 'stop_suite' RETURN
  echo "==> starting a twenty-eighth published application on a template-cloned database at $wayfinding_base_url"
  start_suite wayfinding "$wayfinding_base_url" "$wayfinding_log_file" \
    "$repo_root/$publish_dir/access-token-wayfinding"

  echo "==> seeding one project, one confirmed milestone with evidence, one connection and two candidates"
  seed_workflow_wayfinding "$wayfinding_base_url" "$(cat "$repo_root/$publish_dir/access-token-wayfinding")"

  echo "==> npm run test:e2e (real browser against $wayfinding_base_url)"
  run_suite "$wayfinding_base_url" "$wayfinding_log_file" "$repo_root/$publish_dir/access-token-wayfinding" \
    tests/workflow-wayfinding.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 29: web accessibility roles. It creates projects and confirmed milestones by clicking,
# and a project cannot be removed once it exists - so it runs against its own installation and
# its own published instance rather than against the one suite 1 asserts empty, for the same
# reason suites 2 to 28 each have one. No seed: every record below is created by the same
# clicks an owner would make, and the operations and clients pages are asserted in their
# honest empty states (filters render with no rows).
# ---------------------------------------------------------------------------------------------
suite_a11y_roles() (
  trap 'stop_suite' RETURN
  echo "==> starting a twenty-ninth published application on a template-cloned database at $a11y_roles_base_url"
  start_suite a11y_roles "$a11y_roles_base_url" "$a11y_roles_log_file" \
    "$repo_root/$publish_dir/access-token-a11y-roles"

  echo "==> npm run test:e2e (real browser against $a11y_roles_base_url)"
  run_suite "$a11y_roles_base_url" "$a11y_roles_log_file" "$repo_root/$publish_dir/access-token-a11y-roles" \
    tests/a11y-roles.spec.ts
  stop_suite
)


# ---------------------------------------------------------------------------------------------
# Suite 30: web responsive shell. It creates projects by clicking, and a
# project cannot be removed once it exists - so it runs against its own installation and
# its own published instance rather than against the one suite 1 asserts empty, for the same
# reason suites 2 to 29 each have one. No seed: every record below is created by the same
# clicks an owner would make, and viewports are set per test (360/720/960/1280px).
# ---------------------------------------------------------------------------------------------
suite_responsive() (
  trap 'stop_suite' RETURN
  echo "==> starting a thirtieth published application on a template-cloned database at $responsive_base_url"
  start_suite responsive "$responsive_base_url" "$responsive_log_file" \
    "$repo_root/$publish_dir/access-token-responsive"

  echo "==> npm run test:e2e (real browser against $responsive_base_url)"
  run_suite "$responsive_base_url" "$responsive_log_file" "$repo_root/$publish_dir/access-token-responsive" \
    tests/responsive-shell.spec.ts
  stop_suite
)



# ---------------------------------------------------------------------------------------------
# Parallel runner (PG-ADOPT-004). Each suite above owns its database (a TEMPLATE clone of the
# shared seed), its server, its port, and its token/log files, and runs in its own subshell —
# so suites run N-way parallel without sharing mutable state. Playwright keeps workers: 1
# INSIDE each suite: intra-suite parallelism would break empty-state claims sharing one DB,
# so that knob is a boundary, not a tuning parameter. A failed suite never stops the others:
# statuses land in per-suite files and the aggregate names every failed suite at the end.
# ---------------------------------------------------------------------------------------------
CHRONICLEAF_E2E_JOBS="${CHRONICLEAF_E2E_JOBS:-4}"

SUITES=(
  suite_core
  suite_evidence
  suite_credentials
  suite_voice
  suite_generation
  suite_github
  suite_capture
  suite_drafts
  suite_curation
  suite_timeline
  suite_review
  suite_candidates
  suite_narrative
  suite_variants
  suite_team
  suite_tenancy
  suite_evolution
  suite_gif
  suite_publication
  suite_journey
  suite_area
  suite_publish_variant
  suite_static_blog_export
  suite_adapter_panels
  suite_content_pack
  suite_agency
  suite_video_story
  suite_wayfinding
  suite_a11y_roles
  suite_responsive
)

# PG-TIGHTEN-FU-005. Optional single-suite filter for diagnosis: set
# CHRONICLEAF_E2E_ONLY to one suite name without the `suite_` prefix (e.g.
# `a11y_roles`) and only that suite runs, against its own TEMPLATE clone like
# any other run. Unset, the full 30-suite list runs unchanged — the filter is a
# debugging aid, never the gate's own path.
if [ -n "${CHRONICLEAF_E2E_ONLY:-}" ]; then
  filtered=()
  for suite in "${SUITES[@]}"; do
    if [ "${suite#suite_}" = "$CHRONICLEAF_E2E_ONLY" ]; then
      filtered+=("$suite")
    fi
  done
  if [ "${#filtered[@]}" -eq 0 ]; then
    echo "FAILED: CHRONICLEAF_E2E_ONLY=${CHRONICLEAF_E2E_ONLY} names no suite" >&2
    exit 1
  fi
  SUITES=("${filtered[@]}")
fi

status_dir="$(mktemp -d)"

for suite in "${SUITES[@]}"; do
  echo "==> [${suite#suite_}] starting (log: $publish_dir/playwright-${suite#suite_}.log)"
  ( "$suite" >"$repo_root/$publish_dir/playwright-${suite#suite_}.log" 2>&1; echo "$?" >"$status_dir/$suite" ) &
  while [ "$(jobs -rp | wc -l)" -ge "$CHRONICLEAF_E2E_JOBS" ]; do
    wait -n || true
  done
done
wait || true

failures=0
failed_suites=()
for suite in "${SUITES[@]}"; do
  code="$(cat "$status_dir/$suite" 2>/dev/null || echo MISSING)"
  if [ "$code" != "0" ]; then
    failures=$((failures + 1))
    failed_suites+=("${suite#suite_}(${code})")
  fi
done
rm -rf "$status_dir"

if [ "$failures" -gt 0 ]; then
  echo "FAILED: $failures of ${#SUITES[@]} suite(s) failed: ${failed_suites[*]}" >&2
  echo "per-suite logs are in $publish_dir/playwright-<suite>.log and server-<suite>.log:" >&2
  for failed in "${failed_suites[@]}"; do
    name="${failed%%(*}"
    echo "----- tail of playwright-${name}.log -----" >&2
    tail -n 30 "$repo_root/$publish_dir/playwright-${name}.log" >&2 || true
  done
  exit 1
fi
echo "all ${#SUITES[@]} e2e suites green (jobs=$CHRONICLEAF_E2E_JOBS, one shared PostgreSQL, per-suite TEMPLATE clones)"