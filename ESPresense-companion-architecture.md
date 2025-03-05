# ESPresense-companion Architecture Documentation

This document describes the architecture of ESPresense-companion, focusing on the interaction between the C# backend and Svelte frontend, with particular attention to data flow, WebSocket communication, and store management. This guide is designed to help AI agents and developers understand how to read and write data, and how to create new UI components.

## Overall Architecture

ESPresense-companion follows a client-server architecture with:

1. **C# Backend**: ASP.NET Core application that:
   - Processes MQTT data from ESPresense nodes
   - Calculates device positions using trilateration
   - Manages device tracking and history
   - Interfaces with Home Assistant
   - Provides REST and WebSocket APIs

2. **Svelte Frontend**: SvelteKit application that:
   - Visualizes device locations on floorplans
   - Provides UI for device and node management
   - Uses reactive stores for state management
   - Communicates with backend via REST API and WebSockets

## Backend (C#) Architecture

### Core Components

1. **Controllers**: Handle HTTP and WebSocket endpoints
   - `DeviceController`: Device management
   - `NodeController`: Node configuration and management
   - `StateController`: System state and WebSocket communication
   - `HistoryController`: Historical data
   - `FirmwareController`: Firmware updates

2. **Services**: Implement business logic
   - `MqttCoordinator`: Manages MQTT communication
   - `DeviceTracker`: Processes device data
   - `MultiScenarioLocator`: Implements location algorithms
   - Various stores (Device, Node, Settings, Telemetry)

3. **Models**: Define data structures
   - `Device`: BLE device information
   - `Node`: ESPresense node data
   - `Config`: System configuration
   - `State`: Application state

4. **Events**: Event-based communication system
   - `GlobalEventDispatcher`: Central event hub
   - Various event types for different system changes

### REST API Endpoints

Main endpoints include:

```
GET /api/state/devices - List all devices
GET /api/state/nodes - List all nodes
GET /api/state/config - Get system configuration
GET /api/device/{id} - Get specific device
PUT /api/device/{id} - Update device
GET /api/node/{id} - Get specific node
PUT /api/node/{id} - Update node
```

### WebSocket Implementation

The primary WebSocket endpoint is at `/ws` and is implemented in `StateController.cs`:

```csharp
[Route("/ws")]
public async Task Get([FromQuery] bool showAll = false)
{
    // Check if it's a WebSocket request
    if (!HttpContext.WebSockets.IsWebSocketRequest)
    {
        HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Set up event handlers
    AsyncAutoResetEvent newMessage = new AsyncAutoResetEvent();
    ConcurrentQueue<string> changes = new ConcurrentQueue<string>();

    // Define event handlers
    void OnConfigChanged(object? sender, Config e) =>
        EnqueueAndSignal(new { type = "configChanged" });
    void OnDeviceChanged(object? sender, DeviceEventArgs e) =>
        EnqueueAndSignal(new { type = "deviceChanged", data = e.Device });

    // Subscribe to events
    _config.ConfigChanged += OnConfigChanged;
    _eventDispatcher.DeviceStateChanged += OnDeviceChanged;

    try
    {
        // Accept WebSocket connection
        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();

        // Send initial time sync
        EnqueueAndSignal(new { type = "time", data = DateTime.UtcNow.RelativeMilliseconds() });

        // Main message loop
        while (!webSocket.CloseStatus.HasValue)
        {
            // Send any queued messages
            while (changes.TryDequeue(out var jsonEvent))
                await webSocket.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(jsonEvent)),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);

            // Wait for new messages
            await newMessage.WaitAsync();
        }

        // Close WebSocket properly
        await webSocket.CloseAsync(
            webSocket.CloseStatus.Value,
            webSocket.CloseStatusDescription,
            CancellationToken.None);
    }
    finally
    {
        // Unsubscribe from events
        _config.ConfigChanged -= OnConfigChanged;
        _eventDispatcher.DeviceStateChanged -= OnDeviceChanged;
    }
}
```

Key aspects:
- WebSocket messages are JSON formatted with a `type` field indicating the message type
- Common message types include: `deviceChanged`, `configChanged`, `nodeStateChanged`, `time`
- Events from the backend are queued and sent to all connected clients
- Proper cleanup when connections close

## Frontend (Svelte) Architecture

### Main Components

1. **Pages**: SvelteKit routes in `/routes` directory
   - `/`: Map view (main interface)
   - `/devices`: Device management
   - `/nodes`: Node management
   - `/calibration`: Device calibration

2. **Core Components**:
   - `Map.svelte`: Main visualization using D3.js
   - `DeviceMarker.svelte`: Device visualization
   - `NodeMarker.svelte`: Node visualization
   - Tables for devices, nodes, etc.

3. **Stores**: Svelte reactive stores
   - `devices`: Device data
   - `nodes`: Node data
   - `config`: System configuration
   - UI state stores (showAll, showAll, etc.)

### Store Implementation

The store system is the central part of frontend state management, implemented in `stores.ts`:

