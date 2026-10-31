#!/usr/bin/env bash
# Usage: testnet/report.sh <git-ref>
# Prints what the build under test tried to do, as markdown. Safe to run while it's going.
set -euo pipefail
cd "$(dirname "$0")"

ref=${1:?usage: report.sh <git-ref>}
export TESTNET_NAME
TESTNET_NAME=$(echo "$ref" | tr -c 'a-zA-Z0-9\n' '-' | tr 'A-Z' 'a-z')
compose() { docker compose --env-file "${ENV_FILE:-.env}" "$@"; }

logs=$(compose logs --no-color --no-log-prefix companion)
count() { printf '%s\n' "$logs" | grep -c -E "$1" || true; }

echo "## Test net report: $ref"
echo
echo "| Measure | Value |"
echo "|---|---|"
echo "| Started | $(docker inspect -f '{{.State.StartedAt}}' "$(compose ps -q companion)") |"
echo "| Restarts | $(docker inspect -f '{{.RestartCount}}' "$(compose ps -q companion)") |"
echo "| Proposals applied | $(count 'Applying ') |"
echo "| Proposals rejected | $(count 'found worse results|rejected:') |"
echo "| At a limit | $(count 'at the configured limit') |"
echo "| Errors in log | $(count '\[ERR\]|\bERR\b|Error') |"
echo "| CPU / memory now | $(docker stats --no-stream --format '{{.CPUPerc}} / {{.MemUsage}}' "$(compose ps -q companion)") |"
echo
echo "### Settings written to nodes (never reached them)"
echo
compose exec -T relay python - <<'P'
import json, os
path = "/data/sets.jsonl"
rows = [json.loads(l) for l in open(path)] if os.path.exists(path) else []
print(f"{len(rows)} writes")
last = {}
for r in rows:
    k = (r["node"], r["setting"])
    first, n, _ = last.get(k, (r["value"], 0, None))
    last[k] = (first, n + 1, r["value"])
if last:
    print("\n| Node | Setting | Writes | First | Latest |\n|---|---|---|---|---|")
    for (node, setting), (first, n, latest) in sorted(last.items()):
        print(f"| {node} | {setting} | {n} | {first} | {latest} |")
P
