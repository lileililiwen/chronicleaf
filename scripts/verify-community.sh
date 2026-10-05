#!/usr/bin/env bash
# Community release verification (COMMUNITY-COMPOSE-RELEASE-001/002).
#
# The two-service install, proved rather than described: static packaging checks, container
# build, Compose bring-up, the explicit operator migrate, readiness, a fixture-driven MVP
# smoke (project, confirmed milestone, manual evidence, timeline, the request-time capture
# refusal), a public capture that must fail visibly on this host, restart persistence, and
# backup/restore through destroyed volumes with identical re-reads afterwards.
#
# What this script does NOT claim: a real page photographed (this host cannot confine a
# browser, so the public capture job must fail with capture_sandbox_unavailable - asserted),
# a real provider call (credential-gated live smoke is opt-in per design and is recorded as
# not performed), or an authenticated GitHub collection (no PAT here; recorded, not driven).
# The scripted-provider draft/approve/export chain is proven natively by
# CommunityComposeReleaseTests; this script proves the same routes serve it from the real
# image.
#
# Usage: bash scripts/verify-community.sh
# Environment: docker with Compose v2, dotnet SDK 10 with the ef tool, python3, curl,
# openssl. Any unavailable prerequisite fails loudly.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

image="${CHRONICLEAF_COMMUNITY_IMAGE:-chronicleaf:community}"
project="chronicleaf-verify-$$"
# A free loopback port per run: the daemon is shared with other verification runs, so a
# fixed host port collides. The production default stays 5080 (see compose.yaml).
app_port="$(python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')"
origin="http://127.0.0.1:${app_port}"
postgres_image="postgres:17.6-alpine@sha256:ef257d85f76e48da1c64832459b59fcaba1a4dac97bf5d7450c77753542eee94"
scratch="$(mktemp -d)"
evidence="$scratch/release-evidence.txt"
cleanup_done=0

cleanup() {
  local status=$?
  if [ "$cleanup_done" = "0" ]; then
    cleanup_done=1
    docker compose -p "$project" --env-file "$scratch/env" -f compose.yaml -f "$scratch/override.yaml" down --volumes >/dev/null 2>&1 || true
    rm -rf "$scratch"
  fi
  exit "$status"
}
trap cleanup EXIT

record() { echo "$*" | tee -a "$evidence"; }
fail() { record "FAILED: $*"; return 1; }

command -v docker >/dev/null 2>&1 || { echo "FAILED (prerequisite unavailable): docker is not on PATH." >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo "FAILED (prerequisite unavailable): the docker daemon is not reachable." >&2; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "FAILED (prerequisite unavailable): docker compose v2 is required." >&2; exit 1; }
command -v python3 >/dev/null 2>&1 || { echo "FAILED (prerequisite unavailable): python3 is required for the fixture smoke." >&2; exit 1; }

echo "==> static packaging checks"
touch "$scratch/dummy.key"
dummy_env="CHRONICLEAF_DB_PASSWORD=dummy CHRONICLEAF_ACCESS_TOKEN_HASH=dummy CHRONICLEAF_PUBLIC_ORIGIN=dummy CHRONICLEAF_ENCRYPTION_KEY_FILE=$scratch/dummy.key"
# shellcheck disable=SC2086
services="$(env $dummy_env docker compose -p "$project" -f compose.yaml config --services 2>/dev/null | sort | tr '\n' ' ')"
[ "$services" = "app postgres " ] || fail "compose must describe exactly app and postgres, got: ${services}"
record "services: ${services}(no third runtime)"

echo "==> building ${image}"
docker build --network=host -t "$image" . >/dev/null
[ -n "$(docker images -q "$image")" ] || fail "image build produced no image"
record "image: $(docker image inspect "$image" --format='{{.Config.User}}' | sed 's/^/user=/') digest=$(docker image inspect "$image" --format='{{.Id}}')"
docker run --rm --entrypoint sh "$image" -c 'command -v curl' >/dev/null \
  || fail "image lacks curl for the Compose healthcheck"

echo "==> dotnet build (the operator migrate below runs --no-build)"
dotnet build Chronicleaf.sln --nologo >/dev/null || fail "dotnet build failed"

echo "==> operator values (this run only, never committed)"
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
# Test-only port mapping for the operator migrate/restore path. The production
# compose.yaml exposes no database port; this override exists only in $scratch.
cat >"$scratch/override.yaml" <<EOF
services:
  postgres:
    ports:
      - "127.0.0.1::5432"
EOF

