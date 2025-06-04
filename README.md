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
- Monitors and controls automatic node optimization


![image](https://user-images.githubusercontent.com/1491145/208942192-d8716e50-c822-48a7-a6d3-46b53ab9373e.png)


## Documentation
1. [Installation Guide](https://espresense.com/companion/installation)
2. [Configuration Guide](https://espresense.com/companion/configuration)
3. [Node Setup](https://espresense.com/companion/configuration#node-placement)
4. [Optimization Guide](https://espresense.com/companion/optimization)

## Need Help?
- Join our [Discord Community](https://discord.gg/jbqmn7V6n6)
- Check the [Troubleshooting Guide](https://espresense.com/companion/troubleshooting)
- Report issues on [GitHub](https://github.com/ESPresense/ESPresense-companion/issues)

## Contributing
- Submit pull requests
- Improve our [documentation](https://espresense.com)
- See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup and quick-start instructions
