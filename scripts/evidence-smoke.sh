#!/usr/bin/env bash
# Container smoke for milestone evidence (MILESTONE-EVIDENCE-001/002).
#
# Builds the real image, runs it against a real disposable PostgreSQL, and drives the private evidence
# routes over HTTP: liveness, readiness, an anonymous read, a signed-in read of a milestone that has no
# evidence, a signed-in manual append, and a read back of exactly the bundle that was committed.
# Nothing is stubbed and no in-memory store is involved: the image is the published application and the
# database is a real PostgreSQL.
#
# This is a smoke, not a substitute for the scenarios in
# tests/Chronicleaf.Integration.Tests/MilestoneEvidenceTests.cs. Those are the assertions; this proves
# the same properties survive a container boundary and a non-root user, and it is the only place the
# evidence routes are exercised through a real image.
#
#   bash scripts/evidence-smoke.sh
#
# The image build needs a package source. In a network-restricted environment the default bridge
# cannot reach nuget.org even when the host can, so pass --network=host; the script does that itself
# through CHRONICLEAF_DOCKER_BUILD_NETWORK.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${CHRONICLEAF_EVIDENCE_SMOKE_IMAGE:-chronicleaf:milestone-evidence}"
build_network="${CHRONICLEAF_DOCKER_BUILD_NETWORK:-host}"
# The container is given the host network, so the port it binds is a host port. It is not 8080: the
# image's default, which is what a deployment would use, is frequently already taken on a developer
# machine, and a 404 from whatever else is listening there would look like a broken image.
app_port="${CHRONICLEAF_EVIDENCE_SMOKE_PORT:-5093}"
origin="http://127.0.0.1:${app_port}"

scratch="$(mktemp -d)"
postgres_container="chronicleaf-evidence-smoke-postgres-$$"
api_container="chronicleaf-evidence-smoke-api-$$"
api_pid=""

cleanup() {
  local status=$?
  if [ -n "$api_pid" ] && kill -0 "$api_pid" 2>/dev/null; then
    kill "$api_pid" 2>/dev/null || true
  fi
  docker rm --force --volumes "$api_container" >/dev/null 2>&1 || true
  docker rm --force --volumes "$postgres_container" >/dev/null 2>&1 || true
  exit "$status"
}
trap cleanup EXIT

require_docker() {
  command -v docker >/dev/null 2>&1 || {
    echo "FAILED (prerequisite unavailable): docker is not on PATH." >&2
    return 1
  }
  docker info >/dev/null 2>&1 || {
    echo "FAILED (prerequisite unavailable): the docker daemon is not reachable." >&2
    return 1
  }
}
require_docker

echo "==> building ${image}"
# The build restores the adopted shared packages from the .nupkg files committed under
# packages/platform-feed, so the image needs no sibling checkout and no environment variable. See
# Dockerfile and Ruling 12 of the workspace ARCHITECTURE.md.
docker build --network="$build_network" -t "$image" . >/dev/null

# One token for this run, hashed for the server and kept in plaintext for the sign-in below. The
# image is started with the hash of exactly this token, so the boundary is configured the way a
# deployment configures it rather than bypassed.
access_token="$(openssl rand -hex 32)"
access_token_hash="$(printf '%s' "$access_token" | sha256sum | cut -d' ' -f1)"

echo "==> starting the disposable PostgreSQL"
docker run --detach --rm \
  --name "$postgres_container" \
  --env POSTGRES_DB=chronicleaf \
  --env POSTGRES_USER=chronicleaf \
  --env POSTGRES_PASSWORD=chronicleaf-smoke \
  --publish 127.0.0.1::5432 \
  postgres:17.6-alpine >/dev/null

postgres_port="$(docker port "$postgres_container" 5432/tcp | head -n 1 | sed 's/.*://')"
for _ in $(seq 1 60); do
  docker exec "$postgres_container" pg_isready --username=chronicleaf --dbname=chronicleaf >/dev/null 2>&1 && break
  sleep 1
