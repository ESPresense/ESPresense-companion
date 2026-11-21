# #2220 room-flip fixture

Regression lock for *"wrong room despite stable XYZ"* — [ESPresense#2220](https://github.com/ESPresense/ESPresense/issues/2220),
originally [discussion 1817](https://github.com/ESPresense/ESPresense/discussions/1817) (`issues/1817` is a 404; it was never an issue).

Real captured data, not synthetic. Contributor `plackettsj1`, firmware v4.0.6, 18 × ESP32-C3,
four static iBeacons at surveyed coordinates. Driven by `RoomFlip2220Tests`.

## Provenance

| File | Source | sha256 (first 16) |
|---|---|---|
| `green-2026-05-27T11.replay.jsonl` | [mqtt_2026052711.log](https://github.com/user-attachments/files/28314077/mqtt_2026052711.log) | `d6c6e814fcfaafe5` |
| `green-2026-05-26T14.replay.jsonl` | [mqtt_2026052613.log](https://github.com/user-attachments/files/28278804/mqtt_2026052613.log) + [mqtt_2026052614.log](https://github.com/user-attachments/files/28278803/mqtt_2026052614.log) | `a56f9d9f888b62a0`, `130a2b02bd1523a5` |
| `scenario.yaml` | [definitions.txt](https://github.com/user-attachments/files/25887337/definitions.txt) | `4607e453c78e25c2` |

Extraction rule, so this is reproducible from the uploads: keep every line whose topic contains
the Green Mint beacon id, within the time range below, in capture order; emit the MQTT payload's
`rssi`, `rssi@1m`, `rxAdj`, `rssiVar`, `distance`, `var`, `int` alongside the log timestamp and
the node name from the topic's last segment. No filtering, smoothing or de-duplication.

- `green-2026-05-27T11`: `2026-05-27T11:00:00` → `11:12:42` (1287 records, 9 stations)
- `green-2026-05-26T14`: `2026-05-26T13:55:00` → `14:02:35` (869 records, 8 stations)

## Measured behaviour, 2026-11-21

Both fixtures, deterministic across repeated runs:

| Arm | `locators` | Room reported | Verdict |
|---|---|---|---|
| as-captured | `nadaraya_watson` per-floor × 3 floors | **Dining Room** (Lower Ground Floor), constant | wrong floor entirely |
| shipped fix | `multi_floor` | **Kitchen** (Ground Floor), constant | matches the survey |

So the fixture discriminates: the config the reporter was running puts a static beacon on the
wrong floor, and `multi_floor` puts it in the surveyed room. That difference is the regression
value — if a change makes the `multi_floor` arm report anything but a constant Kitchen, it has
regressed #2220.

Note what the discrimination is *not*: "flips within the window" versus "does not". It is
"constantly wrong" versus "constantly right". See below for why the corpus cannot show the flip
itself. The as-captured arm is therefore recorded, not asserted — asserting that it stays wrong
would lock in the broken behaviour and fail the day someone fixes the per-floor path.

## What it can and cannot prove

**Can:** that the allocator does not change the published room for a static beacon while its own
distance inputs are stable. That is the #2220 symptom and it is what the tests assert.

**Cannot:** that a given build *reproduces* the flip. The flip timestamps come from the reporter's
Home Assistant history, which was never uploaded — the corpus contains only raw node observations
(`espresense/devices/…`), no allocator output. The expected room is derived from the survey, not
from a recorded allocator decision. A build that never flips is indistinguishable here from a
build that cannot flip.

**Cannot:** anything about coordinate accuracy. The as-quoted surveyed XY disagrees with the RSSI
geometry for Blue and Green (Pearson(rssi, log₁₀d) −0.17 at the quoted point vs −0.91 transposed).
The *room* label survives both frames, so room-level truth holds and coordinate-level truth does
not. Assert on rooms only. `distance` and `var` are carried for provenance; no assertion reads them.

## Why only Green Mint

Of the four captured flip events, two qualify and both are Green Mint. Blue's and Orange's inputs
move several metres across the flip window, so "the room changed while distances were stable" is
not what their traces show. Yellow's reported room is the *correct* one, so there is no wrong-room
event to assert on, and its surveyed Z sits 0.21 m from a floor boundary. Full per-log verdicts:
ESPA-570. Do not pad the corpus with the other events.
