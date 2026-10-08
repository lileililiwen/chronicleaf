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

bash scripts/e2e.sh
