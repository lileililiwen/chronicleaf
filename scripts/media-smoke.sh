#!/usr/bin/env bash
# Container smoke for the local media store (LOCAL-MEDIA-STORAGE-001/002).
#
# Builds the real image, runs it against a real disposable PostgreSQL, and drives the private media
# routes over HTTP: liveness, readiness, an anonymous read, a signed-in write of a real PNG through a
# configured MediaRoot, and a read of exactly the bytes that were written. Nothing is stubbed and no
# in-memory provider is involved: the image is the published application, the database is a real
# PostgreSQL, and the MediaRoot is a bind-mounted host directory whose files are inspected afterwards.
#
# This is a smoke, not a substitute for the scenarios in
# tests/Chronicleaf.Integration.Tests/LocalMediaStorageTests.cs. Those are the assertions; this proves
# the same properties survive a container boundary, a non-root user and a mounted volume.
#
#   bash scripts/media-smoke.sh
#
# The image build needs a package source. In a network-restricted environment the default bridge
# cannot reach nuget.org even when the host can, so pass --network=host; the script does that itself
# when CHRONICLEAF_DOCKER_NETWORK says so.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${CHRONICLEAF_SMOKE_IMAGE:-chronicleaf:local-media}"
build_network="${CHRONICLEAF_DOCKER_BUILD_NETWORK:-host}"
# The container is given the host network, so the port it binds is a host port. It is not 8080:
# the image's default, which is what a deployment would use, is frequently already taken on a
# developer machine, and a 404 from whatever else is listening there would look like a broken image.
app_port="${CHRONICLEAF_SMOKE_PORT:-5092}"
origin="http://127.0.0.1:${app_port}"

scratch="$(mktemp -d)"
# The image runs as its own non-root user, and a host directory created by mktemp is 0700 and owned
# by the host user, so the container could not write into it. A deployment mounting a real volume has
# the same requirement: the volume must be writable by the runtime user, which is why the Dockerfile
# hands /app/runtime to it and documents the mount.
mkdir -p "$scratch/media"
chmod 0777 "$scratch/media"
postgres_container="chronicleaf-media-smoke-postgres-$$"
api_container="chronicleaf-media-smoke-api-$$"
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
# The build restores the adopted shared storage from the .nupkg files committed under
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
Chronicleaf__AccessTokenHash="$(printf '%s' chronicleaf-media-smoke | sha256sum | cut -d' ' -f1)" \
  dotnet ef database update \
    --project src/Chronicleaf.Infrastructure \
    --startup-project src/Chronicleaf.Api \
    --no-build >/dev/null

installation_id="$(cat /proc/sys/kernel/random/uuid)"
project_id="$(cat /proc/sys/kernel/random/uuid)"
docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf --quiet --set=ON_ERROR_STOP=1 \
  --command "INSERT INTO installation (id, schema_version, created_at) VALUES ('${installation_id}', 1, now())" >/dev/null
# A real project row, written the way an operator or the API would, because the media table's foreign
# key to projects is enforced and media cannot name a project that does not exist.
docker exec "$postgres_container" psql --username=chronicleaf --dbname=chronicleaf --quiet --set=ON_ERROR_STOP=1 \
  --command "INSERT INTO projects (id, scope_id, name, description, revision)
             VALUES ('${project_id}', '${installation_id}', 'Smoke', 'Smoke project', 1)" >/dev/null

# Development, and deliberately so. The delivered access boundary sets Secure on the session cookie
# everywhere except a loopback public origin in Development - the single relaxation the design grants
# - so an http-only smoke cannot hold a session in any other environment. Nothing about the media
# store depends on it: health 200, ready 200 and anonymous media 401 are observed under the image's
# default environment as well.
echo "==> starting the image with a configured MediaRoot on a mounted volume"
docker run --detach --rm \
  --name "$api_container" \
  --network host \
  --env "ASPNETCORE_HTTP_PORTS=${app_port}" \
  --env "ConnectionStrings__Chronicleaf=${connection_string}" \
  --env "Chronicleaf__AccessTokenHash=${access_token_hash}" \
  --env "Chronicleaf__PublicOrigin=${origin}" \
  --env ASPNETCORE_ENVIRONMENT=Development \
  --env Chronicleaf__MediaRoot=/app/runtime/media \
  --volume "$scratch/media:/app/runtime/media" \
  "$image" >/dev/null

health=000
for _ in $(seq 1 60); do
  health="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${app_port}/api/health" 2>/dev/null || echo 000)"
  [ "$health" = "200" ] && break
  sleep 1
done

