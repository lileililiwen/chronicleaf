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
# `--verbosity quiet` on both steps is deliberate: the gate adapter keeps only
# the first 2048 bytes of a check's combined output (MAX_DIAGNOSTIC_BYTES), and
# at default verbosity the build's per-project lines alone consume that budget,
# so a test failure at the tail was invisible in the gate table. Quiet build
# output alone is not enough: the pre-existing compiler warnings still fill the
# budget, so -clp:ErrorsOnly drops the build to one success line (78 bytes
# measured); quiet test output is then the failed-test details plus the summary,
# which lands inside the visible head. The full output is still teed to
# $log_file for deep diagnosis.
echo "==> dotnet build: dotnet build Chronicleaf.sln --nologo --verbosity quiet -clp:ErrorsOnly"
dotnet build Chronicleaf.sln --nologo --verbosity quiet -clp:ErrorsOnly

echo "==> dotnet test (single unfiltered run): dotnet test Chronicleaf.sln --no-build --nologo --verbosity quiet"
status=0
dotnet test Chronicleaf.sln --no-build --nologo --verbosity quiet 2>&1 | tee "$log_file" || status=$?

echo "==> dotnet attribution (per test-class):"
bash scripts/gate/attribute-dotnet-failures.sh "$log_file" || true

if [ "$status" -ne 0 ]; then
  echo "FAILED (exit ${status}): dotnet test — see attribution above and ${log_file}" >&2
  exit "$status"
fi
echo "dotnet passed"
