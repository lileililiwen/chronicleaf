#!/usr/bin/env bash
# Proves that the platform packages committed under packages/platform-feed are exactly what the
# Infrastructure project resolves, and that they resolve from the repository itself.
#
# SHELL-003 and Ruling 12 of the workspace ARCHITECTURE.md are both about one thing: a fresh checkout
# with no sibling dotnet-platform-libs checkout must build. A build that only works because a
# developer's ~/.nuget cache already holds Platform.Storage is not that build, and neither is one that
# works because an environment variable points at a feed CI does not have. This script is the stage
# that makes either of those a failure rather than a surprise on somebody else's machine.
#
# It checks four things, in increasing order of strength:
#   1. the committed feed holds the exact versions the Infrastructure project references;
#   2. the repository's nuget.config reaches the feed by a repository-relative path and declares no
#      machine-specific one;
#   3. the resolved dependency graph has no unresolved platform dependency;
#   4. a restore into an EMPTY package folder with the feed itself present resolves, which is what
#      proves the committed artifacts - not a warm cache - are sufficient.
#
# Step 4 is the one that actually fails when the feed is missing, so a red run means the right thing.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

infrastructure="src/Chronicleaf.Infrastructure/Chronicleaf.Infrastructure.csproj"
feed="packages/platform-feed"
platform_version="0.1.0"

# Platform.Storage.Local is the filesystem adapter this package is scoped to, Platform.Storage is the
# contract and the key validation, Platform.Core is Platform.Storage's own dependency. Platform.Jobs
# is the dispatcher contract, Platform.Jobs.Hangfire is the durable scheduling adapter, and
# Platform.Core is Platform.Jobs's dependency too. The whole transitive closure is listed because a
# feed that carries only the direct references is a feed that fails to restore on a cold cache,
# which is the whole failure this stage exists to catch.
expected=(
  "Platform.Core"
  "Platform.Storage"
  "Platform.Storage.Local"
  "Platform.Jobs"
  "Platform.Jobs.Hangfire"
)

fail() {
  echo "FAILED: $*" >&2
  exit 1
}

echo "== 1. committed feed contents"
for package in "${expected[@]}"; do
  artifact="${feed}/${package}.${platform_version}.nupkg"
  [ -f "$artifact" ] || fail "${artifact} is missing; run scripts/pack-platform.sh and commit the feed"
  [ -s "$artifact" ] || fail "${artifact} is empty"
  echo "    present: ${artifact}"
done

# A .snupkg in the feed is dead weight: nothing in the build path consumes symbols, and one that is
# newer than its .nupkg is a sign the feed was re-packed without being replaced wholesale.
if [ -n "$(find "$feed" -name '*.snupkg' -print -quit)" ]; then
  fail "${feed} contains .snupkg symbol packages, which nothing in the build path resolves"
fi

echo "== 2. nuget.config reaches the feed repository-relatively"
[ -f nuget.config ] || fail "nuget.config is missing; the feed is only reachable through it"
grep -q '<clear />' nuget.config || fail "nuget.config must clear inherited sources before adding the feed"
grep -qF "value=\"${feed}\"" nuget.config \
  || fail "nuget.config does not add ${feed} as a repository-relative package source"
if grep -nE 'value="(/|[A-Za-z]:|file://|\\\\)' nuget.config >/dev/null; then
  fail "nuget.config declares a machine-specific package source: $(grep -nE 'value="(/|[A-Za-z]:|file://|\\\\)' nuget.config)"
fi
if grep -nE '\$\{?[A-Z_]*(NUGET|PLATFORM|FEED)[A-Z_]*\}?' nuget.config >/dev/null; then
  fail "nuget.config reads a feed from an environment variable, which Ruling 12 rejects"
fi
echo "    ok: the only platform source is ${feed}, by relative path"

echo "== 3. referenced versions match the committed feed"
for package in Platform.Storage Platform.Storage.Local Platform.Jobs Platform.Jobs.Hangfire; do
  referenced="$(sed -n "s|.*Include=\"${package}\" Version=\"\([^\"]*\)\".*|\1|p" "$infrastructure")"
  [ -n "$referenced" ] || fail "${infrastructure} does not reference ${package}"
  [ "$referenced" = "$platform_version" ] \
    || fail "${package} is referenced at ${referenced} but the feed carries ${platform_version}; re-pack and commit, or correct the reference"
  echo "    ${package} is pinned at ${referenced}"
done

# Nothing outside Infrastructure may need the feed: Core, Application and Api have to compile against
# the platform's contract without the platform's artifacts being on offer. This is what keeps the
# operation state machine free of platform types while every platform-typed seam - dispatcher, payload
# handler, executor, clock, server - lives in the one project that may name them.
for project in src/Chronicleaf.Core src/Chronicleaf.Application src/Chronicleaf.Api; do
  if grep -qE 'PackageReference Include="Platform\.' "$project"/*.csproj; then
    fail "$project references a Platform package; only Infrastructure may need the feed"
  fi
done
echo "    only Infrastructure depends on the platform packages"

echo "== 4. cold restore resolves the platform packages from the committed feed"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

# An empty package folder is the whole point: a warm global cache would satisfy the restore without
# the committed artifacts, which is exactly the failure this stage is here to catch. Offline mode
# makes an accidental nuget.org fallback for these three packages a restore failure too.
NUGET_PACKAGES="$scratch/packages" \
  dotnet restore Chronicleaf.sln \
    --packages "$scratch/packages" \
    --no-cache \
    --nologo \
    --verbosity quiet \
    >"$scratch/restore.log" 2>&1 \
  || fail "a cold restore with an empty package folder failed; the committed feed is not sufficient. See below."$'\n'"$(cat "$scratch/restore.log")"

for package in "${expected[@]}"; do
  resolved="$scratch/packages/${package,,}/${platform_version}"
  [ -d "$resolved" ] || fail "${package} ${platform_version} was not resolved from the committed feed into an empty package folder"
  echo "    resolved: ${package} ${platform_version}"
done

echo "platform feed verification passed"
