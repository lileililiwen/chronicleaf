#!/usr/bin/env bash
# Persistent acceptance-review instance (NOT a verification run).
# Mirrors scripts/verify-community.sh bring-up/migrate/provision, but stays up
# for human audit instead of tearing down. Secrets live in scratch (default
# /tmp/opencode/chronicleaf-review, override with CHRONICLEAF_REVIEW_SCRATCH),
# never in the repository.
#
# Usage: bash scripts/review-up.sh
# Stop:  docker compose -p chronicleaf-review \
#          --env-file /tmp/opencode/chronicleaf-review/env \
#          -f compose.yaml -f /tmp/opencode/chronicleaf-review/override.yaml \
#          down --volumes
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
scratch="${CHRONICLEAF_REVIEW_SCRATCH:-/tmp/opencode/chronicleaf-review}"
mkdir -p "$scratch"
log="$scratch/bring-up.log"
exec > >(tee "$log") 2>&1

project="chronicleaf-review"
app_port="${CHRONICLEAF_REVIEW_PORT:-5080}"
origin="http://127.0.0.1:${app_port}"
image="chronicleaf:community"

echo "==> building ${image}"
docker build --network=host -t "$image" .

if [ ! -f "$scratch/env" ]; then
  echo "==> operator values (this review instance only, never committed)"
  db_password="$(openssl rand -hex 24)"
  access_token="$(openssl rand -hex 32)"
  access_token_hash="$(printf '%s' "$access_token" | sha256sum | cut -d' ' -f1)"
  openssl rand -out "$scratch/encryption.key" 32
  chmod 600 "$scratch/encryption.key"
  cat >"$scratch/env" <<EOF
CHRONICLEAF_APP_PORT=$app_port
CHRONICLEAF_DB_PASSWORD=$db_password
CHRONICLEAF_ACCESS_TOKEN_HASH=$access_token_hash
CHRONICLEAF_PUBLIC_ORIGIN=$origin
CHRONICLEAF_ENCRYPTION_KEY_FILE=$scratch/encryption.key
EOF
  echo "$access_token" > "$scratch/setup-token.txt"
  chmod 600 "$scratch/env" "$scratch/setup-token.txt"
fi

cat >"$scratch/override.yaml" <<EOF
services:
  postgres:
    ports:
      - "127.0.0.1::5432"
EOF

compose() { docker compose -p "$project" --env-file "$scratch/env" -f compose.yaml -f "$scratch/override.yaml" "$@"; }

echo "==> compose bring-up"
compose up -d --build
for _ in $(seq 1 60); do
  compose exec -T postgres pg_isready -U chronicleaf -d chronicleaf >/dev/null 2>&1 && break
  sleep 1
done
compose exec -T postgres pg_isready -U chronicleaf -d chronicleaf

set -a; . "$scratch/env"; set +a
postgres_port="$(compose port postgres 5432 | head -n 1 | sed 's/.*://')"
connection_string="Host=127.0.0.1;Port=${postgres_port};Database=chronicleaf;Username=chronicleaf;Password=${CHRONICLEAF_DB_PASSWORD};Timeout=5;Command Timeout=10"

echo "==> explicit operator migrate"
ConnectionStrings__Chronicleaf="$connection_string" \
Chronicleaf__AccessTokenHash="$CHRONICLEAF_ACCESS_TOKEN_HASH" \
  dotnet ef database update \
    --project src/Chronicleaf.Infrastructure \
    --startup-project src/Chronicleaf.Api

echo "==> provision singleton installation"
installation_id="$(cat /proc/sys/kernel/random/uuid)"
compose exec -T postgres psql --username=chronicleaf --dbname=chronicleaf --quiet --set=ON_ERROR_STOP=1 \
  --command "INSERT INTO installation (id, schema_version, created_at)
             VALUES ('${installation_id}', 1, now())
             ON CONFLICT (id) DO NOTHING;"

echo "==> readiness"
for _ in $(seq 1 60); do
  health="$(curl -s -o /dev/null -w '%{http_code}' "${origin}/api/health" 2>/dev/null || echo 000)"
  [ "$health" = "200" ] && break
  sleep 1
done
[ "$health" = "200" ] || { echo "HEALTH CHECK FAILED: $health"; exit 1; }
ready="$(curl -s -o /dev/null -w '%{http_code}' "${origin}/api/ready" 2>/dev/null || echo 000)"
[ "$ready" = "200" ] || { echo "READINESS CHECK FAILED: $ready"; exit 1; }

echo "REVIEW READY: $origin (health=200 ready=200)"
echo "Setup token is in $scratch/setup-token.txt (exchange once for a session per docs/community-quickstart.md)"
