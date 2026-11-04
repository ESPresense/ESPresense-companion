# Building

Git Clone, open the folder in VSCode and install dependencies when prompted. You will need to install the .NET SDK 8.0 and Node.js 22. Install pnpm globally using `npm install -g pnpm`, run `pnpm install` in `src/ui` once (the Debug build does not do this for you), then type into the terminal:

`dotnet watch --project src`

Browse to [http://localhost:5279/]

While developing you use vite as your webserver and it proxies /api and /ws to the dotnet webserver.

On deployment it's the opposite, the dotnet server runs and serves up precompiled html and js files.

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

**Logging.** Take an `ILogger<T>` in the constructor. The static `Serilog.Log` is for the `Program.cs` bootstrap only: it can't be asserted in tests, and its entries have no `SourceContext`, so Serilog's per-namespace minimum levels don't apply to them. Tests can assert on what a class logs with `FakeLogger<T>` (`Microsoft.Extensions.Diagnostics.Testing`). Until #1844 and #1845 merge, `State`, `DeviceTracker` and `MultiScenarioLocator` are exempt, and the types they construct (the locators, `AutoDiscovery`) fall back to the transitional `DeferredLoggerFactory` when no logger is passed. Message templates use named placeholders (`{NodeId}`), never numeric ones: `Microsoft.Extensions.Logging` binds placeholders to arguments by position, so `{1}` takes the first argument, unlike Serilog's `Log.*`, where it is an index.

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
- **Device attribute publishing in `MultiScenarioLocator`** with `SerializerSettings.NullIgnore` (`Utils/JsonSerializerSettings.cs`). Deferred until #1845 merges; `JsonDefaults.Mqtt` produces the same bytes.

Join us on discord for development talk: [https://discord.gg/jbqmn7V6n6]
