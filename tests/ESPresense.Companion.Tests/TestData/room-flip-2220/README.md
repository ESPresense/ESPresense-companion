# room-flip-2220 — the #2220 wrong-room fixture

Field data for [ESPresense/ESPresense#2220](https://github.com/ESPresense/ESPresense/issues/2220),
*"Room flip-flopping in vertically stacked rooms with stable XYZ"*.

Captured and contributed by **@plackettsj1**, posted on that thread on 2026-11-25
([comment](https://github.com/ESPresense/ESPresense/issues/2220#issuecomment-7162642552)):
one unbroken capture of `espresense/devices/#` **and** `espresense/companion/#` on one clock, plus
the `config.yaml` that produced it. Companion **2.6.8**.

Everything in `room-flip-2220.json` is derived from those two attachments. Nothing is synthesised.

## Why this capture and not the older ones

The six MQTT logs the same contributor posted in May 2026 carry only `espresense/devices/#` — every
node's *view* of each beacon, but not the Companion's own answer. They can show that a room is
wrong; they cannot show that a room **keeps changing while the beacon hangs on a nail**, which is
what #2220 is actually about.

This capture has both topic trees, so for the first time the allocator's output is in the corpus.

## What it proves, and it is stronger than "the room flips"

In 681 seconds, with four static beacons:

| beacon | reports | room changes | rooms it visited | state == room containing its own `x,y,z` |
|---|---|---|---|---|
| Green  | 2822 | 324 | Sitting Room, Bedroom One, Lower Ground Floor Hallway, Shower Room, Bedroom Two | 802 (28.4%) |
| Blue   | 1083 | 156 | Kitchen, Dining Room, Bathroom | 299 (27.6%) |
| Yellow |  920 | 148 | Kitchen, Dining Room, Bathroom | 199 (21.6%) |
| Orange |  900 | 126 | Kitchen, Dining Room, Bathroom | 240 (26.7%) |

The last column is the finding. `DeviceReport.From` builds the state topic and the attributes
payload from **one** snapshot of **one** device in **one** call, so each `reports[]` record is an
atomic pair. Within that pair:

- `state` comes from `Device.Room` → `BestScenario.Room` — the winning per-floor scenario's own
  room, from that scenario's unfiltered solve.
- `x`, `y`, `z` come from `Device.Location` → the **Kalman-filtered** position.

Two different estimates, published together as if they agreed. They do not. The filtered position
is rock steady — per-beacon standard deviation **0.04–0.22 m on every axis across the whole
capture** — and resolves into exactly one room for every single report of every single beacon
(`expect.solvedRoom` has one entry per beacon, with the full report count against it). The room
*label* meanwhile walks the vertical stack above and below that point.

So the bug does not need external ground truth to state. The fixture contradicts itself.

Independently, the contributor's stated truth agrees with the coordinates and not the label:
**Green was in the Sitting Room for the entire capture**, and all 2822 of Green's reports put its
coordinates in the Sitting Room. The solver is right; the labeller is wrong. He also runs his own
`shapely` bounds checker against real XYZ and has seen no genuine coordinate excursion for weeks,
including during this capture.

## Assertions to write against it

**A — label/coordinate consistency (data only, no replay, no calibration). Landed.**
`Locators/RoomFlip2220FixtureTests.cs` resolves each `reports[]` record's own `(x, y, z)` through
the production path (`SpatialUtils.FindFloorAndRoom`) against this fixture's geometry and pins the
counts from `expect`. It needs no solver, no node settings and no replay harness, and it asserts on
precisely the component that is broken.

It is **green on the broken behaviour** on purpose: a red build left behind as a monument helps
nobody, and the pinned count says more than a failure would. A build that fixes #2220 takes every
beacon's `stateEqualsSolvedRoom` to its full report count and trips the test — that is the
regression lock. When it fires, update `expect` and say which way the numbers moved.

**B — no-flip under replay.** Drive `readings[]` through the production locators under this
fixture's geometry and locator set, and assert the published room for a beacon is identical at every
publish across a window. Both halves are required:

```
rooms = [published_room(B, t) for t in publishes(W)]
assert len(set(rooms)) == 1          # zero transitions
assert rooms[0] == truthRoom         # and the right one (Green only; see "Limits")
```

`len(set(rooms)) == 1` alone would pass a build that parks every tag in one room forever.

Suggested windows, picked for input-side stability rather than for a dramatic flip count:

| arm | beacon | window | why |
|---|---|---|---|
| canonical | Green  | `t` 495–681 (186 s) | 84 room changes over 4 rooms; 12 stations with ≥8 samples; reporter-stated truth room; published `x/y/z` sd 0.11 / 0.11 / 0.06 m |
| secondary | Yellow | `t` 195–285 (90 s)  | 18 room changes; every station inside the ESPA-570 stability gate as written (max sd 0.64 m, max p95−p5 2.18 m, max gap 23 s) |

**Stability precondition, revised.** ESPA-570 gated on per-station input `sd ≤ 0.75 m` and
`p95 − p5 ≤ 2.5 m`. On the canonical Green window that gate fails on exactly one station,
`bedroomtwo1`, at a mean range of **11.2 m** — beyond this config's own `nearest_node.max_distance:
10`. Two changes, both tightening rather than loosening:

1. Apply the input gate only to stations whose mean range is inside `nearest_node.max_distance`.
   Eleven of Green's twelve stations then pass with max sd 0.70 m.
2. Prefer gating on the Companion's **published** `x/y/z` stability (`sd ≤ 0.25 m` per axis over the
   window). It is measured on the allocator's own output, so it cannot be satisfied by a build that
   destabilises its inputs, and `expect.windows` pins it per beacon.

As with ESPA-570: if the gate fails, the test is **void**, not failing. It exists so the no-flip
assertion can never pass vacuously.

**C — the `multi_floor` arm.** This config has **no** `locators.multi_floor`; it is
`nadaraya_watson` + `nearest_node`, which is the configuration #2220 was filed against and the one
the contributor runs by choice. So this fixture is the **flipping** arm that ESPA-24 needs and the
May corpus could not provide. For the non-flipping arm, replay the same `readings[]` with
`multi_floor` enabled. Watch **Yellow**: the contributor names it as the beacon that was *always*
wrong when he field-tested `multi_floor`.

## Limits — read these before asserting anything

- **Only Green has a stated truth room.** Blue, Yellow and Orange have none *for this capture*. Do
  not reach for the May 2026 surveyed coordinates to supply one: Green's May point `(0.2, 3.7, 4.5)`
  is in the Kitchen and it is in the Sitting Room here, so at least one beacon moved between the two
  captures and the May survey does not describe this one. Assertion A needs no truth room at all;
  assertion B's second half is Green-only unless the contributor supplies more.
- **Not bit-reproducible.** The converged per-node `absorption` / `rx_adj_rssi` / `tx_ref_rssi` in
  effect at capture time is in neither attachment, and the config has `optimization.enabled: true`
  with `per_node_absorption`. A replay will not reproduce these exact coordinates. It does not need
  to: assertion A reads the recorded output, and assertion B asserts self-consistency, not absolute
  accuracy. **This fixture cannot support an "is the position correct" test.**
- **Do not assert on `readings[].dist`.** It is the node's own number, carried for provenance.
  `rssi`, `refRssi` and `rxAdj` are the inputs; let the Companion derive range.
- **Timestamps are whole seconds.** `mosquitto_sub -F '%I'` has 1 s resolution, and many records
  share a `t`. `reports[]` and `readings[]` are in wire order — **pair by array index, never by
  `t`.** Pairing by timestamp instead of by order silently degrades
  `expect.consistency.*.stateOnBestScenarioFloor` from 100% to 73–90%; that 100% is the fixture's
  own check that the pairing is right, since `state` and `best_scenario` come from the same
  scenario by construction.
- **Span is 681 s, not an hour.** The contributor described it as "an hours worth of data"; the file
  runs 10:40:12–10:51:33 UTC. 62231 lines, **0 malformed**. No part of the analysis above needs an
  hour, so this was not worth going back to him for — but do not cite it as a one-hour capture.
- **Every node lists all three floors**, exactly as configured — see `nodeFloorsNote`. That is
  load-bearing. A config with one floor per node does not reproduce #2220, which is why this
  contributor's own March 2026 workaround stopped his flipping.
- **Zero `<floor-only>` reports.** The ~1 m² strip of Lower Ground Floor covered by no room is a
  real utility void in a partially-underground floor, confirmed by the contributor — the geometry
  as configured is correct, and nothing in this capture lands there.
- **Room and node names are the contributor's own**, from the `config.txt` he published on the
  thread, kept so the fixture can be cross-read against #2220. He redacted broker credentials and
  GPS himself; `gps.report` is `false`. Anonymising to the `n01`/`r01` scheme `robot-survey` uses is
  a single mechanical pass if maintainers would rather.

## Files

| file | what |
|---|---|
| `room-flip-2220.json` | the fixture: geometry, nodes, beacons, `reports[]` (Companion output), `readings[]` (node observations), `expect` (pinned measurements) |
| `verify_expectations.py` | recomputes every number in `expect` from the fixture's own records and geometry, and diffs. No network, no .NET. `python3 verify_expectations.py` |
| `../../Locators/RoomFlip2220FixtureTests.cs` | the characterization tests (assertion A) |

`verify_expectations.py` is a second, independent implementation of the containment used by the C#
test, so the pinned numbers are checkable without a build and the two agree or one of them is
wrong. They currently agree to the report: 802 / 299 / 199 / 240. Keeping both is cheap; if that
stops being true, drop the Python one rather than loosening the C# one.
