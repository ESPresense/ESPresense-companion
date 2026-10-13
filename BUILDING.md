# Building

Git Clone, open the folder in VSCode and install dependencies when prompted. You will need to install the .NET SDK 8.0 and Node.js 22. Install pnpm globally using `npm install -g pnpm`, run `pnpm install` in `src/ui` once (the Debug build does not do this for you), then type into the terminal:

`dotnet watch --project src`

Browse to [http://localhost:5279/]

While developing you use vite as your webserver and it proxies /api and /ws to the dotnet webserver.

On deployment it's the opposite, the dotnet server runs and serves up precompiled html and js files.

## Running read-only against a live broker

To debug against your real MQTT broker without a dev instance changing anything, set a `client_id` containing `read` (any case) in the dev config's `mqtt` section:

```yaml
mqtt:
  client_id: espresense-companion-readonly
```

A read-only client still subscribes to everything, but publishes nothing: no `espresense/companion/status`, no discovery, no node settings. Each publish it skips is logged as `ReadOnly, would have sent to <topic>: <payload>`. Use a different client ID from your production Companion, or the broker disconnects one of them.

One thing still gets out: the connection registers a retained last will of `offline` on `espresense/companion/status`. If the dev instance drops without a clean disconnect (a crash, or killing the process), the broker publishes it and your production Companion shows as offline until it next reconnects.

Join us on discord for development talk: [https://discord.gg/jbqmn7V6n6]
