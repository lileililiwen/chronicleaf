#!/usr/bin/env bash
# GATE-ADOPT-002. Per-class attribution for the single unfiltered dotnet run.
# Groups the run's `Failed <fully-qualified>.<test> [...]` lines by test-class
# (method segment stripped) and prints `count class` rows. The map is generic —
# no class-to-package list is hardcoded, so a renamed class can never silently
# fall out of attribution. This script attributes; it never verdicts the gate,
# but it exits nonzero iff the log holds at least one failure line so a caller
# piping only this script still fails loudly.
#
# Usage: bash scripts/gate/attribute-dotnet-failures.sh <dotnet-test-log>
set -euo pipefail

log_file="${1:?usage: attribute-dotnet-failures.sh <dotnet-test-log>}"
if [ ! -f "$log_file" ]; then
  echo "FAILED: attribution log not found: ${log_file}" >&2
  exit 1
fi

failures="$(grep -E '^[[:space:]]*Failed [A-Za-z0-9_.]+( \[[^]]*\])? *$' "$log_file" \
  | sed -E 's/^[[:space:]]*Failed //; s/ \[[^]]*\] *$//' \
  | rev | cut -d. -f2- | rev \
  | sort | uniq -c | sort -rn || true)"

if [ -z "$failures" ]; then
  echo "attribution: no failed tests in ${log_file}"
  exit 0
fi

echo "attribution: failures per test-class:"
echo "$failures"
exit 1
