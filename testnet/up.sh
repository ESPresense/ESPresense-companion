#!/usr/bin/env bash
# Usage: testnet/up.sh <git-ref>
# Builds <git-ref> from a clean export and starts a test net for it. One ref, one stack.
set -euo pipefail
cd "$(dirname "$0")"

ref=${1:?usage: up.sh <git-ref>}
[ -f .env ] || { echo "testnet/.env is missing; copy .env.example" >&2; exit 1; }

export TESTNET_NAME
TESTNET_NAME=$(echo "$ref" | tr -c 'a-zA-Z0-9\n' '-' | tr 'A-Z' 'a-z')
export SOURCE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/espresense-testnet/$TESTNET_NAME"

git fetch -q origin
sha=$(./export.sh "$ref" "$SOURCE_DIR")

docker compose --env-file .env up -d --build --wait --wait-timeout 300 broker relay companion ui
echo "$ref ($sha) is running as testnet-$TESTNET_NAME"
echo "UI: http://$(docker compose --env-file .env port ui 8267)"
