# ESPresense-companion: architecture assessment and refactor plan

_Written 2026-09-24 from a full read of `src/` (11.3k lines C#), `src/ui/` (7.7k lines Svelte/TS), `tests/`, and CI. Line numbers refer to the tree at commit `08c0eee` plus the phase-0 fixes on `refactor/phase-0-fixes`._

## 1. Honest summary

The codebase is not garbage. The pipeline shape (MQTT → `DeviceTracker` channels → `MultiScenarioLocator` → MQTT/WebSocket), the strategy pattern for locators, the lease service, the Kalman filter, the reconnect logic, the accuracy harness in the Simulation project, and the tooltip/modal/toast primitives in the UI are all sound and worth keeping.

What is wrong is everything _around_ those pieces:

- **No seams.** `State` is a god object that is also a scenario factory and a tracking-policy engine, takes `ConfigLoader` (disk I/O in its constructor) and `NodeTelemetryStore`, and is read via a public mutable `Config` field from 15+ files. Two of the hottest services bypass `IMqttCoordinator` and take the concrete class. Tests cope with `Mock<ConcreteClass>`, `virtual` methods added for tests, reflection into private fields, and 12 copies of the same 60-line setup.
- **Config hot-reload is half implemented.** Scenarios, locator weighting, and device timeouts are frozen when a device is first seen. `ConfigLoader` busy-spins at 100% CPU if the YAML is invalid, and any subscriber exception causes a once-per-second reload storm.
- **Persistence is MQTT retained messages** for all settings and a SQLite table for history that, until today, was never initialised and never read (see §3).
- **Two of everything.** Two JSON stacks (Newtonsoft + System.Text.Json, sometimes on the same property), two WebSocket server implementations, two OTA paths, two event systems plus a relay, three HTTP clients, four definitions of "current", three writers of `Device.Name`, calibration math copy-pasted into MCP and already divergent.
- **Frontend is a Svelte 4/5 hybrid** (37 components on `export let`, 5 on runes) with 41 raw `fetch` calls, a 1 ms `setInterval`, four 1 Hz pollers, ESLint that has never parsed a `.svelte` file, 83 type errors, and CI that runs only Playwright.
- **Dead and risky bits.** Four optimizers referenced nowhere, an unregistered ingress-path middleware, an AutoMapper license key committed to a public repo that expires **2026-12-13** backing two trivial maps, the MQTT password returned to every browser via `/api/state/config` and to MCP clients, and MCP tools (`delete_node`, `restart_node`, `start_firmware_update`) exposed without auth on the directly published port.

Nothing found is a data-loss bug. The things that hurt users today are, in order: locate work multiplied by node count (CPU), history silently broken, "works after restart" config behaviour, and the auto-optimize toggle that never called the API.

## 2. Target shape

```
                ┌──────────────┐     ┌────────────────────┐
   MQTT ──────▶ │ MqttGateway  │ ──▶ │ DeviceIngest       │ ──▶ dirty-set ──▶ Locator loop ──▶ Publisher (MQTT + events)
                │ (connect,    │     │ (parse, DeviceToNode│                    (ILocate per       │
                │  route only) │     │  update, telemetry) │                     scenario)         ▼
                └──────────────┘     └────────────────────┘                                  WebSocket hub
                       ▲                        ▲                                            (Channel<T> per client)
                       │                        │
                ┌──────┴───────┐        ┌───────┴────────┐        ┌──────────────┐
                │ SettingsStore│        │ RuntimeState   │        │ ConfigSource │
                │ (SQLite +    │        │ Nodes/Devices/ │ ◀───── │ immutable    │
                │  MQTT echo)  │        │ Floors         │        │ snapshot     │
                └──────────────┘        └────────────────┘        └──────────────┘
```

Principles:

1. **One immutable `Config` snapshot** swapped atomically; consumers read `IConfigSource.Current`, never a mutable field. Config changes are _applied_ to existing devices (scenarios rebuilt, timeouts updated).
2. **`State` becomes three things:** `RuntimeState` (dictionaries), `TrackingPolicy` (`ShouldTrack`/`IsExcluded` with precompiled globs), `ScenarioFactory` (builds `ILocate` instances, takes `IWeighting` and a `TimeProvider`).
3. **Interfaces at every service boundary** the tests need: `IMqttCoordinator` (already exists, use it), `INodeSettingsStore`, `IDeviceSettingsStore`, `IDeviceHistoryStore`, `ChannelReader<string>` instead of a concrete `DeviceTracker`.
4. **Domain models are not wire format.** Controllers return DTOs; `ConfigDto` omits credentials; request bodies are separate from MQTT persistence models.
5. **One JSON stack** (System.Text.Json, source-generated), **one HTTP client factory**, **one WebSocket hub**, **one OTA path** (the job service).
6. **Frontend:** a typed `api.ts` client, stores that are WebSocket-driven with a slow poll fallback (the existing `devices` store is the template), runes everywhere, leaf components first.
7. **Guardrails before big moves:** the accuracy harness (`tests/ESPresense.Companion.Simulation`), golden-file tests for MQTT payloads and HA discovery, and a `TestWorld` builder so pipeline tests are cheap.

## 3. Phase 0 — done on `refactor/phase-0-fixes` (not yet committed)

Small, confirmed, behaviour-restoring fixes. Backend changes could **not** be compiled locally (no .NET SDK on this machine); they need a CI run.

| Area | Fix | Files |
|---|---|---|
| History | Controller used a factory that built a fresh, never-initialised store per request. Singleton store only initialised on a `ConfigChanged` event that had already fired before it subscribed. Now the store applies the current config at construction, initialisation failures are logged instead of crashing the process (was `async void`), an `(Id, When)` index is added, and the controller injects the singleton. `DatabaseFactory` deleted. | `Services/DeviceHistoryStore.cs`, `Controllers/HistoryController.cs`, `Program.cs` |
| WebSocket | `changeFilter` command could never match after `ToLower()`. | `Controllers/StateController.cs` |
| WS client | Listened for `nodeChanged`; backend sends `nodeStateChanged`. Added `calibrationChanged`. Active device-message subscriptions are re-sent after a reconnect. New `unsubscribeDeviceMessage` sends the `value` key the server reads (the old call sent `deviceId`, so the server never stopped streaming). `onOpen` hook so the `showAll` filter is re-sent after reconnect. | `ui/src/lib/wsManager.ts`, `stores.ts`, `DeviceCalibration.svelte` |
| CPU | `relativeTimer` ticked every 1 ms and re-rendered every node marker; now 100 ms. | `ui/src/lib/stores.ts` |
| Robustness | `calibration` store polled every second with no error handling or in-flight guard. | `ui/src/lib/stores.ts` |
| Auto-optimize | `SlideToggle` had no click prop, so `on:click` never fired and the POST never happened. Added `onchange(checked)` to `SlideToggle`. | `ui/src/lib/SlideToggle.svelte`, `NodeCalibrationMatrix.svelte` |
| Keyboard | Map shortcuts (`0`, `+`, `-`, arrows) and Ctrl/Cmd+C were hijacked inside text inputs on every map page. | `ui/src/lib/dom.ts`, `Map.svelte`, `MapCoordinates.svelte` |
| 3D | Measured-distance spheres were added to `scene` while nodes live in the centred/rotated `contentGroup`; replaced sphere geometries were never disposed; history path filtered on a `location` field the backend never sends (rows are flat `x/y/z`, one per scenario, `best` flag). History fetch ignored the base path (broke under ingress). | `ui/src/lib/Map3D.svelte`, `types.ts`, `api.ts`, `routes/3d/[id]/+page.svelte` |

Verification: `svelte-check` 83 → 82 errors (no new ones), `vitest` 19/19, Prettier clean on touched files.

## 4. Phase 1 — hygiene and safety nets (each item S, low risk, independently mergeable)

Backend

1. Remove the `RunFrontendTests` target from `tests/ESPresense.Companion.Tests/*.csproj` (it installs Playwright browsers and runs the E2E suite on every `dotnet test`; CI already runs it separately). Remove the `pnpm install` on every Debug build in `src/*.csproj`.
2. `ConfigLoader`: advance `_lastModified` before invoking subscribers; wrap each subscriber in its own try/catch; replace the `ConfigAsync` spin loop with a `TaskCompletionSource`; move `Load()` out of the constructor into `StartAsync`.
3. `DeviceTracker`: replace `Channel<Device>` with a dirty-set keyed by device id (a device seen by 8 nodes is currently located 8× per cycle) and stop enqueueing for the locate channel when the lease is not held (unbounded growth today). Fix the dead discovery-deletion branch (`arg.AutoDiscover?.` inside `if (arg.AutoDiscover == null)`).
4. `MqttCoordinator`: catch all exceptions in `ConnectedAsync` and fault/reset the connection TCS; make `_reconnectRequired`/`_mqttClient` volatile; replace `ClientId.Contains("read")` with an explicit `mqtt.read_only` config flag.
5. Remove AutoMapper and the committed license key: replace `MappingProfile`/`MappingActions` with a static `NodeState.From(Node, telemetry)`. Two call sites.
6. Delete dead code: `CombinedOptimizer`, `JointRxAdjAbsorptionOptimizer`, `WeightedJointRxAdjAbsorptionOptimizer`, `TwoStageRxAdjAbsorptionOptimizer`; either register `FixAbsolutePaths` or delete it; drop `MathNet.Filtering*`, fix the invalid `<RuntimeIdentifiers>linux</RuntimeIdentifiers>`.
7. NaN guards: `CalculateConfidence`, `CalculatePearsonCorrelation` (one sentinel), `AbsorptionAvgOptimizer` distance ≤ 1, `OptimizationResults.Evaluate` `Log10(0)`, `Normalize` of a zero vector in NM/MLE.
8. `NodeSettingsStore` round-trip: add inbound cases for `forget_after_ms` and `count_ms`; prune `_storeByAlias` on alias change; optimistically update the cache on `Set`.

Frontend

9. ESLint: add `parserOptions.parser: tsEslint.parser` for `.svelte` (51 of 53 current "errors" are parse failures, so no `.svelte` file has ever been linted); drop `--plugin-search-dir`; run `prettier --write`; add `@types/d3-zoom`, `@types/d3-selection`, `@types/d3-interpolate`. Add `check`, `lint`, `test:unit` to `ci.yml`.
10. Adopt `lib/api.ts` for all 41 `fetch` calls (`apiFetch<T>(path, init)` with `!ok` → typed error). Removes 20 of the remaining type errors and the un-resolved calls that break under ingress.
11. Fix `TriStateCheckbox` to accept `disabled`; retype `AxisX`/`AxisY` (17 errors); `firmware.ts` writables typed as `Writable`; replace `$app/stores` with `$app/state`.

## 5. Phase 2 — boundaries (M each, medium risk, ordered)

1. **Test seams first.** Give `State` a constructor taking `Config` (keep a thin adapter from `ConfigLoader`); add a `TestWorld` builder under `tests/`; inject `TimeProvider` into `Scenario`, `DeviceToNode`, `RxNode`, `KalmanLocation`, `DeviceTracker`, `OptimizationRunner`. Delete the 12 duplicated fixtures' setup and the reflection hacks in Simulation's `MockConfigLoader`.
2. **Use the interfaces.** `IMqttCoordinator` into `MultiScenarioLocator` and `TelemetryService`; `ChannelReader`/small interface instead of concrete `DeviceTracker`; interfaces for the three settings stores and subscribe in constructors (drop `BackgroundService` from stores whose only job is attaching handlers).
3. **Async events.** Replace `Func<T,Task>` multicast events (only the last subscriber is awaited; earlier exceptions are unobserved) with an `AsyncEvent<T>` that awaits every handler. Fix the remaining `async void` handlers.
4. **Config propagation.** On `ConfigChanged`, rebuild `Device.Scenarios`, update `Device.Timeout`, push Kalman settings to scenario filters; one definition of "current" (`NodeToNode` hardcodes 30 s today).
5. **Split `State`** into `RuntimeState`, `TrackingPolicy`, `ScenarioFactory`; `Config` becomes an atomically swapped property; move `GlobalEventDispatcher` to the `Events` namespace.
6. **WebSocket hub.** Extract the 160-line loop in `StateController` into an endpoint + `WebSocketSession` fed by a `Channel<T>`, using `HttpContext.RequestAborted`, observing the receive task, and sending a periodic ping (the 15-minute keep-alive is above typical proxy idle timeouts). Same session class serves firmware progress.
7. **DTOs and errors.** `DeviceDto`, `NodeStateDto`, `ConfigDto` (no MQTT credentials), split `NodeSettings`/`DeviceSettings` request models from MQTT persistence models; `AddProblemDetails` + `UseExceptionHandler`; consistent 200/204; one `api/` prefix; drop the dead conventional route and `AddControllersWithViews`. Single `EnrichDevice` used by REST, WS, and MCP; single calibration calculation shared by REST and MCP.
8. **HTTP.** `IHttpClientFactory` named clients; `FirmwareTypeStore` fetch in a hosted service with bounded retry and refresh; remove Flurl.

## 6. Phase 3 — algorithms (M, run `accuracy check` and `multifloor` before/after every step)

1. Normalise `scenario.Error` to RMS residual in metres for every locator; fix MLE's `DistVar ?? 1e-6` fallback (currently caps MLE confidence so it never wins selection); use `nts.Online` for `possibleNodeCount` everywhere. Add `MathUtilsTests` for confidence/Pearson/RMSE with NaN inputs.
2. Collapse `NelderMead`/`MLE`/`Bfgs` into one parameterised solver with a residual strategy; move `MultiFloor` and `NadarayaWatson` onto `BaseMultilateralizer`; unify exception semantics; fix NW's "revert" that pushes another Kalman update.
3. Legacy optimizers: use `GetAdjustedRssi`, resolve TxRef from settings instead of hardcoded −59, refresh `currentSettings` after each apply, score the rounded values that are actually applied.
4. Decide on the per-scenario Kalman filter: remove it or feed it configured settings (today `filtering.max_velocity` does not bound scenario output).
5. `IOptimizer.Optimize` takes a `CancellationToken`/time budget (BFGS-B with O(P·M) numeric gradients has none). Document or replace rank-based Gaussian weighting (σ=0.3 effectively uses ~2.5 nodes).
6. Tests for the hot path: message → `DeviceToNode` → dirty set; `ProcessDevice` with two competing scenarios asserting selection; each `IOptimizer` against a synthetic snapshot with known ground truth; one `OptimizationRunner` cycle with a fake store.

## 7. Phase 4 — frontend architecture (L overall, each step S–M)

1. `nodes` and `calibration` stores become WS-driven (`nodeStateChanged`, `calibrationChanged`) with a 30–60 s poll fallback and an immediate initial fetch, following the `devices` store. Delete unused `deviceSettings` and `history` exports. Replace the three hand-rolled 1 Hz detail-page pollers with one `pollResource(path, ms)` helper.
2. Align `types.ts` with the backend (`lastSeen: string`, nullable `confidence/scale/fixes/name/room/floor`, full `NodeTelemetry`), type `wsManager` events per name, and pair with the backend `ConfigDto` so the browser stops receiving MQTT credentials.
3. Svelte 5 migration leaf-first: `*ActiveId`/`*Online` (merge four near-identical status dots into one), breadcrumbs, `FloorTabs`+`CalibrationTabs` → `PillTabs`, modals drop `createEventDispatcher` and the Skeleton `parent` shim, `svelte:component` removed.
4. Split `Map3D.svelte` (851 lines) into `scene.ts`, `rooms.ts`, `nodes.ts`, `devices.ts`, `history.ts`; delete dead bloom/composer code; one lil-gui helper for both 3D routes.
5. Split `DeviceCalibration.svelte` (735 lines): `rssiCalibration.ts` pure math (unit-testable), a capture store, presentational tables; remove the in-place `calibrationSpot.z` mutation and the per-second `fetchNodeSettings` loop.
6. Tests: unit-test `wsManager` reconnect/backoff and the real `Modal.svelte` via `@testing-library/svelte` (the current tests re-implement the logic they test); one Playwright spec that does not stub the WebSocket; fix `tooltip.spec.ts` to mock the real `auto-optimize` path.
7. Dependencies: drop `svelte-table`, `autoprefixer`, the `d3` umbrella import (three functions), vendored `lil-gui`; pin `ol` CSS to the installed version or bundle it.

## 8. Phase 5 — product and operations ("brighter future")

1. **One OTA path.** REST endpoints over `FirmwareUpdateJobService` (`POST api/node/{id}/firmware`, `GET api/firmware/jobs/{id}`), progress over the main WebSocket hub, delete `FirmwareController.Update`, prune finished jobs, fix `ESPOta`'s fire-and-forget invite and `Task<Task>`.
2. **Settings persistence.** Store node/device settings in SQLite as the source of truth and publish to MQTT as a projection, so a broker reset no longer wipes calibration. Add schema versioning/migrations (today the schema is created as a `ConfigChanged` side effect).
3. **Security posture.** Gate MCP and Swagger behind config; optional bearer token for MCP/off-ingress access; never serialise `mqtt.password` out of the process.
4. **Health.** Real checks behind `/healthz` (MQTT connected, config parsed, lease state) so the add-on healthcheck means something.
5. **One JSON stack.** Migrate MQTT payloads to System.Text.Json payload-by-payload behind golden-file tests; then delete Newtonsoft, `Flurl.Http.Newtonsoft`, and the duplicate attributes on every model. Last, because it touches every wire format.
6. **Config.** Replace the hand-written 270-line `Config.Clone` (lossy: drops `Filtering`, `DeviceRetention`, `Optimizer`, `Weights`, `Rotation`) with records; replace regex section save with a YAML AST edit that preserves comments; lock saves against the 1 s reload.

## 9. Sequencing and risk

- Phases 1 and 2.1–2.3 are prerequisites for everything else: they make the code testable and stop the two known CPU/memory sinks.
- Phase 3 must be gated on the accuracy harness; expect confidence values (and therefore scenario selection) to shift when error units are normalised.
- Phase 4 and Phase 5 can proceed in parallel with Phase 3 on separate branches; they touch disjoint code.
- Every phase is a series of small PRs. Nothing here requires a big-bang rewrite, and the existing behaviour is preserved except where a fix is explicitly called out.

## 10. Local development note

This machine currently has no .NET SDK and no `pnpm` on `PATH`. Frontend tooling works via `npx --yes pnpm@10 <script>` from `src/ui`. Backend verification needs `brew install --cask dotnet-sdk@8` or a CI run.
