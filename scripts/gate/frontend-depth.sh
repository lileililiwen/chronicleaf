#!/usr/bin/env bash
# GATE-ADOPT-003. The deep frontend check — the concrete answer to the
# "shallow gate" critique. Two halves, split by harness ownership:
#
# 1. Standalone half (executed here, no server, no database): the
#    token-sweep suite (static oracles, no server), the form-feedback and
#    perf-animation suites (in-spec vite dev server with route-mocked
#    `/api/*`), plus the new per-top-level-nav-page axe-core WCAG pass
#    (tests/frontend-depth-axe.spec.ts, same harness: every collection reads
#    empty, every provider reads unconfigured, zero critical/serious per
#    page).
# 2. Wiring half (proven here, executed in the e2e-browser check): the
#    a11y-core, a11y-roles and responsive-shell suites need the published
#    application with a real PostgreSQL behind it, so this script does NOT
#    re-drive them — it asserts each of their files is named in a
#    scripts/e2e.sh `run_suite` invocation and fails loudly naming whichever
#    suite is unwired.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root/src/Chronicleaf.Web"

echo "==> frontend depth (standalone): token-sweep + form-feedback + perf-animation"
npx playwright test tests/token-sweep.spec.ts tests/form-feedback.spec.ts tests/perf-animation.spec.ts

echo "==> frontend depth (standalone): axe-core WCAG pass per top-level nav page"
npx playwright test tests/frontend-depth-axe.spec.ts

cd "$repo_root"
echo "==> frontend depth (wiring): app-backed suites named in scripts/e2e.sh run_suite lines"
# run_suite invocations span backslash-continued lines and the script's prose
# comments name the same files, so the oracle extracts only the invocation
# blocks (a block starts at a run_suite line and ends at the first line
# without a trailing backslash) and requires each suite file inside one.
run_blocks="$(awk '/^[[:space:]]*run_suite/ { in_run = 1 } in_run { print } in_run && !/\\$/ { in_run = 0 }' scripts/e2e.sh)"
wiring_failures=0
for suite in tests/a11y-core.spec.ts tests/a11y-roles.spec.ts tests/responsive-shell.spec.ts; do
  if printf '%s\n' "$run_blocks" | grep -F -- "$suite" >/dev/null 2>&1; then
    echo "    wired: ${suite}"
  else
    echo "FAILED: ${suite} is not named in any scripts/e2e.sh run_suite invocation" >&2
    wiring_failures=$((wiring_failures + 1))
  fi
done
if [ "$wiring_failures" -ne 0 ]; then
  echo "FAILED: ${wiring_failures} app-backed UI suite(s) unwired from scripts/e2e.sh" >&2
  exit 1
fi
echo "frontend depth passed"
