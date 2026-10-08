#!/usr/bin/env bash
# GATE-ADOPT-003. Frontend-build check: locked dependencies, typecheck, then
# the production bundle the lint and browser checks consume.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root/src/Chronicleaf.Web"
echo "==> frontend install: npm ci"
npm ci
echo "==> frontend typecheck: npx tsc --noEmit"
npx tsc --noEmit
echo "==> frontend build: npx vite build"
npx vite build
