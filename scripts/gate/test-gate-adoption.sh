#!/usr/bin/env bash
# GATE-ADOPT-004 / gate-adoption test. Light, hermetic, no Docker, no browsers,
# no database, no full gate run:
#   1. the manifest exists and validates under `driftwatchdog gate --dry-run`
#      (plan resolution only — nothing executes, nothing persists);
#   2. the attribution parser attributes a seeded failure log to the right
#      test-classes with exact counts (and passes a clean log);
#   3. the wrapper still exits nonzero on a failing check (stubbed runner).
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

failures=0

echo "==> gate-adoption 1/3: manifest validates under driftwatch gate --dry-run"
if [ ! -f .ai-gate/gate.yaml ]; then
  echo "FAILED: .ai-gate/gate.yaml missing" >&2
  failures=$((failures + 1))
else
  dry_run_output="$(driftwatchdog gate --dry-run 2>&1)" || {
    echo "FAILED: driftwatchdog gate --dry-run exited nonzero:" >&2
    echo "$dry_run_output" >&2
    failures=$((failures + 1))
  }
  if [ "${failures}" -eq 0 ]; then
    for check in platform-feed web-scaffolding-lint dotnet frontend-build frontend-depth e2e-browser; do
      if ! printf '%s\n' "$dry_run_output" | grep -F -- "$check" >/dev/null 2>&1; then
        echo "FAILED: dry-run plan does not resolve check: ${check}" >&2
        failures=$((failures + 1))
      fi
    done
    echo "    dry-run resolves all six checks"
  fi
fi

echo "==> gate-adoption 2/3: parser attributes a seeded failure log per test-class"
seeded="$(mktemp)"
cat > "$seeded" <<'EOF'
  Passed Chronicleaf.Api.Tests.SessionTests.Login_succeeds [12 ms]
  Failed Chronicleaf.Integration.Tests.CommunityPersistenceTests.Projects_round_trip [1 s]
    Error Message: Assert failed
  Failed Chronicleaf.Integration.Tests.CommunityPersistenceTests.Milestones_round_trip [900 ms]
    Error Message: Assert failed
  Failed Chronicleaf.Api.Tests.VoiceProfilesTests.Forbidden_term_span [30 ms]
    Error Message: Assert failed
Failed! - Failed: 3, Passed: 10, Skipped: 0, Total: 13 - Chronicleaf.Tests.dll
EOF
parser_output="$(bash scripts/gate/attribute-dotnet-failures.sh "$seeded" 2>&1)" && parser_status=0 || parser_status=$?
if [ "$parser_status" -eq 0 ]; then
  echo "FAILED: parser exited 0 on a log with 3 failures" >&2
  failures=$((failures + 1))
fi
for expected in "2 Chronicleaf.Integration.Tests.CommunityPersistenceTests" "1 Chronicleaf.Api.Tests.VoiceProfilesTests"; do
  if ! printf '%s\n' "$parser_output" | grep -F -- "$expected" >/dev/null 2>&1; then
    echo "FAILED: parser did not attribute '${expected}'; got:" >&2
    echo "$parser_output" >&2
    failures=$((failures + 1))
  fi
done
clean="$(mktemp)"
printf 'Passed! - Failed: 0, Passed: 10, Skipped: 0, Total: 10 - Chronicleaf.Tests.dll\n' > "$clean"
if bash scripts/gate/attribute-dotnet-failures.sh "$clean" >/dev/null 2>&1; then
  echo "    seeded attribution exact; clean log passes"
else
  echo "FAILED: parser exited nonzero on a clean log" >&2
  failures=$((failures + 1))
fi
rm -f "$clean"

echo "==> gate-adoption 3/3: wrapper exits nonzero on a failing check"
stub_dir="$(mktemp -d)"
trap 'rm -rf "$stub_dir" "$seeded"' EXIT
printf '#!/usr/bin/env bash\necho "stub driftwatchdog: FAIL" >&2\nexit 1\n' > "$stub_dir/driftwatchdog"
chmod +x "$stub_dir/driftwatchdog"
if PATH="$stub_dir:$PATH" bash scripts/verify.sh >/dev/null 2>&1; then
  echo "FAILED: scripts/verify.sh exited 0 against a failing gate" >&2
  failures=$((failures + 1))
else
  echo "    wrapper propagates failing-check exit"
fi

if [ "$failures" -ne 0 ]; then
  echo "FAILED: gate-adoption: ${failures} assertion(s) failed" >&2
  exit 1
fi
echo "gate-adoption passed"
