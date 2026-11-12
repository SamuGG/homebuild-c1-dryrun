#!/usr/bin/env bash
# Runs the Tailwind CSS standalone CLI (no Node.js), downloading it into tools/.tailwindcss/ on first use.
# The version and each platform's SHA-256 are pinned here; a download that does not match is deleted and the build fails.
# Usage: tools/tailwindcss.sh <tailwindcss arguments>, for example -i Styles/app.css -o wwwroot/app.css --minify
# To upgrade: change VERSION, take both hashes from the release's sha256sums.txt, and check them against
# `gh api repos/tailwindlabs/tailwindcss/releases/tags/v<version> --jq '.assets[] | .name + " " + .digest'`.
set -euo pipefail

VERSION=4.3.5
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64)  asset=tailwindcss-macos-arm64; sha256=f3f4f5d1727e2b693b8f1c9a1492b5dd87008c86ea286bee1589cb458e6235fc ;;
  Linux-x86_64)  asset=tailwindcss-linux-x64;   sha256=e47695cee3f0d90623a91f3939b08bdee610eb5ad99340adff9d4145b284d6cc ;;
  *) echo "tailwindcss.sh: no pinned Tailwind CLI for $(uname -s) $(uname -m); add its asset and SHA-256." >&2; exit 1 ;;
esac

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/.tailwindcss"
cli="$dir/$asset-$VERSION"

checksum() { if command -v sha256sum >/dev/null; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1; }

if [ ! -x "$cli" ]; then
  mkdir -p "$dir"
  partial="$cli.download"
  echo "tailwindcss.sh: downloading Tailwind CSS $VERSION ($asset)" >&2
  curl -fsSL --retry 3 -o "$partial" "https://github.com/tailwindlabs/tailwindcss/releases/download/v$VERSION/$asset"
  actual="$(checksum "$partial")"
  if [ "$actual" != "$sha256" ]; then
    rm -f "$partial"
    echo "tailwindcss.sh: checksum mismatch for $asset $VERSION: expected $sha256, got $actual" >&2
    exit 1
  fi
  chmod +x "$partial"
  mv "$partial" "$cli"
fi

exec "$cli" "$@"
