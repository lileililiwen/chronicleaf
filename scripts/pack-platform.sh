#!/usr/bin/env bash
#
# Pack the platform packages Chronicleaf depends on into the repository's own NuGet feed.
#
# MAINTENANCE TOOL ONLY. This script is the one and only place in this repository that may
# mention a path to a dotnet-platform-libs checkout. It is never invoked by the build, the
# Dockerfile, `dotnet restore`, or any verification stage: the restore path is `nuget.config`
# plus the .nupkg files committed under packages/platform-feed, so a fresh clone on a machine
# with no sibling checkout - and CI - restores correctly. An environment-variable feed is
# rejected for the same reason (see Ruling 12 of the workspace ARCHITECTURE.md): a runner that
# does not have it fails exactly like a hard-coded absolute path, but harder to spot.
#
# Re-run this script whenever a platform package version changes, then commit the changed
# feed. scripts/verify.sh fails when the committed feed does not contain the exact version the
# Infrastructure project references, so a forgotten re-pack cannot pass unnoticed.
#
# Usage:
#   scripts/pack-platform.sh
#   PLATFORM_ROOT=/path/to/dotnet-platform-libs PLATFORM_VERSION=0.1.0 scripts/pack-platform.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Default sibling location, matching the convention therapist-commons uses. Overridable, and
# never read by anything except this script.
PLATFORM_ROOT="${PLATFORM_ROOT:-/home/paul/code/dotnet-platform-libs}"
PLATFORM_VERSION="${PLATFORM_VERSION:-0.1.0}"
FEED_DIR="${FEED_DIR:-$repo_root/packages/platform-feed}"

# The whole transitive closure Chronicleaf needs: Platform.Storage carries the IObjectStorage
# contract and the key validation, Platform.Storage.Local is the filesystem adapter,
# Platform.Storage.S3 is the S3-compatible adapter the cloud object storage package is scoped to,
# and Platform.Core is Platform.Storage's own dependency. Platform.Jobs carries the IJobDispatcher
# contract and the JobPayload shape, Platform.Jobs.Hangfire is the durable scheduling adapter, and
# Platform.Core is Platform.Jobs's dependency too. The whole closure is listed because a feed
# that carries only the direct references is a feed that fails to restore on a cold cache, which
# is the whole failure this stage exists to catch. Nothing else is copied - a feed that carries
# the sibling's whole catalogue would make a version bump in an unrelated package look like a
# Chronicleaf change.
PACKAGES=(
  Platform.Core
  Platform.Storage
  Platform.Storage.Local
  Platform.Storage.S3
  Platform.Jobs
  Platform.Jobs.Hangfire
)

if [[ ! -d "$PLATFORM_ROOT/src" ]]; then
  echo "platform repo not found at $PLATFORM_ROOT; set PLATFORM_ROOT to a dotnet-platform-libs checkout" >&2
  exit 1
fi

mkdir -p "$FEED_DIR"
echo "==> packing ${#PACKAGES[@]} package(s) at $PLATFORM_VERSION from $PLATFORM_ROOT into $FEED_DIR"

for pkg in "${PACKAGES[@]}"; do
  proj="$PLATFORM_ROOT/src/$pkg/$pkg.csproj"
  if [[ ! -f "$proj" ]]; then
    echo "FAILED: $proj does not exist; the platform source layout changed" >&2
    exit 1
  fi

  echo "    pack $pkg $PLATFORM_VERSION"
  dotnet pack "$proj" -c Release -o "$FEED_DIR" \
    -p:Version="$PLATFORM_VERSION" -p:VersionPrefix="$PLATFORM_VERSION" \
    --nologo -v q >/dev/null
done

# .snupkg symbol packages are not needed to restore or build and would double the feed size.
find "$FEED_DIR" -name '*.snupkg' -delete

echo "==> committed feed now contains:"
ls -1 "$FEED_DIR" | sed 's/^/    /'
echo
echo "commit the changed feed, then run: CHRONICLEAF_E2E=1 bash scripts/verify.sh"
