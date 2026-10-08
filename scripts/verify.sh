#!/usr/bin/env bash
# Chronicleaf verification entry point (verify-gate-adoption, GATE-ADOPT-004).
#
# Thin wrapper over the adopted sibling gate runner: `driftwatchdog gate`
# resolves the policy in `.ai-gate/gate.yaml` (project-defined profile
# `chronicleaf-gate`: platform-feed, web-scaffolding-lint, dotnet,
# frontend-build, frontend-depth, e2e-browser), executes each bound check in
# `scripts/gate/`, aggregates with the manifest blocking policy, and exits
# nonzero when blocked. Any failing check propagates as this script's own
# nonzero exit; there is no fallback to older stages (a fallback would be
# two gates) and no behavior here beyond strict mode, root resolution, and
# delegation. A missing `driftwatchdog` binary fails loudly here instead of
# running a degraded substitute.
#
# The dotnet section is ONE `dotnet build` then ONE unfiltered
# `dotnet test --no-build` (scripts/gate/dotnet-tests.sh) with per-class
# failure attribution parsed from the single run's output — no per-stage
# filters, no list-tests guards. The e2e browser suites run only under
# CHRONICLEAF_E2E=1 (scripts/gate/e2e-browser.sh); without it that check
# SKIPs explicitly and full-gate evidence must not be claimed.
#
# Full gate (light steps run per change; the whole gate runs once at batch
# end by the coordinator, NOT per change):
# CHRONICLEAF_E2E=1 bash scripts/verify.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

exec driftwatchdog gate "$@"