done

connection_string="Host=127.0.0.1;Port=${postgres_port};Database=chronicleaf;Username=chronicleaf;Password=chronicleaf-smoke;Timeout=5;Command Timeout=10"

echo "==> applying the committed migrations (the operator command, never start-up)"
ConnectionStrings__Chronicleaf="$connection_string" \
Chronicleaf__AccessTokenHash="$(printf '%s' chronicleaf-evidence-smoke | sha256sum | cut -d' ' -f1)" \
  dotnet ef database update \
    --project src/Chronicleaf.Infrastructure \
    --startup-project src/Chronicleaf.Api \
    --no-build >/dev/null

installation_id="$(cat /proc/sys/kernel/random/uuid)"
project_id="$(cat /proc/sys/kernel/random/uuid)"
milestone_id="$(cat /proc/sys/kernel/random/uuid)"
docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf --quiet --set=ON_ERROR_STOP=1 \
  --command "INSERT INTO installation (id, schema_version, created_at) VALUES ('${installation_id}', 1, now())" >/dev/null
# A real project and a real confirmed milestone, written the way the API would, because evidence
# belongs to a milestone and both tables' foreign keys are enforced.
docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf --quiet --set=ON_ERROR_STOP=1 \
  --command "INSERT INTO projects (id, scope_id, name, description, revision)
             VALUES ('${project_id}', '${installation_id}', 'Smoke', 'Smoke project', 1)" >/dev/null
docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf --quiet --set=ON_ERROR_STOP=1 \
  --command "INSERT INTO milestones
               (id, project_id, title, version, occurred_at, type, source, status, revision, notes,
                known_limitations, next_steps)
             VALUES
               ('${milestone_id}', '${project_id}', 'Smoke milestone', 'v0.1.0', now(), 'feature',
                'manual', 'confirmed', 1, 'Recorded for the smoke.', '{}', '{}')" >/dev/null

# Development, and deliberately so, for the same reason the media smoke does it: the delivered access
# boundary sets Secure on the session cookie everywhere except a loopback public origin in
# Development, so an http-only smoke cannot hold a session in any other environment. The anonymous
# and unauthenticated answers are the same in either environment.
echo "==> starting the image"
docker run --detach --rm \
  --name "$api_container" \
  --network host \
  --env "ASPNETCORE_HTTP_PORTS=${app_port}" \
  --env "ConnectionStrings__Chronicleaf=${connection_string}" \
  --env "Chronicleaf__AccessTokenHash=${access_token_hash}" \
  --env "Chronicleaf__PublicOrigin=${origin}" \
  --env ASPNETCORE_ENVIRONMENT=Development \
  "$image" >/dev/null

health=000
ready=000
for _ in $(seq 1 60); do
  health="$(curl -s -o /dev/null -w '%{http_code}' "${origin}/api/health" 2>/dev/null || echo 000)"
  if [ "$health" = "200" ]; then
    ready="$(curl -s -o /dev/null -w '%{http_code}' "${origin}/api/ready" 2>/dev/null || echo 000)"
    break
  fi
  sleep 1
done

anonymous="$(curl -s -o "$scratch/anonymous.json" -w '%{http_code}' "${origin}/api/milestones/${milestone_id}/evidence")"

echo "    health:    ${health}"
echo "    ready:     ${ready}"
echo "    anonymous: ${anonymous} $(cat "$scratch/anonymous.json")"

[ "$health" = "200" ] || { echo "FAILED: the image never became live" >&2; exit 1; }
[ "$ready" = "200" ] || { echo "FAILED: the image never reported ready" >&2; exit 1; }
[ "$anonymous" = "401" ] || { echo "FAILED: the evidence route answered ${anonymous} without a session" >&2; exit 1; }

jar="$scratch/cookies"

