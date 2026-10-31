# Test net

Runs any branch of Companion against a copy of live MQTT traffic, without letting it touch the house.

```
live broker ──read-only──▶ relay ──▶ test broker ◀──▶ Companion under test
                             ▲            │              (no route out)
                             └── echoes ──┘
                               settings writes
```

- **One way.** The relay only subscribes to the live broker. Use a live account that can only read `espresense/#` too, so that holds even if the relay is wrong.
- **No way out.** Companion sits on an `internal` Docker network with the test broker. It has no route to the live broker or your LAN, and its config never contains the live credentials. The `ui` proxy is the only way in.
- **Copied:** `espresense/devices/#`, `espresense/rooms/#`, `espresense/settings/#`.
- **Not copied:** `espresense/companion/#`, because live leases would stop the test build optimizing, and `/set` commands. HA discovery isn't copied either.
- **Echo.** Real nodes publish a setting back after applying it. The relay does this on the test broker, then ignores live values for that setting, so a calibration the test build chose stays in place.
- **Not simulated yet.** Echoed calibrations don't change incoming readings, because `rssi` and `distance` still come from the real nodes using their real settings. The relay is where that closed loop will go.

## Use

Copy `.env.example` to `.env` and fill it in, then:

```bash
testnet/up.sh <branch-or-sha>      # build and start; prints the UI address
testnet/report.sh <branch-or-sha>  # markdown report for the PR
testnet/down.sh <branch-or-sha>    # remove the stack and its data
```

Each ref gets its own stack, so builds can run side by side. Reports contain house node ids, so keep them out of public PRs unless they're anonymised.

`RECORD_TRAFFIC=true` also saves every live message in the data volume, ready for CI replay fixtures. Those files hold raw house ids and need anonymising first.

## Check it

`testnet/smoke.sh` builds the current commit and proves the isolation against a fake live broker. Run it after changing anything here.
