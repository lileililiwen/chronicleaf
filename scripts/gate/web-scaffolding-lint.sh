#!/usr/bin/env bash
# GATE-ADOPT-003. Web-scaffolding-lint check: production UI contains no
# spec-authoring or design-boundary text (PRODUCT-SHELL-UX-003). Runs before
# any test check in manifest order, so scaffolding fails the gate first.
# The bundle half is covered because the frontend-build check produces dist
# first and this script scans it when present.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"
bash scripts/check-web-scaffolding.sh