echo "    health:    ${health}"
echo "    ready:     $(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${app_port}/api/ready")"
echo "    anonymous: $(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${app_port}/api/media/$(cat /proc/sys/kernel/random/uuid)")"

[ "$health" = "200" ] || { echo "FAILED: the image never became live" >&2; exit 1; }

jar="$scratch/cookies"

echo "==> signing in and writing a real PNG through the container"
login="$(curl -s -c "$jar" -o /dev/null -w '%{http_code}' \
  -X POST "http://127.0.0.1:${app_port}/api/session" \
  --header 'Content-Type: application/json' \
  --header "Origin: ${origin}" \
  --data "{\"accessToken\":\"${access_token}\"}")"
echo "    sign-in: ${login}"

# The response names its own header, so the smoke uses the delivered value rather than a copy of it.
antiforgery="$(curl -s -b "$jar" "http://127.0.0.1:${app_port}/api/session/antiforgery" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')"

# A genuine PNG: signature, IHDR with real dimensions, a zlib-compressed IDAT and an empty IEND.
python3 - "$scratch/image.png" <<'PY'
import struct
import sys
import zlib

def chunk(kind: bytes, data: bytes) -> bytes:
    body = kind + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

width = height = 6
raw = b"".join(
    b"\x00" + bytes((x * 7 + y * 13) % 256 for x in range(width))
    for y in range(height)
)

png = (
    b"\x89PNG\r\n\x1a\n"
    + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0))
    + chunk(b"IDAT", zlib.compress(raw, 9))
    + chunk(b"IEND", b"")
)

with open(sys.argv[1], "wb") as target:
    target.write(png)
PY

expected_hash="$(sha256sum "$scratch/image.png" | cut -d' ' -f1)"
expected_length="$(stat -c%s "$scratch/image.png")"

upload="$(curl -s -b "$jar" -o "$scratch/upload.json" -w '%{http_code}' \
  -X POST "http://127.0.0.1:${app_port}/api/media?projectId=${project_id}" \
  --header 'Content-Type: image/png' \
  --header "Origin: ${origin}" \
  --header "X-Chronicleaf-Antiforgery: ${antiforgery}" \
  --data-binary "@$scratch/image.png")"
echo "    upload:   ${upload}"
cat "$scratch/upload.json"
echo

[ "$upload" = "201" ] || { echo "FAILED: the media upload was not accepted" >&2; exit 1; }

media_id="$(python3 -c "import json; print(json.load(open('$scratch/upload.json'))['id'])")"
stored_hash="$(python3 -c "import json; print(json.load(open('$scratch/upload.json'))['sha256'])")"
stored_length="$(python3 -c "import json; print(json.load(open('$scratch/upload.json'))['byteLength'])")"
stored_type="$(python3 -c "import json; print(json.load(open('$scratch/upload.json'))['mediaType'])")"

echo "    mediaType:    ${stored_type}"
echo "    sha256 match: $([ "$stored_hash" = "$expected_hash" ] && echo yes || echo no)"
echo "    length match: $([ "$stored_length" = "$expected_length" ] && echo yes || echo no)"

read_code="$(curl -s -b "$jar" -o "$scratch/read.png" -w '%{http_code}' \
  "http://127.0.0.1:${app_port}/api/media/${media_id}")"
echo "    read:      ${read_code}"
echo "    identical:  $(cmp -s "$scratch/read.png" "$scratch/image.png" && echo yes || echo no)"

echo "    files under the mounted MediaRoot:"
find "$scratch/media" -type f | sed "s|$scratch/media|MediaRoot|" | sed 's/^/      /'

# A non-PNG is refused with the delivered envelope, and nothing is written for it.
refused="$(curl -s -b "$jar" -o "$scratch/refused.json" -w '%{http_code}' \
  -X POST "http://127.0.0.1:${app_port}/api/media?projectId=${project_id}" \
  --header 'Content-Type: text/plain' \
  --header "Origin: ${origin}" \
  --header "X-Chronicleaf-Antiforgery: ${antiforgery}" \
  --data-binary 'not an image')"
echo "    non-png:  ${refused} $(cat "$scratch/refused.json")"

if cmp -s "$scratch/read.png" "$scratch/image.png" \
  && [ "$read_code" = "200" ] \
  && [ "$stored_hash" = "$expected_hash" ] \
  && [ "$refused" = "415" ] \
  && [ -n "$(find "$scratch/media" -type f -print -quit)" ]; then
  echo "media smoke passed"
else
  echo "FAILED: the container media smoke did not hold" >&2
  exit 1
fi
