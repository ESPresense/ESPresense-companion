# Location fusion

Fusion chooses one location scenario for a device. Presence answers whether a node
still hears the device; a position answers where it is. Keep these separate so a
device with too few readings for a position does not appear to have left home.

## Multi-floor reading count

Attempt a `multi_floor` position solve only with at least four current readings.
With fewer readings, refresh `LastHit`, `Minimum`, and `Fixes` from the readings,
set confidence to zero, and preserve the last room, floor, and position. Report
`NoFix` and do not update the scenario's position filter.

Three ranges can leave two positions mirrored through the nodes' plane. A
numerical solver returning a point does not remove that ambiguity. Four readings
are the minimum count for this unconstrained 3D solve; they do not guarantee good
geometry or accurate distances.

Use reading count to trigger fallback, rather than a poor-fit confidence threshold.
The error, correlation, and coverage score is a heuristic, not calibrated position
accuracy. Switching to a node's position whenever that score drops can produce
jarring room changes even while a position is available.

## Scenario selection

Run the configured locators, then select the first nonempty group:

1. Current non-nearest scenarios with positive confidence.
2. Current nearest-node scenarios with positive confidence.
3. Current non-nearest scenarios retaining their last-known placement.

Rank scenarios within the selected group by smoothed probability, then confidence,
then their configured scenario order. A stationary position remains eligible:
`Locate` returning false means no movement, not necessarily no position.

Nearest node is a fallback, not a competing position estimate. Giving it confidence
1 alone does not enforce this: a smoothed prior can keep fallback selected after a
solve recovers, or keep a zero-confidence retained room selected when solving stops.
Eligibility must therefore precede probability and confidence ranking.

An ineligible scenario has zero weighted confidence and probability. It supplies
no device Kalman measurement and contributes no position-change event. When a
newly eligible group has no probability prior, seed it from normalized weighted
confidence, including when smoothing is configured to retain all prior weight.

For a device configured with only `multi_floor` and `nearest_node`:

| Current readings | Selected result | Why |
| --- | --- | --- |
| None | `not_home` | No node provides current evidence of presence. |
| One to three | Eligible nearest node; otherwise last-known placement | Presence is known, but the 3D position is underconstrained. |
| Four or more with a positive-confidence position | Multi-floor position | An available position takes priority over a node's coarse estimate. |

Handoff in both directions takes effect in the same fusion pass. For example, a
node in Entry is the fallback with three readings; a four-reading solve in Living
Room selects Living Room immediately. Dropping back to three selects Entry again,
regardless of which scenario previously had the higher probability.

## Fallback and presence

Use nearest node only when explicitly enabled. Choose the closest current reading
within its configured `max_distance`; an unset, zero, or negative limit is unlimited.
Enabling it implicitly would change the requested locator configuration and could
snap a device to a distant node without the user choosing that behavior.

Use the reading's timestamp for fallback presence. If no acceptable reading exists,
clear fallback confidence, `LastHit`, and `Minimum`, and set `Fixes` to zero. A
processing pass must not renew an old reading or retain a distance-rejected fallback.

Without an eligible fallback, `multi_floor` retains its last-known placement with
one to three readings, and `nadaraya_watson` does so with one floor-matching reading.
Keep confidence zero: knowing that the device is present does not constitute a new
position. The map requires confidence greater than 1, so this does not promise map
visibility. With no previous placement, report `not_home` rather than inventing one.

When no readings remain current, both locators clear their reading metadata and
become ineligible. Publish `not_home`; do not retain stale presence indefinitely.

## Filtering and accuracy

The highest-confidence eligible scenario supplies the device Kalman measurement.
Motion consistency weights eligible confidence before probability smoothing.
The Kalman measurement noise starts from configuration and increases for implausible
movement. Reported room and floor come from the selected scenario, while device
coordinates pass through the device Kalman filter.

Confidence is not measurement covariance. Weighting fixes by their total inaccuracy
requires calibrated positional uncertainty that accounts for ranging error and node
geometry. Coordinate smoothing alone does not smooth room transitions, because
room assignment comes from the selected scenario. Keep these distinctions when
changing filtering or introducing quality-based fallback.

## Verification

[SparseReadingPresenceTests](../tests/ESPresense.Companion.Tests/Services/SparseReadingPresenceTests.cs)
checks the one/two/three/four-reading boundary, both handoff directions with frozen
probability priors, sparse-only presence, cold start, recovery, and expiry through
tracker, fusion, and publisher. The nearest node is in Entry and the solved position
is in Living Room so the published room exposes incorrect selection.

[ScenarioFusionTests](../tests/ESPresense.Companion.Tests/Services/ScenarioFusionTests.cs)
checks eligibility, probability initialization, stationary positions, and exclusion
of unused fallback measurements and movement.
[NearestNodeTests](../tests/ESPresense.Companion.Tests/Locators/NearestNodeTests.cs)
checks reading timestamps, expiry, and distance rejection.
[MultiFloorMultilateralizerTests](../tests/ESPresense.Companion.Tests/Locators/MultiFloorMultilateralizerTests.cs)
checks that zero to three readings preserve placement without producing a new fix.
