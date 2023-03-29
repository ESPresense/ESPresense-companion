# ESPresense-companion

![GitHub release (latest by date)](https://img.shields.io/github/v/release/ESPresense/ESPresense-companion)
![Docker Pulls](https://badgen.net/docker/pulls/espresense/espresense-companion)

*This is a work in progress!*

It attempts to locate your BLE items in the floorplan of your house.  It will eventually be extended to allow you to manage
ESPresense nodes as well.

![image](https://user-images.githubusercontent.com/1491145/208942192-d8716e50-c822-48a7-a6d3-46b53ab9373e.png)

# Installation

To install add this repo to your add-ons store in HASS:

Step 1: Add Repository:

[![Open your Home Assistant instance and show the add add-on repository dialog with a specific repository URL pre-filled.](https://my.home-assistant.io/badges/supervisor_add_addon_repository.svg)](https://my.home-assistant.io/redirect/supervisor_add_addon_repository/?repository_url=https%3A%2F%2Fgithub.com%2FESPresense%2Fhassio-addons)

Click Add

Step 2: Install

[![Open your Home Assistant instance and show the Supervisor add-on store.](https://my.home-assistant.io/badges/supervisor_store.svg)](https://my.home-assistant.io/redirect/supervisor_store/)

Click Install, Click Start, Click Show in Sidebar

# Node Placement

To accurately determine the location of a device, it is necessary to have base station nodes positioned on the corners of the locating area (floorplan), with an additional node fairly close (1-3m). That's 5 fixes for an optimal location solution.  The more fixes, the better the accuracy. The algorithm uses the distances in order from closest to farthest. The nearest distance is like 40% of the location (uses the gaussian distribution).

# Node Configuration

For ESPresense nodes you should set the maximum distance to zero to obtain distance readings from all nodes (no filtering).  You can do this easily by retaining a message like this:

```
key: espresense/rooms/*/max_distance/set
value: 0
```

# Fine Tuning

By hovering over the device on the map, it is possible to check if the circles align with its actual location. If the circles are too large or small, the RSS@1m value can be adjusted to improve accuracy.  If the device isn't seen on the map you can check the devices tab to see how many nodes are seeing it (the fixes column).  If it is only seeing one or two nodes, you can try moving the nodes closer or add more nodes.
