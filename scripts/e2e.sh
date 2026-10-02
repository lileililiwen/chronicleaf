#!/usr/bin/env bash
# Run the browser fixtures against the real built and published Chronicleaf shell.
#
# The application is published and started from its own output; the Playwright fixtures
# drive a real browser against that server. No mock, stub or dev server is involved.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_dir="${CHRONICLEAF_PUBLISH_DIR:-dist/e2e}"
base_url="${CHRONICLEAF_BASE_URL:-http://127.0.0.1:5081}"
log_file="$repo_root/$publish_dir/server.log"

bash "$repo_root/scripts/publish.sh" "$publish_dir" Release

echo "==> starting the published shell at $base_url"
# exec replaces the subshell, so $! is the server process itself and the trap below
# actually stops it instead of orphaning it on the port.
(cd "$repo_root/$publish_dir" && exec env ASPNETCORE_URLS="$base_url" dotnet Chronicleaf.Api.dll >"$log_file" 2>&1) &
server_pid=$!
trap 'kill "$server_pid" 2>/dev/null || true' EXIT

ready=0
for _ in $(seq 1 60); do
  if curl --fail --silent --show-error --max-time 2 "$base_url/api/health" >/dev/null 2>&1; then
    ready=1
    break
  fi
  if ! kill -0 "$server_pid" 2>/dev/null; then
    echo "the published shell exited before becoming ready:" >&2
    cat "$log_file" >&2
    exit 1
  fi
  sleep 1
done

if [ "$ready" -ne 1 ]; then
  echo "the published shell did not answer /api/health within 60s:" >&2
  cat "$log_file" >&2
  exit 1
fi

echo "==> npm run test:e2e (real browser against $base_url)"
(cd "$repo_root/src/Chronicleaf.Web" && CHRONICLEAF_BASE_URL="$base_url" npm run test:e2e)