echo "==> signing in and reading the milestone's evidence"
login="$(curl -s -c "$jar" -o /dev/null -w '%{http_code}' \
  -X POST "${origin}/api/session" \
  --header 'Content-Type: application/json' \
  --header "Origin: ${origin}" \
  --data "{\"accessToken\":\"${access_token}\"}")"
echo "    sign-in: ${login}"
# 204 is the delivered answer for the session exchange: the cookie is the whole response body.
[ "$login" = "204" ] || { echo "FAILED: the sign-in was refused (${login})" >&2; exit 1; }

empty_read="$(curl -s -b "$jar" -o "$scratch/empty.json" -w '%{http_code}' \
  "${origin}/api/milestones/${milestone_id}/evidence")"
echo "    empty read: ${empty_read}"
echo "    revision:   $(python3 -c "import json; print(json.load(open('$scratch/empty.json'))['revision'])")"
echo "    bundles:    $(python3 -c "import json; print(len(json.load(open('$scratch/empty.json'))['items']))")"

# The response names its own header, so the smoke uses the delivered value rather than a copy of it.
antiforgery="$(curl -s -b "$jar" "${origin}/api/session/antiforgery" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')"

echo "==> appending a manual limitation through the container"
append="$(curl -s -b "$jar" -o "$scratch/appended.json" -w '%{http_code}' \
  -X POST "${origin}/api/milestones/${milestone_id}/evidence/manual" \
  --header 'Content-Type: application/json' \
  --header "Origin: ${origin}" \
  --header "X-Chronicleaf-Antiforgery: ${antiforgery}" \
  --header 'If-Match: 0' \
  --data '{"notes":"Dates arrive in the browser locale.","knownLimitations":["No undo yet."],"nextSteps":[],"expectedRevision":0}')"
echo "    append: ${append}"
cat "$scratch/appended.json"
echo

[ "$append" = "201" ] || { echo "FAILED: the manual append was not accepted" >&2; exit 1; }

echo "==> a stale append is refused with the delivered envelope"
stale="$(curl -s -b "$jar" -o "$scratch/stale.json" -w '%{http_code}' \
  -X POST "${origin}/api/milestones/${milestone_id}/evidence/manual" \
  --header 'Content-Type: application/json' \
  --header "Origin: ${origin}" \
  --header "X-Chronicleaf-Antiforgery: ${antiforgery}" \
  --header 'If-Match: 0' \
  --data '{"notes":"Written against an old revision.","expectedRevision":0}')"
echo "    stale append: ${stale} $(cat "$scratch/stale.json")"

echo "==> reading the evidence back"
readback="$(curl -s -b "$jar" -o "$scratch/readback.json" -w '%{http_code}' \
  "${origin}/api/milestones/${milestone_id}/evidence")"
echo "    read back: ${readback}"
echo "    revision:  $(python3 -c "import json; print(json.load(open('$scratch/readback.json'))['revision'])")"
echo "    bundles:   $(python3 -c "import json; print(len(json.load(open('$scratch/readback.json'))['items']))")"
echo "    items:     $(python3 -c "import json; print(len(json.load(open('$scratch/readback.json'))['items'][0]['items']))")"

echo "==> the append-only triggers hold against a direct write"
docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf --quiet \
  --command "UPDATE evidence_items SET text = 'rewritten';" 2>&1 | sed 's/^/      /' || true

still_intact="$(docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf --quiet \
  --tuples-only --no-align --command "SELECT text FROM evidence_items WHERE kind = 'limitations'")"

if [ "$readback" = "200" ] \
  && [ "$stale" = "409" ] \
  && [ "$login" = "204" ] \
  && [ -n "$still_intact" ] \
  && ! printf '%s' "$still_intact" | grep -q rewritten; then
  echo "evidence smoke passed"
else
  echo "FAILED: the container evidence smoke did not hold" >&2
  exit 1
fi
