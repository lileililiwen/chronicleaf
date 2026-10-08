#!/usr/bin/env bash
# GATE-ADOPT-003. Platform-feed check: the committed packages/platform-feed is
# what the build resolves. Unchanged semantics; invoked by the gate instead of
# the old inline stage.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"
bash scripts/verify-platform-feed.sh