```typescript
// Simple writable store
export const config = writable<Config>();

// Derived store with API fetching and WebSocket
export const devices = derived<[typeof showAll], Device[]>(
    [showAll],
    ([$showAll], set) => {
        let deviceMap = new Map();
        var q = (new URLSearchParams({
            showAll: $showAll ? "true" : "false"
        })).toString();

        // Update function
        function updateDevicesFromMap() {
            const devicesArray = Array.from(deviceMap.values());
            set(devicesArray);
        }

        // Fetch initial data and set up polling
        function fetchDevices() {
            fetch(`${base}/api/state/devices?${q}`)
                .then((d) => d.json())
                .then((r) => {
                    deviceMap = new Map(r.map((device: Device) => [device.id, device]));
                    updateDevicesFromMap();
                });
        }
        fetchDevices();
        const interval = setInterval(fetchDevices, 60000);

        // Set up WebSocket for real-time updates
        function setupWebsocket() {
            const loc = new URL(`${base}/ws?${q}`, window.location.href);
            const new_uri = (loc.protocol === 'https:' ? 'wss:' : 'ws:') +
                '//' + loc.host + loc.pathname + loc.search;
            const socket = new WebSocket(new_uri);

            socket.addEventListener('message', async function (event) {
                const eventData = JSON.parse(event.data);
                if (eventData.type === 'deviceChanged' && eventData.data?.id) {
                    deviceMap.set(eventData.data.id, eventData.data);
                    updateDevicesFromMap();
                } else if (eventData.type === 'configChanged') {
                    getConfig();
                } else if (eventData.type === 'time') {
                    relative.set(eventData.data);
                }
            });

            return socket;
        }
        const socket = setupWebsocket();

        // Cleanup function (called on unsubscribe)
        return () => {
            clearInterval(interval);
            socket.close();
        };
    }
);
```

Key aspects:
- Stores combine polling (for reliability) with WebSockets (for real-time updates)
- Map-based storage enables efficient updates (by device ID)
- Proper cleanup functions prevent memory leaks
- Store dependencies (derived stores) create reactive relationships

### WebSocket Communication

1. **Connection Setup**:
```typescript
function setupWebsocket() {
    const loc = new URL(`${base}/ws?${q}`, window.location.href);
    const new_uri = (loc.protocol === 'https:' ? 'wss:' : 'ws:') +
        '//' + loc.host + loc.pathname + loc.search;
    const socket = new WebSocket(new_uri);
    // Set up event handlers
    return socket;
}
```

2. **Message Handling**:
```typescript
socket.addEventListener('message', async function (event) {
    const eventData = JSON.parse(event.data);
    if (eventData.type === 'deviceChanged' && eventData.data?.id) {
        // Update device in map and trigger store update
        deviceMap.set(eventData.data.id, eventData.data);
        updateDevicesFromMap();
    } else if (eventData.type === 'configChanged') {
        // Refresh configuration
        getConfig();
    } else if (eventData.type === 'time') {
        // Update time synchronization
        relative.set(eventData.data);
    }
});
```

3. **Message Format**:
WebSocket messages follow this JSON structure:
```json
{
  "type": "deviceChanged",
  "data": {
    "id": "device_id",
    "name": "Device Name",
    "rssi": -70,
    "distance": 2.5,
    "x": 10.2,
    "y": 5.3,
    "room": "Living Room",
    "floor": 1,
    "track": true
  }
}
```

Common message types:
- `deviceChanged`: Device data updated
- `configChanged`: System configuration changed
- `nodeStateChanged`: Node status changed
- `time`: Time synchronization

## Data Flow Examples

### Creating a New Device

1. **Frontend**:
   - User fills out device form
   - Form data is submitted via `fetch`:
   ```javascript
   const response = await fetch(`/api/device/${id}`, {
     method: 'PUT',
     headers: { 'Content-Type': 'application/json' },
     body: JSON.stringify(deviceData)
   });
   ```

2. **Backend**:
   - `DeviceController.Put(string id, DeviceSettings settings)` handles the request
   - Updates device settings in storage
   - Publishes to MQTT if needed
   - Emits `DeviceStateChanged` event

3. **Real-time Update**:
   - `DeviceStateChanged` event triggers WebSocket message
   - Connected clients receive the update
   - Frontend store updates with new device data
   - UI components reactively update

### Creating a New UI Component

To create a new UI component that integrates with the system:

1. **Subscribe to stores**:
```svelte
<script>
  import { devices, nodes, config } from '$lib/stores';

  // Reactive declarations using store values
  $: relevantDevices = $devices.filter(d => d.track);
  $: nodeMap = new Map($nodes.map(n => [n.id, n]));
</script>
```

2. **Use WebSocket data**:
The component will automatically receive updates via the store subscriptions. No additional WebSocket handling is needed.

3. **Update data**:
```javascript
async function updateDevice(id, data) {
  const response = await fetch(`/api/device/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data)
  });

  if (!response.ok) {
    throw new Error('Failed to update device');
  }

  // No need to update local store - WebSocket will handle it
}
```

4. **Map backend data types to frontend**:
Ensure your component handles the same data structures as defined in the TypeScript interfaces (like `Device`, `Node`, etc.) in `types.ts`.

## Best Practices

1. **Reactive Store Usage**:
   - Subscribe to stores with `$` prefix
   - Use derived stores for computed values
   - Always implement cleanup for custom stores

2. **WebSocket Handling**:
   - Let stores handle WebSocket communication
   - Watch for specific event types your component needs
   - Build idempotent update handlers

3. **API Interaction**:
   - Use `fetch` for CRUD operations
   - Don't manually update stores after API calls (WebSocket will handle it)
   - Handle loading/error states appropriately

4. **Component Design**:
   - Keep components focused on single responsibilities
   - Pass data down via props
   - Use events to communicate upward
   - Leverage Svelte's reactivity system

## Conclusion

The ESPresense-companion architecture provides a robust, real-time system for managing BLE device tracking. The combination of REST APIs for CRUD operations and WebSockets for real-time updates creates a responsive user experience while maintaining data consistency.

When building new features, leverage the existing store system and WebSocket infrastructure to ensure your components stay in sync with the application state. Follow the patterns established in existing components for consistency and maintainability.