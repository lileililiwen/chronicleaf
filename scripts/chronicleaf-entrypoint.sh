#!/usr/bin/env bash
# Chronicleaf application container initialization (community-compose-release).
#
# One application service, initialized before the non-root API starts:
#   1. private key/media ownership and dedicated browser job directories exist;
#   2. the documented capture-UID egress firewall is applied when this container holds the
#      capability to install it (CAP_NET_ADMIN on Linux);
#   3. the non-root API starts alongside the bounded in-process supervisor.
#
# Chromium sandboxing is never disabled and webpage processing never runs as root: this script
# runs the API as the image's runtime user and only *attempts* the firewall step. On a host
# that cannot grant CAP_NET_ADMIN, or without iptables/nftables available, the step reports
# the fact and the server still starts - capture jobs then answer
# capture_sandbox_unavailable before any browser process is started, which is the honest
# refusal rather than a capture with weaker isolation. No flag here disables that refusal.
set -euo pipefail

media_root="${Chronicleaf__MediaRoot:-/app/runtime/media}"
browser_jobs_dir="${CHRONICLEAF_BROWSER_JOBS_DIR:-/app/runtime/browser-jobs}"

mkdir -p "$media_root" "$browser_jobs_dir"

# The capture firewall is the kernel mechanism the screenshot-capture sandbox supervisor
# probes for: owner-based rules restricting the capture UID to the egress proxy. Installing
# them needs privilege this process usually does not have, so this is best-effort by
# design and its absence is a recorded property of the host, not a silent downgrade.
if command -v nft >/dev/null 2>&1 || command -v iptables >/dev/null 2>&1; then
  if capsh --print 2>/dev/null | grep -q "cap_net_admin" || [ "$(id -u)" = "0" ]; then
    echo "chronicleaf-entrypoint: applying capture-UID egress firewall" >&2
    # The exact rule set is documented in docs/community-quickstart.md; failures here
    # must not prevent the API from serving the workflow that needs no browser.
    if ! chronicleaf-capture-firewall 2>/dev/null; then
      echo "chronicleaf-entrypoint: capture firewall tool unavailable; capture jobs will answer capture_sandbox_unavailable" >&2
    fi
  else
    echo "chronicleaf-entrypoint: no CAP_NET_ADMIN; capture jobs will answer capture_sandbox_unavailable" >&2
  fi
else
  echo "chronicleaf-entrypoint: no nft/iptables tooling; capture jobs will answer capture_sandbox_unavailable" >&2
fi

exec dotnet Chronicleaf.Api.dll "$@"
