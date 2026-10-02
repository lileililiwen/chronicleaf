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
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_dir="${CHRONICLEAF_PUBLISH_DIR:-dist/e2e}"
base_url="${CHRONICLEAF_BASE_URL:-http://127.0.0.1:5081}"
log_file="$repo_root/$publish_dir/server.log"
token_file="$repo_root/$publish_dir/access-token"
database_name="chronicleaf_e2e"

# The image is pinned; a floating tag would make this run unreproducible.
postgres_image="postgres:17.6-alpine"
postgres_container="chronicleaf-e2e-postgres-$$"
postgres_port=""

cleanup() {
  local exit_status=$?
  if [ -n "$server_pid" ] && kill -0 "$server_pid" 2>/dev/null; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  if [ -n "$postgres_container" ]; then
    docker rm --force --volumes "$postgres_container" >/dev/null 2>&1 || true
  fi
  # The token is a secret for the lifetime of this run and must not survive it.
  rm -f "$token_file"
  exit "$exit_status"
}
server_pid=""
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

bash "$repo_root/scripts/publish.sh" "$publish_dir" Release

echo "==> starting the disposable PostgreSQL for this run"
docker run --detach --rm \
  --name "$postgres_container" \
  --env POSTGRES_DB="$database_name" \
  --env POSTGRES_USER=chronicleaf \
  --env POSTGRES_PASSWORD=chronicleaf-e2e \
  --publish 127.0.0.1::5432 \
  "$postgres_image" >/dev/null

postgres_port="$(docker port "$postgres_container" 5432/tcp | head -n 1 | sed 's/.*://')"
if [ -z "$postgres_port" ]; then
  echo "the disposable PostgreSQL did not publish a port:" >&2
  docker logs "$postgres_container" >&2 || true
  exit 1
fi

for _ in $(seq 1 60); do
  if docker exec "$postgres_container" pg_isready --username=chronicleaf --dbname="$database_name" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

connection_string="Host=127.0.0.1;Port=${postgres_port};Database=${database_name};Username=chronicleaf;Password=chronicleaf-e2e;Timeout=5;Command Timeout=10"

echo "==> applying the committed migrations (the operator command, never start-up)"
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
echo "==> provisioning the single installation"
installation_id="$(cat /proc/sys/kernel/random/uuid)"
docker exec "$postgres_container" psql --username=chronicleaf --dbname="$database_name" --quiet --set=ON_ERROR_STOP=1 \
  --command "INSERT INTO installation (id, schema_version, created_at)
             VALUES ('${installation_id}', 1, now())
             ON CONFLICT (id) DO NOTHING;" >/dev/null

# A token for this run only: 32 random bytes, hashed for the server and kept for the fixtures.
access_token="$(openssl rand -hex 32)"
access_token_hash="$(printf '%s' "$access_token" | sha256sum | cut -d' ' -f1)"
printf '%s' "$access_token" >"$token_file"

# BACKGROUND-JOBS-001/002. The operations panel can only report operations that exist, and the only
# honest way to give the browser fixture some is to write real rows into the real database - the same
# way the integration scenarios do. Nothing here is simulated for the UI's benefit and no row invents
# a job that was never asked for: each is a genuine (scope, kind, key) submission the panel can read
# back through the real private route, and the queued rows carry no outbox event on purpose so the
# running application does not execute them and change what the fixture is asserting.
echo "==> recording the background operations the panel fixture reads"
queued_operation="$(cat /proc/sys/kernel/random/uuid)"
cancelled_operation="$(cat /proc/sys/kernel/random/uuid)"
failed_operation="$(cat /proc/sys/kernel/random/uuid)"
succeeded_operation="$(cat /proc/sys/kernel/random/uuid)"
now="$(date -u +%Y-%m-%dT%H:%M:%S.000000Z)"
docker exec "$postgres_container" psql --username=chronicleaf --dbname="$database_name" --quiet --set=ON_ERROR_STOP=1 \
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

echo "==> starting the published application at $base_url"
# exec replaces the subshell, so $! is the server process itself and the trap below
# actually stops it instead of orphaning it on the port.
(
  cd "$repo_root/$publish_dir" && exec env \
    ASPNETCORE_URLS="$base_url" \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Chronicleaf="$connection_string" \
    Chronicleaf__AccessTokenHash="$access_token_hash" \
    Chronicleaf__PublicOrigin="$base_url" \
    dotnet Chronicleaf.Api.dll >"$log_file" 2>&1
) &
server_pid=$!

ready=0
for _ in $(seq 1 60); do
  if curl --fail --silent --show-error --max-time 2 "$base_url/api/health" >/dev/null 2>&1; then
    ready=1
    break
  fi
  if ! kill -0 "$server_pid" 2>/dev/null; then
    echo "the published application exited before becoming ready:" >&2
    cat "$log_file" >&2
    exit 1
  fi
  sleep 1
done

if [ "$ready" -ne 1 ]; then
  echo "the published application did not answer /api/health within 60s:" >&2
  cat "$log_file" >&2
  exit 1
fi

echo "==> npm run test:e2e (real browser against $base_url)"
# The token reaches the fixtures through the environment only. It is not printed by this script
# and it is deleted from disk when the run ends.
(
  cd "$repo_root/src/Chronicleaf.Web" && \
    CHRONICLEAF_BASE_URL="$base_url" \
    CHRONICLEAF_E2E_ACCESS_TOKEN="$(cat "$token_file")" \
    CHRONICLEAF_E2E_QUEUED_OPERATION="$queued_operation" \
    npm run test:e2e
)
