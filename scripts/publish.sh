#!/usr/bin/env bash
# Publish the Chronicleaf runtime shell.
#
# The React client is built first, then copied into the API publish output as wwwroot.
# This is the explicit build/publish step referenced by the runtime shell design; it uses
# repository-relative paths only and never a machine-specific absolute path.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${1:-dist/publish}"
configuration="${2:-Release}"

case "$output_dir" in
  '' | '/' | '.' | '..')
    echo "refusing to publish into '$output_dir'" >&2
    exit 1
    ;;
esac

cd "$repo_root"

echo "==> npm ci (src/Chronicleaf.Web)"
(cd src/Chronicleaf.Web && npm ci)

echo "==> npm run build (src/Chronicleaf.Web)"
(cd src/Chronicleaf.Web && npm run build)

if [ -e "$output_dir" ]; then
  rm -rf "$output_dir"
fi

echo "==> dotnet publish (src/Chronicleaf.Api -> $output_dir, $configuration)"
dotnet publish src/Chronicleaf.Api/Chronicleaf.Api.csproj -c "$configuration" -o "$output_dir"

echo "==> copying the built client into the publish output"
mkdir -p "$output_dir/wwwroot"
cp -R src/Chronicleaf.Web/dist/. "$output_dir/wwwroot/"

echo "published: $repo_root/$output_dir"