compose() { docker compose -p "$project" --env-file "$scratch/env" -f compose.yaml -f "$scratch/override.yaml" "$@"; }

echo "==> compose bring-up (two services, nothing else)"
compose up -d --build >/dev/null
[ "$(compose ps --services | sort | tr '\n' ' ')" = "app postgres " ] \
  || fail "running services are not exactly app and postgres"
for _ in $(seq 1 60); do
  compose exec -T postgres pg_isready -U chronicleaf -d chronicleaf >/dev/null 2>&1 && break
  sleep 1
done
compose exec -T postgres pg_isready -U chronicleaf -d chronicleaf >/dev/null 2>&1 \
  || fail "postgres did not become ready"

postgres_port="$(compose port postgres 5432 | head -n 1 | sed 's/.*://')"
connection_string="Host=127.0.0.1;Port=${postgres_port};Database=chronicleaf;Username=chronicleaf;Password=${db_password};Timeout=5;Command Timeout=10"

echo "==> explicit operator migrate (never on HTTP startup)"
ConnectionStrings__Chronicleaf="$connection_string" \
Chronicleaf__AccessTokenHash="$access_token_hash" \
  dotnet ef database update \
    --project src/Chronicleaf.Infrastructure \
    --startup-project src/Chronicleaf.Api \
    --no-build >/dev/null \
  || fail "operator migrate failed"

echo "==> operator-provision the singleton installation (operator state, never self-claimed)"
installation_id="$(cat /proc/sys/kernel/random/uuid)"
compose exec -T postgres psql --username=chronicleaf --dbname=chronicleaf --quiet --set=ON_ERROR_STOP=1 \
  --command "INSERT INTO installation (id, schema_version, created_at)
             VALUES ('${installation_id}', 1, now())
             ON CONFLICT (id) DO NOTHING;" >/dev/null \
  || fail "installation provisioning failed"

echo "==> readiness: /api/health alive, /api/ready usable"
health=000
for _ in $(seq 1 60); do
  health="$(curl -s -o /dev/null -w '%{http_code}' "${origin}/api/health" 2>/dev/null || echo 000)"
  [ "$health" = "200" ] && break
  sleep 1
done
[ "$health" = "200" ] || fail "/api/health is ${health}, not 200"
ready="$(curl -s -o /dev/null -w '%{http_code}' "${origin}/api/ready" 2>/dev/null || echo 000)"
[ "$ready" = "200" ] || fail "/api/ready is ${ready}, not 200"
record "readiness: health=200 ready=200"

echo "==> fixture MVP smoke through the real image"
export CHRONICLEAF_SMOKE_ORIGIN="$origin" CHRONICLEAF_SMOKE_TOKEN="$access_token"
smoke_out="$(python3 - "$scratch/smoke.json" <<'PYEOF'
import json, os, sys, urllib.request, urllib.error

origin = os.environ["CHRONICLEAF_SMOKE_ORIGIN"]
out_path = sys.argv[1]
state = {}

class Client:
    def __init__(self):
        # Cookies are carried explicitly, the way the delivered OwnerClient test fixture
        # does it: the session cookie is Secure in Production and this smoke speaks plain
        # HTTP to loopback, so a policy-following jar would hold the session and never
        # send it back. Nothing about the boundary is bypassed - the cookie value is the
        # one the server set on sign-in.
        self.opener = urllib.request.build_opener()
        self.cookie = None

    def remember(self, set_cookie):
        if set_cookie is not None:
            self.cookie = set_cookie.split(";")[0]

    def call(self, method, path, body=None, revision=None, expect=(200,)):
        data = json.dumps(body if body is not None else {}).encode()
        req = urllib.request.Request(origin + path, data=data if method != "GET" else None, method=method)
        req.add_header("Accept", "application/json")
        if self.cookie is not None:
            req.add_header("Cookie", self.cookie)
        if method != "GET":
            req.add_header("Content-Type", "application/json")
            req.add_header("Origin", origin)
            # Sign-in itself carries no antiforgery value: the session it creates is what
            # the antiforgery endpoint requires, so fetching one first is a 401.
            if path != "/api/session":
                token = self.get("/api/session/antiforgery")["token"]
                req.add_header("X-Chronicleaf-Antiforgery", token)
        if revision is not None:
            req.add_header("If-Match", str(revision))
        try:
            with self.opener.open(req) as res:
                self.remember(res.headers.get("Set-Cookie"))
                raw = res.read().decode()
                return res.status, (json.loads(raw) if raw else None)
        except urllib.error.HTTPError as e:
            raw = e.read().decode()
            try:
                parsed = json.loads(raw) if raw else None
            except json.JSONDecodeError:
                parsed = {"_raw": raw}
            if e.code not in expect:
                raise AssertionError(f"{method} {path} -> {e.code}, expected {expect}: {raw[:300]}")
            return e.code, parsed

    def get(self, path, expect=(200,)):
        status, parsed = self.call("GET", path, expect=expect)
        assert status in expect, (path, status)
        return parsed

