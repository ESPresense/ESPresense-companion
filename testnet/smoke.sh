#!/usr/bin/env bash
# Proves the isolation with a fake live broker: traffic gets copied in, leases and test writes don't cross.
set -euo pipefail
cd "$(dirname "$0")"

export TESTNET_NAME=smoke LIVE_HOST=fake-live LIVE_PORT=1883 LIVE_USERNAME= LIVE_PASSWORD= LIVE_TLS=false
export RECORD_TRAFFIC=false UI_BIND=127.0.0.1 UI_PORT=0
export HOUSE_CONFIG=$PWD/../src/config.example.yaml SOURCE_DIR=$(mktemp -d)
./export.sh HEAD "$SOURCE_DIR" >/dev/null
compose() { docker compose --profile smoke --env-file /dev/null "$@"; }
trap 'compose down -v --rmi local --remove-orphans >/dev/null 2>&1; rm -rf "$SOURCE_DIR"' EXIT

fails=0
check() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected [$2], got [$3]"; fails=$((fails + 1)); fi; }
live_pub() { compose exec -T fake-live mosquitto_pub "$@"; }
test_pub() { compose exec -T broker mosquitto_pub "$@"; }
read_one() { compose exec -T "$1" mosquitto_sub -t "$2" -C 1 -W 3 2>/dev/null | tr -d '\r' || true; }

compose up -d --build --wait broker fake-live relay
sleep 2

live_pub -r -t espresense/rooms/n1/absorption -m 3.1
live_pub -r -t espresense/companion/lease/optimization -m '{"instanceId":"live"}'
live_pub -t espresense/rooms/n1/restart/set -m PRESS
compose exec -T fake-live sh -c 'for i in 1 2 3 4 5 6; do mosquitto_pub -t espresense/devices/d1/n1 -m "{\"rssi\":-60}"; sleep 0.5; done' &
sleep 1
check "device traffic is copied" '{"rssi":-60}' "$(read_one broker 'espresense/devices/#')"
wait
check "retained settings are copied" 3.1 "$(read_one broker espresense/rooms/n1/absorption)"
check "live leases are not copied" "" "$(read_one broker 'espresense/companion/#')"

test_pub -t espresense/rooms/n1/absorption/set -m 3.4
sleep 1
check "a test write is echoed back" 3.4 "$(read_one broker espresense/rooms/n1/absorption)"
check "a test write doesn't reach live" 3.1 "$(read_one fake-live espresense/rooms/n1/absorption)"
check "no /set reaches live" "" "$(read_one fake-live 'espresense/rooms/+/+/set')"

live_pub -r -t espresense/rooms/n1/absorption -m 2.9
sleep 1
check "live can't overwrite a test value" 3.4 "$(read_one broker espresense/rooms/n1/absorption)"

test_pub -t espresense/rooms/n1/restart/set -m PRESS
sleep 1
check "commands aren't echoed" "" "$(read_one broker espresense/rooms/n1/restart)"

check "the isolated network has no way out" true \
  "$(docker network inspect testnet-smoke_isolated -f '{{.Internal}}')"
check "broker can't reach the fake live broker" unreachable \
  "$(compose exec -T broker sh -c 'nc -z -w 2 fake-live 1883 2>/dev/null && echo reachable || echo unreachable')"

compose up -d --build --wait --wait-timeout 300 companion
sleep 5
check "Companion connects to the test broker" 1 \
  "$(compose logs broker | grep -c 'as espresense-testnet ' | sed 's/^[2-9].*/1/')"
api() { compose exec -T companion curl -s "$@"; }
check "Companion reads copied settings" true \
  "$(api http://localhost:8267/api/node/n1 | grep -q '"absorption":3.4' && echo true || echo false)"
api -X PUT -H 'Content-Type: application/json' -d '{"calibration":{"absorption":3.7}}' http://localhost:8267/api/node/n1 >/dev/null
sleep 2
check "a Companion write comes back through the echo" true \
  "$(api http://localhost:8267/api/node/n1 | grep -q '"absorption":3.7' && echo true || echo false)"
check "the write was recorded" true \
  "$(compose exec -T relay grep -q '"value": "3.7' /data/sets.jsonl && echo true || echo false)"
check "the write didn't reach live" 2.9 "$(read_one fake-live espresense/rooms/n1/absorption)"
check "Companion can't reach the fake live broker" unreachable \
  "$(compose exec -T companion sh -c 'timeout 2 bash -c "</dev/tcp/fake-live/1883" 2>/dev/null && echo reachable || echo unreachable')"
check "Companion can't reach the LAN" unreachable \
  "$(compose exec -T companion sh -c 'timeout 2 bash -c "</dev/tcp/1.1.1.1/53" 2>/dev/null && echo reachable || echo unreachable')"
check "live credentials aren't in the Companion config" 0 \
  "$(compose exec -T companion grep -c -i -E 'username|password' /config/espresense/config.yaml || true)"

report=$(ENV_FILE=/dev/null ./report.sh smoke)
check "the report lists the write" true "$(printf '%s' "$report" | grep -q '| n1 | absorption |' && echo true || echo false)"

[ "$fails" = 0 ] && echo "All checks passed" || { echo "$fails checks failed"; exit 1; }
