# Building

Git Clone, open the folder in VSCode and install dependencies when prompted. You will need to install the .NET SDK 8.0 and Node.js 22. Install pnpm globally using `npm install -g pnpm`, run `pnpm install` in `src/ui` once (the Debug build does not do this for you), then type into the terminal:

`dotnet watch --project src`

Browse to [http://localhost:5279/]

While developing you use vite as your webserver and it proxies /api and /ws to the dotnet webserver.

On deployment it's the opposite, the dotnet server runs and serves up precompiled html and js files.

## Release channels and image tags

| Channel | Built from | Image tags (`espresense/espresense-companion`) | Home Assistant |
|---|---|---|---|
| PR | each push to a PR in this repo | `pr-<number>`, `pr-<number>-<sha7>` | — |
| Edge | each merge to `main` | `edge`, `main-<sha7>` | — |
| Beta | a `vX.Y.Z-beta.N` prerelease | `X.Y.Z-beta.N`, `beta` | ESPresense Companion - BETA add-on |
| Stable | a `vX.Y.Z` release | `X.Y.Z`, `latest` | ESPresense Companion add-on |

PR and edge images never move `beta`, `latest` or a version tag. Pin a soak to `pr-<number>-<sha7>` or `main-<sha7>`.

Releasing is one workflow, **Actions → Release → Run workflow**:

- **beta** tags `main` (or the `ref` you give) as the next `vX.Y.Z-beta.N` prerelease, where `X.Y.Z` is Release Drafter's draft version. Its notes list the PRs new since the previous beta.
- **stable** publishes the draft as `vX.Y.Z` at the newest beta's commit, so stable is exactly what beta users ran. The notes drop PRs merged after that commit. Pass `ref` to release a different commit of `main`.

Both build the image and notify `ESPresense/hassio-addons`, which moves the add-ons. Prereleases skip the ESPresense.com notification. Publishing a release by hand in the GitHub UI still works; tick "Set as a pre-release" and name it `vX.Y.Z-beta.N` for a beta. To preview either button, run `DRY_RUN=1 .github/scripts/release.sh beta|stable [ref]` locally.

## Running read-only against a live broker

To debug against your real MQTT broker without a dev instance changing anything, set `read_only: true` in the dev config's `mqtt` section, with a `client_id` that differs from your production Companion (the broker disconnects one of two clients sharing an ID):

```yaml
mqtt:
  client_id: espresense-companion-dev
  read_only: true
```

A read-only client still subscribes to everything, but publishes nothing: no `espresense/companion/status`, no last will, no discovery, no node settings. Each publish it skips is logged as `ReadOnly, would have sent to <topic>: <payload>`. For backward compatibility a `client_id` containing `read` (any case) also enables read-only mode, but that heuristic is deprecated: it logs a warning at startup and will be removed, so set `read_only: true` explicitly.

## Logging and JSON conventions

Two rules, each enforced by a test under `tests/ESPresense.Companion.Tests/Conventions/` (#1846):

**Logging.** Take an `ILogger<T>` in the constructor. The static `Serilog.Log` is for the `Program.cs` bootstrap only: it can't be asserted in tests, and its entries have no `SourceContext`, so Serilog's per-namespace minimum levels don't apply to them. Tests can assert on what a class logs with `FakeLogger<T>` (`Microsoft.Extensions.Diagnostics.Testing`). A class that constructs other types hands them a logger from its own `ILoggerFactory`, so what it builds still logs under its own category: `DeviceTracker` gives the devices it creates an `ILogger<AutoDiscovery>`, and `ScenarioFactory` gives each locator type its own. A type built without a logger discards its entries (`NullLogger`), which is what the simulation tools and the log-assertion-free tests rely on. Message templates use named placeholders (`{NodeId}`), never numeric ones: `Microsoft.Extensions.Logging` binds placeholders to arguments by position, so `{1}` takes the first argument, unlike Serilog's `Log.*`, where it is an index. To log an object, destructure it (`{@settings}`) rather than serializing it inside the call: Serilog escapes line breaks on the way out, so destructuring is the form that can't forge a log line, it keeps the value queryable in the sink, and it costs nothing when the level is off (Serilog checks the level before it destructures; a `JsonSerializer.Serialize` argument runs either way). One limit: destructuring reflects over properties and never consults the JSON attributes, so the `[JsonIgnore]` that keeps the broker password out of every JSON boundary does not reach logs. Never `{@}` a `Config` or `ConfigMqtt`.

**JSON.** System.Text.Json for everything the Companion owns, using the shared options in `JsonDefaults` rather than a `new JsonSerializerOptions` per call:

| Options | Boundary |
| --- | --- |
| `JsonDefaults.Web` | REST API (applied to ASP.NET's own options in `Program.cs`) and MCP tools and resources, which return exactly the REST JSON (#1849) |
| `JsonDefaults.WebSocket` | `/ws` events and commands |
| `JsonDefaults.Mqtt` | what the Companion publishes to MQTT: HA discovery, telemetry, device settings |
| `JsonDefaults.Verbatim` | the MQTT `last_seen` stamp and firmware-update progress frames |
| `JsonDefaults.MqttRead` | reading JSON other publishers wrote to MQTT (HA discovery messages) |

They differ on purpose: each reproduces what its boundary has always sent (REST enums are strings, WebSocket enums are numbers). The MQTT ones reproduce Newtonsoft's output byte for byte, including its string escaping and its `2.0`-style doubles, through the converters in `Converters/NewtonsoftCompatible.cs`. Changing any of them is a wire-format change.

Newtonsoft.Json remains only here, and `NewtonsoftConventionTests` fails if it spreads:

- **HA Supervisor API** (`Program.cs`: `FlurlHttp.Clients.UseNewtonsoft()`, used by `SupervisorConfigLoader`). A third-party contract: the Supervisor returns `port` as a number into a string member, which Newtonsoft coerces and System.Text.Json would reject.
- **MQTT parsing in `Services/Mqtt/MqttMessageHandlers.cs`** (device messages, node telemetry, device settings, device attributes), the `JsonSerializationException` it throws that `MqttCoordinator` catches, and the Newtonsoft attributes on `DeviceMessage`, `NodeTelemetry` and `DeviceSettings` that parse depends on. The attributes arrive as `Dictionary<string, object>`, and `DeviceTracker` restores `last_seen` only when Newtonsoft has already turned it into a `DateTime`; an STJ `JsonElement` would be skipped. Deferred until #1844 changes `DeviceTracker`; `JsonDefaults.MqttRead` is the intended replacement, with the parse pinned first.

Join us on discord for development talk: [https://discord.gg/jbqmn7V6n6]
