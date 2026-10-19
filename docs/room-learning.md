# Teach room predictions from Home Assistant

Room learning is disabled by default. To enable it, add to `config.yaml`:

```yaml
room_learning:
  enabled: true
```

When enabled, each tracked device gets a **Room (correct to teach)** MQTT select entity in Home
Assistant. Its dropdown follows Companion's predicted room. If it is wrong,
select the room the device is actually in. Stay in that room for about 10 seconds
before correcting it, so the recent signal readings describe the right room.

Options come from Companion's configured rooms. Use unique room names. A room's
floor and ID are stored internally, so changing its display name keeps its examples.
An ambiguous duplicate room name is rejected rather than guessed.

A correction labels the preceding 10 seconds of raw RSSI readings. At least three
receivers must have reported within the last three seconds. Companion holds an
accepted correction for 10 seconds, then resumes prediction. It learns only when
it receives a command; publishing its own predicted state never trains the model.
The dropdown is unknown when there is no current room prediction. Its attributes
retain the device's `floor`, and the main device state reports the floor name when
available. Floors are not dropdown options: selecting one cannot teach a room.
Disabling room learning stops corrections and overrides; the next locator update
removes each device's dropdown while preserving its saved examples.

Examples are specific to the device and saved in `room-fingerprints.json` beside
`config.yaml`. Each device keeps up to 200 examples. A nearly identical new example
replaces an old one, including its label, so correcting a mistake can undo it.
Similar signal patterns can then improve the reported room; weak matches and ties
fall back to the existing coordinate locator. This is a conservative initial matcher,
not a guarantee that one correction will distinguish every room or device orientation.

Room learning does not change device calibration, anchor a device, write coordinate
fingerprints, or move its map coordinates. The reported room and floor can therefore
differ from the room containing the existing coordinate estimate. Use **Teach this
location** on the map when you know an exact position and want coordinate fingerprints
or a calibration suggestion. Anchored devices continue to use their anchor and ignore
room-learning commands.

## MQTT behavior

The entity uses Home Assistant's [MQTT Select](https://www.home-assistant.io/integrations/select.mqtt/):

- State: `espresense/companion/<device-id>/room`
- Command: `espresense/companion/<device-id>/room/set` (plain room name, not retained)
- Attributes: `espresense/companion/<device-id>/room/attributes`

State and discovery are retained. Commands must not be retained: retained commands
are ignored so reconnection cannot teach an old location. Repeated commands for the
same room within 30 seconds are deduplicated. Corrections waiting more than 15 seconds
for the active locator are discarded. Only the instance holding the locating lease
saves examples and publishes predictions; examples reside in that instance's config
directory, so failover instances need access to the same training data to learn from
earlier corrections.

The attributes identify `position`, `correction`, or `learned` as the room source.
If the training file cannot be read or written, the existing locator continues and
an error is reported without overwriting that file. Deleting a device clears its
examples and dropdown discovery. Removing it from tracking removes the dropdown
but preserves its examples for later use.
