"""One-way copy of live ESPresense traffic onto a test broker.

The live client only ever subscribes: this file has no call that publishes through it. The broker
account should be read-only as well, so that stays true whatever happens to this code.

The test client stands in for the nodes: real nodes confirm a setting by publishing it back, so
without that echo the Companion under test would never see its own calibration land.
"""
import json
import os
import re
import ssl
import threading
import time

import paho.mqtt.client as mqtt
from paho.mqtt.subscribeoptions import SubscribeOptions

# Not espresense/companion/#: that is the live Companion's own state, and its leases would stop the
# build under test from ever optimizing.
LIVE_TOPICS = ["espresense/devices/#", "espresense/rooms/#", "espresense/settings/#"]

SET_TOPIC = "espresense/rooms/+/+/set"
SET_PATTERN = re.compile(r"^espresense/rooms/([^/]+)/([^/]+)/set$")
SETTING_PATTERN = re.compile(r"^espresense/rooms/([^/]+)/([^/]+)$")

# Commands, not settings: a real node acts on them and doesn't publish them back.
NOT_ECHOED = {"update", "arduino_ota", "restart"}

DATA_DIR = os.environ.get("DATA_DIR", "/data")
OVERRIDES_PATH = os.path.join(DATA_DIR, "overrides.json")
SETS_PATH = os.path.join(DATA_DIR, "sets.jsonl")
RECORD_TRAFFIC = os.environ.get("RECORD_TRAFFIC", "false").lower() == "true"

lock = threading.Lock()
overrides: dict[str, str] = {}
counts = {"copied": 0, "dropped": 0, "echoed": 0}


def load_overrides():
    if os.path.exists(OVERRIDES_PATH):
        with open(OVERRIDES_PATH) as f:
            overrides.update(json.load(f))


def save_overrides():
    with open(OVERRIDES_PATH + ".tmp", "w") as f:
        json.dump(overrides, f)
    os.replace(OVERRIDES_PATH + ".tmp", OVERRIDES_PATH)


def append_jsonl(path, entry):
    with open(path, "a") as f:
        f.write(json.dumps(entry) + "\n")


def should_copy(topic):
    """Live commands stay out, and so does a live value for a setting the test build has changed."""
    if SET_PATTERN.match(topic):
        return False
    return topic not in overrides


def on_live_connect(client, _userdata, _flags, reason_code, _properties):
    print(f"live broker: connected ({reason_code})", flush=True)
    # Retain-as-published: otherwise the broker clears the flag on anything sent after we subscribed,
    # and an updated setting would stop being retained on the test broker.
    options = SubscribeOptions(qos=0, retainAsPublished=True)
    client.subscribe([(topic, options) for topic in LIVE_TOPICS])


def on_live_message(_client, test, message):
    with lock:
        copy = should_copy(message.topic)
        counts["copied" if copy else "dropped"] += 1
    if copy:
        test.publish(message.topic, message.payload, qos=0, retain=message.retain)
    if RECORD_TRAFFIC:
        day = time.strftime("%Y-%m-%d", time.gmtime())
        append_jsonl(os.path.join(DATA_DIR, f"traffic-{day}.jsonl"), {
            "t": time.time(),
            "topic": message.topic,
            "payload": message.payload.decode("utf-8", "replace"),
            "retain": message.retain,
        })


def on_test_connect(client, _userdata, _flags, reason_code, _properties):
    print(f"test broker: connected ({reason_code})", flush=True)
    client.subscribe(SET_TOPIC)


def on_test_message(client, _userdata, message):
    match = SET_PATTERN.match(message.topic)
    if not match:
        return
    node, setting = match.groups()
    payload = message.payload.decode("utf-8", "replace")
    append_jsonl(SETS_PATH, {"t": time.time(), "node": node, "setting": setting, "value": payload})
    if setting in NOT_ECHOED:
        return

    topic = f"espresense/rooms/{node}/{setting}"
    with lock:
        overrides[topic] = payload
        save_overrides()
        counts["echoed"] += 1
    client.publish(topic, message.payload, qos=0, retain=True)


def main():
    load_overrides()
    client_id = os.environ.get("CLIENT_ID", "espresense-testnet")

    test = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id=f"{client_id}-echo")
    test.on_connect = on_test_connect
    test.on_message = on_test_message
    test.connect(os.environ.get("TEST_HOST", "broker"), 1883)
    test.loop_start()

    live = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id=client_id, userdata=test, protocol=mqtt.MQTTv5)
    live.on_connect = on_live_connect
    live.on_message = on_live_message
    if os.environ.get("LIVE_USERNAME"):
        live.username_pw_set(os.environ["LIVE_USERNAME"], os.environ.get("LIVE_PASSWORD"))
    if os.environ.get("LIVE_TLS", "false").lower() == "true":
        live.tls_set(cert_reqs=ssl.CERT_REQUIRED)
    live.connect(os.environ["LIVE_HOST"], int(os.environ.get("LIVE_PORT", "1883")))
    live.loop_start()

    while True:
        time.sleep(60)
        with lock:
            print(f"relay: {counts}, {len(overrides)} settings overridden", flush=True)


if __name__ == "__main__":
    main()
