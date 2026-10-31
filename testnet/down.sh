#!/usr/bin/env bash
# Usage: testnet/down.sh <git-ref>
# Removes the stack and everything it recorded. Run report.sh first if you want to keep the result.
set -euo pipefail
cd "$(dirname "$0")"

ref=${1:?usage: down.sh <git-ref>}
export TESTNET_NAME
TESTNET_NAME=$(echo "$ref" | tr -c 'a-zA-Z0-9\n' '-' | tr 'A-Z' 'a-z')
docker compose --env-file .env --profile smoke down -v --rmi local --remove-orphans