c = Client()
# Sign in for real: the hash in the env file matches this token, nothing is bypassed.
status, _ = c.call("POST", "/api/session", {"accessToken": os.environ["CHRONICLEAF_SMOKE_TOKEN"]})
assert status in (200, 204), status
assert c.cookie is not None and c.cookie.startswith("chronicleaf_session=") and len(c.cookie) > len("chronicleaf_session="), "sign-in set no session cookie"
# Probe the session itself before any mutation: a 200 here names the owner, proving the
# cookie the server just set round-trips back to the same session.
me = c.get("/api/session")
assert "actorId" in me, me
project = c.call("POST", "/api/projects", {"name": "Release 项目", "description": "Release 说明。"})[1]
state["projectId"] = project["id"]
ms = c.call("POST", f"/api/projects/{state['projectId']}/milestones",
            {"title": "第一次可复现发布", "version": "0.1.0", "occurredAt": "2026-10-01T09:00:00Z",
             "type": "feature", "notes": "记录。", "knownLimitations": [], "nextSteps": []})[1]
assert ms["status"] == "confirmed", ms
state["milestoneId"] = ms["id"]
c.call("POST", f"/api/milestones/{state['milestoneId']}/evidence/manual",
       {"notes": "The pipeline now reproduces from a clean checkout.", "expectedRevision": 0},
       revision=0, expect=(201,))
bundle = c.get(f"/api/milestones/{state['milestoneId']}/evidence")["items"][0]
state["bundleRevision"] = bundle["revision"]
assert bundle["revision"] >= 1
timeline = c.get(f"/api/projects/{state['projectId']}/timeline")
assert any(i["id"] == state["milestoneId"] and i["status"] == "confirmed" for i in timeline["items"]), timeline
state["timeline"] = timeline
# A private destination is refused before any job exists: the honest half of capture.
_, refused = c.call("POST", f"/api/milestones/{state['milestoneId']}/captures",
    {"url": "http://[::ffff:10.0.0.5]/admin", "viewports": ["desktop"], "waitMs": 0,
     "fullPage": False, "expectedBundleRevision": None, "idempotencyKey": "release-smoke-1"},
    revision=0, expect=(403,))
assert refused["code"] == "capture_target_blocked", refused
state["captureRefusal"] = refused["code"]

with open(out_path, "w") as f:
    json.dump(state, f)
print("SMOKE_OK project=%s milestone=%s bundle=%s" % (state["projectId"], state["milestoneId"], state["bundleRevision"]))
PYEOF
)" || fail "fixture smoke failed"
record "smoke: $smoke_out"
[ -f "$scratch/smoke.json" ] || fail "smoke produced no state file"

echo "==> public capture on this host must fail visibly, never silently"
export CHRONICLEAF_SMOKE_STATE="$scratch/smoke.json"
capture_out="$(python3 - <<'PYEOF'
import json, os, time, urllib.request, urllib.error

origin = os.environ["CHRONICLEAF_SMOKE_ORIGIN"]
state = json.load(open(os.environ["CHRONICLEAF_SMOKE_STATE"]))

