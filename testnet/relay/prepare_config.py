"""Copies a house config for the test net, pointing mqtt at the test broker and nothing else."""
import os
import sys

import yaml

source, target = sys.argv[1:3]
with open(source) as f:
    config = yaml.safe_load(f) or {}

discovery_topic = (config.get("mqtt") or {}).get("discovery_topic", "homeassistant")
config["mqtt"] = {
    "host": "broker",
    "port": 1883,
    "ssl": False,
    "client_id": "espresense-testnet",
    "discovery_topic": discovery_topic,
}

os.makedirs(os.path.dirname(target), exist_ok=True)
with open(target, "w") as f:
    yaml.safe_dump(config, f, sort_keys=False)
