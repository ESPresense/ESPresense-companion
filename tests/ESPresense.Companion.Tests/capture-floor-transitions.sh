#!/bin/bash
# =============================================================================
# Floor Transition Data Capture
# =============================================================================
# Records MQTT messages from ESPresense nodes during a floor transition walk.
# Run this, then physically walk between floors while narrating timestamps.
#
# Usage:
#   ./capture-floor-transitions.sh <broker-ip> <device-id> <output-file>
#
# Example:
#   ./capture-floor-transitions.sh 192.168.86.1 phone:darrell-15-pro walk-upstairs-2026-03-25.jsonl
#
# After recording, annotate ground truth by editing the companion .truth file:
#   walk-upstairs-2026-03-25.truth
#
# Format (one line per transition):
#   2026-03-25T19:30:00  First Floor
#   2026-03-25T19:30:45  Stairs        (optional: transitional label)
#   2026-03-25T19:31:15  Second Floor
# =============================================================================

set -euo pipefail

BROKER=${1:?"Usage: $0 <broker-ip> <device-id> <output-file>"}
DEVICE=${2:?"Missing device-id (e.g., phone:darrell-15-pro)"}
OUTPUT=${3:?"Missing output file (e.g., walk-upstairs.jsonl)"}
TRUTH="${OUTPUT%.jsonl}.truth"

echo "=== Floor Transition Capture ==="
echo "Broker:  $BROKER"
echo "Device:  $DEVICE"
echo "Output:  $OUTPUT"
echo "Truth:   $TRUTH"
echo ""
echo "Recording espresense/devices/$DEVICE/# and espresense/companion/$DEVICE/#"
echo "Press Ctrl+C to stop recording."
echo ""
echo "PRO TIP: Before walking, note the start time and floor."
echo "         After recording, create $TRUTH with floor annotations."
echo ""

# Create truth template
if [ ! -f "$TRUTH" ]; then
    echo "# Ground truth floor annotations" > "$TRUTH"
    echo "# Format: ISO-timestamp  FloorName" >> "$TRUTH"
    echo "# $(date -Iseconds)  First Floor    # started recording" >> "$TRUTH"
fi

# Subscribe to both raw device measurements AND companion output
# Raw: espresense/devices/{device}/{node} — has RSSI, distance per node
# Companion: espresense/companion/{device} — has computed state
mosquitto_sub -h "$BROKER" \
    -t "espresense/devices/$DEVICE/#" \
    -t "espresense/companion/$DEVICE" \
    -t "espresense/companion/$DEVICE/attributes" \
    -v | while IFS= read -r line; do
        # Prepend ISO timestamp to each line
        echo "{\"tst\":\"$(date -Iseconds)\",\"raw\":\"$line\"}"
    done >> "$OUTPUT"