class Client:
    def __init__(self):
        # Cookies are carried explicitly, the way the delivered OwnerClient test fixture
        # does it: the session cookie is Secure in Production and this smoke speaks plain
        # HTTP to loopback, so a policy-following jar would hold the session and never
        # send it back. Nothing about the boundary is bypassed - the cookie value is the
        # one the server set on sign-in.
        self.opener = urllib.request.build_opener()
        self.cookie = None

    def remember(self, set_cookie):
        if set_cookie is not None:
            self.cookie = set_cookie.split(";")[0]
    def call(self, method, path, body=None, revision=None):
        data = json.dumps(body if body is not None else {}).encode()
        req = urllib.request.Request(origin + path, data=data if method != "GET" else None, method=method)
        req.add_header("Accept", "application/json")
        if self.cookie is not None:
            req.add_header("Cookie", self.cookie)
        if method != "GET":
            req.add_header("Content-Type", "application/json")
            req.add_header("Origin", origin)
            # Sign-in itself carries no antiforgery value (see the smoke client above).
            if path != "/api/session":
                areq = urllib.request.Request(origin + "/api/session/antiforgery")
                if self.cookie is not None:
                    areq.add_header("Cookie", self.cookie)
                with self.opener.open(areq) as r:
                    token = json.loads(r.read().decode())["token"]
                req.add_header("X-Chronicleaf-Antiforgery", token)
        if revision is not None:
            req.add_header("If-Match", str(revision))
        try:
            with self.opener.open(req) as res:
                self.remember(res.headers.get("Set-Cookie"))
                raw = res.read().decode()
                return res.status, (json.loads(raw) if raw else None)
        except urllib.error.HTTPError as e:
            raw = e.read().decode()
            try:
                return e.code, (json.loads(raw) if raw else None)
            except json.JSONDecodeError:
                return e.code, {"_raw": raw}

c = Client()
c.call("POST", "/api/session", {"accessToken": os.environ["CHRONICLEAF_SMOKE_TOKEN"]})
status, accepted = c.call("POST", f"/api/milestones/{state['milestoneId']}/captures",
    {"url": "https://example.com/pricing", "viewports": ["desktop"], "waitMs": 0,
     "fullPage": False, "expectedBundleRevision": state["bundleRevision"],
     "idempotencyKey": "release-smoke-public-1"}, revision=state["bundleRevision"])
assert status == 202, (status, accepted)
op = accepted["operationId"]
deadline = time.time() + 120
final, code = None, None
while time.time() < deadline:
    status, row = c.call("GET", f"/api/operations/{op}")
    assert status == 200, (status, row)
    if row["status"] not in ("queued", "running", "retrying"):
        final, code = row["status"], row.get("errorCode")
        break
    time.sleep(2)
assert final == "failed" and code == "capture_sandbox_unavailable", (final, code)
print(f"CAPTURE_REFUSED operation={op} status={final} code={code}")
PYEOF
)" || fail "public capture did not fail with capture_sandbox_unavailable"
record "capture: $capture_out"

echo "==> restart with retained volumes keeps project evidence"
compose restart app postgres >/dev/null
ready=000
for _ in $(seq 1 60); do
  ready="$(curl -s -o /dev/null -w '%{http_code}' "${origin}/api/ready" 2>/dev/null || echo 000)"
  [ "$ready" = "200" ] && break
  sleep 1
done
[ "$ready" = "200" ] || fail "/api/ready after restart is ${ready}, not 200"
export CHRONICLEAF_SMOKE_STATE="$scratch/smoke.json"
python3 - <<'PYEOF' || fail "state changed across restart"
import json, os, urllib.request, urllib.error
origin = os.environ["CHRONICLEAF_SMOKE_ORIGIN"]
state = json.load(open(os.environ["CHRONICLEAF_SMOKE_STATE"]))
opener = urllib.request.build_opener()
jar = {}
def rpc(method, path, body=None):
    data = json.dumps(body if body is not None else {}).encode()
    req = urllib.request.Request(origin + path, data=data if method != "GET" else None, method=method)
    if jar.get("cookie") is not None:
        req.add_header("Cookie", jar["cookie"])
    if method != "GET":
        req.add_header("Content-Type", "application/json")
        req.add_header("Origin", origin)
        # Sign-in itself carries no antiforgery value (see the smoke client above).
        if path != "/api/session":
            areq = urllib.request.Request(origin + "/api/session/antiforgery")
            if jar.get("cookie") is not None:
                areq.add_header("Cookie", jar["cookie"])
            with opener.open(areq) as r:
                req.add_header("X-Chronicleaf-Antiforgery", json.loads(r.read().decode())["token"])
    with opener.open(req) as res:
        set_cookie = res.headers.get("Set-Cookie")
        if set_cookie is not None:
            jar["cookie"] = set_cookie.split(";")[0]
        return res.status, json.loads(res.read().decode() or "{}")
rpc("POST", "/api/session", {"accessToken": os.environ["CHRONICLEAF_SMOKE_TOKEN"]})
_, timeline = rpc("GET", f"/api/projects/{state['projectId']}/timeline")
assert timeline == state["timeline"], "timeline changed across restart"
_, bundle = rpc("GET", f"/api/milestones/{state['milestoneId']}/evidence")
assert bundle["items"][0]["revision"] == state["bundleRevision"], "evidence head changed across restart"
print("RESTART_OK timeline and evidence head identical")
PYEOF
record "restart: timeline and evidence head identical after app+postgres restart"

