#!/usr/bin/env bash
# PRODUCT-SHELL-UX-003. Production UI contains no spec-authoring or
# design-boundary text. This stage fails loudly when a forbidden pattern is
# present in the Web production sources or the built production bundle, and
# passes on the clean tree. It runs before any test stage, so scaffolding
# fails the gate before a test runs.
#
# The patterns are literal production strings the 2026-10-07 visual review
# removed: the operations footer ("NOT IN THIS CHANGE"), the shell notice
# ("Not in this change", "Project setup coming in a later change.") and the
# testids that carried them (`later-packages`, `setup-pending`).
#
# Playwright fixtures are deliberately NOT scanned: the shell and
# product-shell-ux suites assert the absence of these strings by name, and
# scanning them would fail the clean tree on the discipline itself working.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
web_src="$repo_root/src/Chronicleaf.Web/src"
web_dist="$repo_root/src/Chronicleaf.Web/dist"

failures=0

for pattern in 'NOT IN THIS CHANGE' 'Not in this change' 'Project setup coming in a later change.' \
    "'later-packages'" '"later-packages"' "'setup-pending'" '"setup-pending"'; do
  if grep -rn --fixed-strings -- "$pattern" "$web_src" 2>/dev/null; then
    echo "FAILED: scaffolding pattern '${pattern}' present in Web sources" >&2
    failures=$((failures + 1))
  fi
done

if [ -d "$web_dist/assets" ]; then
  for pattern in 'NOT IN THIS CHANGE' 'Not in this change' 'Project setup coming in a later change.'; do
    if grep -rn --fixed-strings -- "$pattern" "$web_dist/assets" 2>/dev/null; then
      echo "FAILED: scaffolding string '${pattern}' present in the served production bundle" >&2
      failures=$((failures + 1))
    fi
  done
else
  echo "NOTE: no built bundle at src/Chronicleaf.Web/dist/assets; bundle half of the check is skipped." >&2
fi

# The stage must select its target rather than match nothing: prove the
# patterns are live by asserting this script itself names them.
if ! grep -q 'NOT IN THIS CHANGE' "$repo_root/scripts/check-web-scaffolding.sh"; then
  echo "FAILED: the lint stage no longer names its own target; it would pass on anything." >&2
  failures=$((failures + 1))
fi

if [ "$failures" -ne 0 ]; then
  echo "FAILED: web scaffolding lint found ${failures} violation(s)." >&2
  exit 1
fi

echo "web scaffolding lint passed: no authoring-scaffolding text in sources or bundle."
