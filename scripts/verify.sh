#!/usr/bin/env bash
# Chronicleaf native verification (SHELL-003).
#
# Required chain, in order:
#   1. dotnet build            - .NET 10 API and test projects
#   2. dotnet test             - WebApplicationFactory contract assertions
#   3. npm ci                  - locked frontend dependencies
#   4. npm run build           - tsc then vite build
#
# Any failing step records its own failure and exits nonzero. Restore, NuGet and npm
# network failures are reported as failures; they are never skipped or tolerated.
#
# The real-browser stage is opt-in because it needs a published shell and Playwright
# browsers: CHRONICLEAF_E2E=1 bash scripts/verify.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

step() {
  local name="$1"
  shift
  echo "==> ${name}: $*"
  # The status is captured in a || list because `set -e` must not abort before the
  # failing command is reported. Returning it aborts the script with the same code.
  local status=0
  "$@" || status=$?
  if [ "$status" -ne 0 ]; then
    echo "FAILED (exit ${status}): ${name}: $*" >&2
    return "$status"
  fi
}

step "api build" dotnet build --nologo
step "api test" dotnet test --nologo
step "frontend install" bash -euo pipefail -c 'cd src/Chronicleaf.Web && npm ci'
step "frontend build" bash -euo pipefail -c 'cd src/Chronicleaf.Web && npm run build'

if [ "${CHRONICLEAF_E2E:-0}" = "1" ]; then
  step "browser fixtures" bash scripts/e2e.sh
fi

echo "verification passed"