echo "==> backup, destroy, restore into fresh volumes, re-read"
compose exec -T postgres pg_dump -U chronicleaf -d chronicleaf -F c -f /tmp/chronicleaf-release.dump >/dev/null \
  || fail "pg_dump failed"
compose cp postgres:/tmp/chronicleaf-release.dump "$scratch/chronicleaf-release.dump" >/dev/null \
  || fail "reading the backup out of the container failed"
[ -s "$scratch/chronicleaf-release.dump" ] || fail "backup file is empty"
# Media leaves through the same pinned postgres image (alpine tar), so no third image enters.
docker run --rm -v "${project}_media:/media" -v "$scratch:/out" "$postgres_image" \
  tar czf /out/media-backup.tgz -C /media . >/dev/null || fail "media backup failed"
backup_size="$(du -h "$scratch/chronicleaf-release.dump" | cut -f1)"
compose down --volumes >/dev/null || fail "compose down failed"
compose up -d postgres >/dev/null || fail "fresh postgres did not start"
for _ in $(seq 1 60); do
  compose exec -T postgres pg_isready -U chronicleaf -d chronicleaf >/dev/null 2>&1 && break
  sleep 1
done
cat "$scratch/chronicleaf-release.dump" \
  | compose exec -T postgres pg_restore -U chronicleaf -d chronicleaf --clean --if-exists >/dev/null \
  || fail "pg_restore into fresh volumes failed"
docker run --rm -v "${project}_media:/media" -v "$scratch:/in:ro" "$postgres_image" \
  tar xzf /in/media-backup.tgz -C /media >/dev/null || fail "media restore failed"
compose up -d app >/dev/null || fail "app did not restart after restore"
ready=000
for _ in $(seq 1 60); do
  ready="$(curl -s -o /dev/null -w '%{http_code}' "${origin}/api/ready" 2>/dev/null || echo 000)"
  [ "$ready" = "200" ] && break
  sleep 1
done
[ "$ready" = "200" ] || fail "/api/ready after restore is ${ready}, not 200"
python3 - <<'PYEOF' || fail "restored state differs from the backup"
import json, os, urllib.request
origin = os.environ["CHRONICLEAF_SMOKE_ORIGIN"]
state = json.load(open(os.environ["CHRONICLEAF_SMOKE_STATE"]))
opener = urllib.request.build_opener()
jar = {}
def rpc(method, path, body=None):
    data = json.dumps(body if body is not None else {}).encode()
    req = urllib.request.Request(origin + path, data=data if method != "GET" else None, method=method)
    if jar.get("cookie") is not None:
        req.add_header("Cookie", jar["cookie"])
    if method != "GET":
        req.add_header("Content-Type", "application/json")
        req.add_header("Origin", origin)
        # Sign-in itself carries no antiforgery value (see the smoke client above).
        if path != "/api/session":
            areq = urllib.request.Request(origin + "/api/session/antiforgery")
            if jar.get("cookie") is not None:
                areq.add_header("Cookie", jar["cookie"])
            with opener.open(areq) as r:
                req.add_header("X-Chronicleaf-Antiforgery", json.loads(r.read().decode())["token"])
    with opener.open(req) as res:
        set_cookie = res.headers.get("Set-Cookie")
        if set_cookie is not None:
            jar["cookie"] = set_cookie.split(";")[0]
        return res.status, json.loads(res.read().decode() or "{}")
rpc("POST", "/api/session", {"accessToken": os.environ["CHRONICLEAF_SMOKE_TOKEN"]})
_, timeline = rpc("GET", f"/api/projects/{state['projectId']}/timeline")
assert timeline == state["timeline"], "timeline changed across restore"
_, bundle = rpc("GET", f"/api/milestones/{state['milestoneId']}/evidence")
assert bundle["items"][0]["revision"] == state["bundleRevision"], "evidence head changed across restore"
print("RESTORE_OK timeline and evidence head identical after backup and restore")
PYEOF
record "restore: backup ${backup_size} restored into fresh volumes; timeline and evidence head identical"

record "github: authenticated collection not driven here (no PAT configured); real public api.github.com collection is proven by the github-evidence browser fixture."
record "byok-live-smoke: not performed (credential-gated paid call, opt-in per design)."
record "deployable: false (this host cannot confine a browser, so screenshot success is unproven here; live provider smoke not performed)."

echo "verification passed"
cat "$evidence"
