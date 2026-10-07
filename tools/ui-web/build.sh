#!/usr/bin/env bash
# Builds the omtx ui static bundle into dist/ (index.html + hashed assets, relative URLs).
# BUN_REGISTRY overrides the package registry, e.g. http://192.168.1.2:4873 on Filip's LAN.
set -euo pipefail
cd "$(dirname "$0")"
BUN="$(command -v bun || echo "$HOME/.bun/bin/bun")"
if [ ! -x "$BUN" ]; then
  curl -fsSL https://bun.sh/install | bash
  BUN="$HOME/.bun/bin/bun"
fi
"$BUN" install --frozen-lockfile ${BUN_REGISTRY:+--registry "$BUN_REGISTRY"}
"$BUN" x tsc --noEmit
rm -rf dist
"$BUN" build ./index.html --outdir dist --production --public-path ./
ls -l dist
