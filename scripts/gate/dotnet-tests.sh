#!/usr/bin/env bash
# GATE-ADOPT-002. The single dotnet run: ONE `dotnet build` then ONE unfiltered
# `dotnet test --no-build`. A single unfiltered run cannot silently select nothing,
# which removes the failure class the old per-stage `--filter` + `--list-tests`
# guards existed for — so no filter or list-tests invocation may appear in the
# gate path. Per-package failure attribution comes from parsing, not scheduling:
# the run's output is tee'd to a log and scripts/gate/attribute-dotnet-failures.sh
# reports counts per test-class. The parser attributes; the run's own exit code
# verdicts, and it is propagated unchanged.
#
# CHRONICLEAF_SKIP_DB_TESTS is retired (GATE-ADOPT-002 Boundary): a single
# unfiltered run cannot honestly report partial coverage, so requesting it is a
# loud error, not a narrowed green run.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

if [ "${CHRONICLEAF_SKIP_DB_TESTS:-0}" = "1" ]; then
  echo "FAILED: CHRONICLEAF_SKIP_DB_TESTS is retired by verify-gate-adoption (GATE-ADOPT-002 Boundary): the single unfiltered dotnet run cannot honestly report partial coverage. Unset the variable to run the full gate." >&2
  exit 1
fi

# The integration tests prove their claims against real disposable PostgreSQL
# containers. An unreachable daemon used to be a quiet skip; here it is a loud
# failure with the reason, mirroring the old require_docker preflight.
if ! command -v docker >/dev/null 2>&1; then
  echo "FAILED (prerequisite unavailable): docker is not on PATH; the integration tests need real disposable PostgreSQL containers." >&2
  exit 1
fi
if ! docker info >/dev/null 2>&1; then
  echo "FAILED (prerequisite unavailable): the docker daemon is not reachable; the integration tests need real disposable PostgreSQL containers." >&2
  exit 1
fi

log_file="${CHRONICLEAF_DOTNET_LOG:-$(pwd)/dotnet-gate.log}"
echo "==> dotnet build: dotnet build Chronicleaf.sln --nologo"
dotnet build Chronicleaf.sln --nologo

echo "==> dotnet test (single unfiltered run): dotnet test Chronicleaf.sln --no-build --nologo"
status=0
dotnet test Chronicleaf.sln --no-build --nologo 2>&1 | tee "$log_file" || status=$?

echo "==> dotnet attribution (per test-class):"
bash scripts/gate/attribute-dotnet-failures.sh "$log_file" || true

if [ "$status" -ne 0 ]; then
  echo "FAILED (exit ${status}): dotnet test — see attribution above and ${log_file}" >&2
  exit "$status"
fi
echo "dotnet passed"
