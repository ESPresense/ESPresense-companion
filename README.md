# ESPresense-companion

![GitHub release (latest by date)](https://img.shields.io/github/v/release/ESPresense/ESPresense-companion)
![Docker Pulls](https://badgen.net/docker/pulls/espresense/espresense-companion)

The ESPresense-companion attempts to locate your Bluetooth Low Energy (BLE) items in the floorplan of your house. It also allows you to manage ESPresense nodes as well.

![image](https://user-images.githubusercontent.com/1491145/208942192-d8716e50-c822-48a7-a6d3-46b53ab9373e.png)

## Installation

### HAOS

To install add this repo to your add-ons store in HASS:

**Step 1: Add Repository:**

[![Open your Home Assistant instance and show the add add-on repository dialog with a specific repository URL pre-filled.](https://my.home-assistant.io/badges/supervisor_add_addon_repository.svg)](https://my.home-assistant.io/redirect/supervisor_add_addon_repository/?repository_url=https%3A%2F%2Fgithub.com%2FESPresense%2Fhassio-addons)

Click `Add`

**Step 2: Install**

[![Open your Home Assistant instance and show the Supervisor add-on store.](https://my.home-assistant.io/badges/supervisor_store.svg)](https://my.home-assistant.io/redirect/supervisor_store/)

Click `Install`, Click `Start`, Click `Show in Sidebar`

### HA Container

Example config for docker-compose

```yaml
version: '3.7'
services:
  espresense:
    image: espresense/espresense-companion
    ports:
      - 8267:8267
    volumes:
      - ./data/espresense:/config/espresense
```

## Room Measurement Guide

Start at the **bottom left corner** of the building/area, which will serve as the **origin (0,0)**. Measurements are taken from this south-west corner. When plotting the measurements, choose either a clockwise or counter-clockwise approach. For this guide, we will use a **clockwise** direction.

### Room 1

1. **First point:** `(0,0)`
2. Move north along the outside wall about 9 feet (or 3 meters). This gives the point: `(3,0)`
3. Move to the right for 12 feet (or 4 meters). This gives the point: `(3,4)`
4. Return to the outside wall. This gives the point: `(0,4)`

### Room 2

Starting with an adjacent wall, the measurements would be:

1. **First point:** `(3,0)`
2. The room is narrower, 6 feet wide (or 2 meters). This gives the point: `(5,0)`
3. The room depth is 10.5 feet (or roughly 3.5 meters). This gives the point: `(5,3.5)`
4. Return to the remaining unmarked corner. This gives the point: `(3,3.5)`

## Node Placement

To accurately determine the location of a device, it is necessary to have base station nodes positioned on the corners of the locating area (floorplan), with an additional node fairly close (1-3m). That's 5 fixes for an optimal location solution. The more fixes, the better the accuracy. The algorithm uses the distances in order from closest to farthest. The nearest distance is like 40% of the location (uses the gaussian distribution).

## Node Configuration

For ESPresense nodes you should set the maximum distance to zero to obtain distance readings from all nodes (no filtering). You can do this easily by retaining a message like this:

```markdown
key: espresense/rooms/*/max_distance/set
value: 0
```

## Fine Tuning

By hovering over the device on the map, it is possible to check if the circles align with its actual location. If the circles are too large or small, the RSS@1m value can be adjusted to improve accuracy (click on the device on map to edit).  If the device isn't seen on the map you can check the devices tab to see how many nodes are seeing it (the fixes column).  If it is only seeing one or two nodes, you can try moving the nodes closer or add more nodes.
