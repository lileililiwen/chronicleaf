#!/usr/bin/env bash
# Chronicleaf native verification (SHELL-003, COMMUNITY-PERSISTENCE-001/002).
#
# Required chain, in order:
#   1. dotnet build            - .NET 10 Core/Application/Infrastructure/Api and test projects
#   2. dotnet test             - shell contract assertions, then the real-PostgreSQL integration
#                                tests of tests/Chronicleaf.Integration.Tests
#   3. npm ci                  - locked frontend dependencies
#   4. npm run build           - tsc then vite build
#
# Any failing step records its own failure and exits nonzero. Restore, NuGet and npm
# network failures are reported as failures; they are never skipped or tolerated.
#
# Stage 2 needs a running Docker daemon: the persistence scenarios use a real disposable
# PostgreSQL container and a mock or in-memory store is not accepted as evidence. When the
# daemon is unreachable this script FAILS with the reason instead of quietly skipping the
# scenarios. Set CHRONICLEAF_SKIP_DB_TESTS=1 only to run the stages that need no database, and
# treat that run as incomplete rather than as a passing gate.
#
# The real-browser stage is opt-in because it needs Playwright browsers:
# CHRONICLEAF_E2E=1 bash scripts/verify.sh
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

require_docker() {
  if ! command -v docker >/dev/null 2>&1; then
    echo "FAILED (prerequisite unavailable): docker is not on PATH; the persistence scenarios need a real PostgreSQL container." >&2
    return 1
  fi
  if ! docker info >/dev/null 2>&1; then
    echo "FAILED (prerequisite unavailable): the docker daemon is not reachable; the persistence scenarios need a real PostgreSQL container." >&2
    return 1
  fi
}

step "api build" dotnet build Chronicleaf.sln --nologo
step "shell tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~Chronicleaf.Api.Tests"

if [ "${CHRONICLEAF_SKIP_DB_TESTS:-0}" = "1" ]; then
  echo "WARNING: CHRONICLEAF_SKIP_DB_TESTS=1 - the real-PostgreSQL persistence tests did not run. This is not a complete gate." >&2
else
  step "docker prerequisite" require_docker
  step "persistence tests" dotnet test Chronicleaf.sln --nologo --filter "FullyQualifiedName~CommunityPersistenceTests"
fi

step "frontend install" bash -euo pipefail -c 'cd src/Chronicleaf.Web && npm ci'
step "frontend build" bash -euo pipefail -c 'cd src/Chronicleaf.Web && npm run build'

if [ "${CHRONICLEAF_E2E:-0}" = "1" ]; then
  step "browser fixtures" bash scripts/e2e.sh
fi

echo "verification passed"
