# ESPresense-companion
![GitHub release (latest by date)](https://img.shields.io/github/v/release/ESPresense/ESPresense-companion)
![Docker Pulls](https://badgen.net/docker/pulls/espresense/espresense-companion)

A Home Assistant Add-on / Docker container that solves indoor positions using MQTT data received from multiple ESPresense nodes. The companion is the central brain of your ESPresense system. It:
- Processes distance readings from all nodes using trilateration to determine device locations
- Reports device room presence to Home Assistant via MQTT
- Visualizes BLE device locations on your floorplan
- Manages and configures ESPresense nodes
- Updates node firmware
- Adjusts device-specific settings
- Publishes Bayesian room probabilities for fuzzy automations
- Monitors and controls automatic node optimization

![image](https://user-images.githubusercontent.com/1491145/208942192-d8716e50-c822-48a7-a6d3-46b53ab9373e.png)

## Documentation
1. [Installation Guide](https://espresense.com/companion/installation)
2. [Configuration Guide](https://espresense.com/companion/configuration)
3. [Node Setup](https://espresense.com/companion/configuration#node-placement)
4. [Optimization Guide](https://espresense.com/companion/optimization)

## Bayesian probability output

Enable the optional Bayesian publisher to expose per-room probability vectors alongside the traditional `device_tracker` state.
Set `bayesian_probabilities.enabled: true` in `config.yaml` to turn it on:

```yaml
bayesian_probabilities:
  enabled: true
  discovery_threshold: 0.1   # auto-create sensors above this probability
  retain: true               # keep MQTT state so HA restores after restart
```

When enabled the companion:

- Publishes `espresense/companion/<device_id>/probabilities/<room>` topics containing a `0.0-1.0` float for each room.
- Adds a `probabilities` object to the device attribute payload (`espresense/companion/<device_id>/attributes`).
- Auto-discovers Home Assistant `sensor` entities for any room whose probability crosses the configured threshold.

You can fuse multiple device probabilities into a person-level Bayesian sensor in Home Assistant:

```yaml
sensor:
  - platform: bayesian
    name: "Pat in Kitchen"
    prior: 0.5
    observations:
      - platform: template
        value_template: "{{ states('sensor.pat_phone_kitchen_probability') | float }}"
        probability: 0.6
      - platform: template
        value_template: "{{ states('sensor.pat_watch_kitchen_probability') | float }}"
        probability: 0.9
```

Automations can then trigger on thresholds (for example, turn on lights when `sensor.pat_in_kitchen > 0.7`).

## Need Help?
- Join our [Discord Community](https://discord.gg/jbqmn7V6n6)
- Check the [Troubleshooting Guide](https://espresense.com/companion/troubleshooting)
- Report issues on [GitHub](https://github.com/ESPresense/ESPresense-companion/issues)

## Contributing
- Submit pull requests
- Improve our [documentation](https://espresense.com)
