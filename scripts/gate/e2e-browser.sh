#!/usr/bin/env bash
# GATE-ADOPT-003. E2E-browser check: the real-browser suites via scripts/e2e.sh
# (published application, real disposable PostgreSQL per suite, real database
# behind it). Honors the existing CHRONICLEAF_E2E=1 path: with it set the suites
# run; without it this check prints an explicit SKIP note and exits 0 rather
# than running browsers nobody asked for. Full-gate evidence is only claimed
# from runs with CHRONICLEAF_E2E=1 (GATE-ADOPT-003 Boundary).
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

if [ "${CHRONICLEAF_E2E:-0}" != "1" ]; then
  echo "SKIP: e2e-browser not requested (CHRONICLEAF_E2E is not 1). No browser suite ran; do not claim full-gate evidence from this run." >&2
  exit 0
fi

# PG-TIGHTEN-FU-001 preflight: refuse to start when an orphan `dotnet
# Chronicleaf.Api.dll` from a prior interrupted run holds any of the 30 origins.
# Mirrors assert_suite_ports_free in scripts/e2e.sh (the same function is called
# at the top of start_suite, so a held port fails the gate before any disposable
# database is cloned or any new server is launched; this stage is the user-
# visible artifact, named in the gate's stage output).
assert_suite_ports_free() {
  local origin port pid listener_cmd
  for origin in "$@"; do
    port="$(printf '%s' "$origin" | sed -n 's|^http://127.0.0.1:\([0-9][0-9]*\).*|\1|p')"
    if [ -z "$port" ]; then
      continue
    fi
    pid="$(ss -tnlp "sport = :$port" 2>/dev/null \
      | sed -n 's|.*pid=\([0-9][0-9]*\).*|\1|p' \
      | head -n 1)"
    if [ -n "$pid" ] && [ -d "/proc/$pid" ]; then
      listener_cmd="$(tr '\0' ' ' </proc/"$pid"/cmdline 2>/dev/null || echo '<unknown>')"
      echo "port $port (origin $origin) is held by an orphan listener (pid=$pid cmd='$listener_cmd') - refusing to start the gate on a port another process owns" >&2
      return 1
    fi
  done
}

assert_suite_ports_free \
  http://127.0.0.1:5081 http://127.0.0.1:5082 http://127.0.0.1:5083 \
  http://127.0.0.1:5084 http://127.0.0.1:5085 http://127.0.0.1:5086 \
  http://127.0.0.1:5087 http://127.0.0.1:5088 http://127.0.0.1:5089 \
  http://127.0.0.1:5090 http://127.0.0.1:5091 http://127.0.0.1:5092 \
  http://127.0.0.1:5093 http://127.0.0.1:5094 http://127.0.0.1:5095 \
  http://127.0.0.1:5096 http://127.0.0.1:5097 http://127.0.0.1:5098 \
  http://127.0.0.1:5099 http://127.0.0.1:5100 http://127.0.0.1:5101 \
  http://127.0.0.1:5102 http://127.0.0.1:5103 http://127.0.0.1:5104 \
  http://127.0.0.1:5105 http://127.0.0.1:5106 http://127.0.0.1:5107 \
  http://127.0.0.1:5108 http://127.0.0.1:5109 http://127.0.0.1:5110

bash scripts/e2e.sh
