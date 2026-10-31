# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

ESPresense-companion is a Home Assistant Add-on / Docker container that processes indoor position data from ESPresense BLE nodes. It's a full-stack application with:

- **Backend**: ASP.NET Core (.NET 8.0) with C# providing REST APIs, WebSocket communication, and MQTT integration
- **Frontend**: SvelteKit 5 with TypeScript providing a reactive web interface for device visualization and management
- **Database**: SQLite for storing historical data and device settings
- **Communication**: MQTT for node communication, WebSockets for real-time UI updates

## Development Commands

### Backend (C#)
```bash
# Start development server (backend + UI)
dotnet run --project src

# Same, with hot reload. Use ./watch, NOT `dotnet watch` -- see below
./watch

# Run tests
dotnet test

# Build the project
dotnet build --project src

# Restore dependencies
dotnet restore
```

### Frontend (Svelte 5)
```bash
# Navigate to UI directory
cd src/ui

# Install dependencies
pnpm install

# Start development server (proxies to backend)
pnpm start

# Build for production
pnpm build

# Run tests
pnpm test

# Run unit tests
pnpm test:unit

# Lint code
pnpm lint

# Format code
pnpm format

# Type check
pnpm check
```

### Development Setup
1. Install .NET SDK 8.0 and Node.js 22
2. Install pnpm globally: `npm install -g pnpm`
3. Install frontend dependencies: `cd src/ui && pnpm install` (the Debug build no longer does this; the SPA proxy launches `pnpm start`, which needs `node_modules`)
4. Run `dotnet run --project src`
5. Browse to http://localhost:5279/ -- it 302s to the Vite dev server on 44490, which proxies `/api` and `/ws` back to 5279

### Use `./watch`, not bare `dotnet watch`

`launchSettings.json` sets `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=Microsoft.AspNetCore.SpaProxy`.
That hosting startup is what launches `pnpm start` and redirects `/` to the Vite dev server.
Since SDK 9.0.203 (reproduced on 10.0.100), `dotnet watch` replaces that variable with its own
`Microsoft.AspNetCore.Watch.BrowserRefresh`, so SpaProxy never loads: Vite is never started and
`http://localhost:5279/` 404s on everything except `/api`. Other launch-profile variables (`ASPNETCORE_URLS`, `ASPNETCORE_ENVIRONMENT`) come through
fine -- watch composes this one variable from a list it never seeds with the profile's value, so the
composed value wins. See dotnet/sdk#48480.

`./watch` sets `DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH=1`, which stops watch injecting its hosting
startup, so SpaProxy survives and hot reload still works. Nothing is lost -- Vite owns the page and
does its own HMR, so watch's browser refresh was never useful for this SPA.

Check which one is loaded with:

```bash
ps eww -p $(pgrep -f ESPresense.Companion) | tr ' ' '\n' | grep HOSTINGSTARTUP
```

## Architecture Overview

### Backend Structure
- **Controllers/**: HTTP and WebSocket endpoints (`DeviceController`, `NodeController`, `StateController`, etc.)
- **Services/**: Business logic (`MqttCoordinator`, `DeviceTracker`, `MultiScenarioLocator`, etc.)
- **Models/**: Data structures (`Device`, `Node`, `Config`, `State`)
- **Events/**: Event-driven communication system with `GlobalEventDispatcher`
- **Locators/**: Trilateration algorithms for position calculation
- **Optimizers/**: Machine learning optimizers for improving accuracy
- **Extensions/**: Utility extensions for various types

### Frontend Structure
- **routes/**: SvelteKit pages (`/`, `/devices`, `/nodes`, `/calibration`, etc.)
- **lib/**: Reusable Svelte components (`Map.svelte`, `DeviceMarker.svelte`, etc.)
- **lib/stores.ts**: Reactive state management using Svelte stores
- **lib/types.ts**: TypeScript type definitions
- **lib/wsManager.ts**: WebSocket management utilities

### Svelte 5 Event Handling
- **DOM Events**: Use camelCase syntax (e.g., `onclick`, `onchange`, `onmouseover`)
- **Custom Events**: Use function props instead of events (e.g., `onselected`, `onhovered`, `onclose`)
- **Component Communication**: Pass functions as props rather than dispatching custom events

### Data Flow
1. **MQTT**: ESPresense nodes → Backend via MQTT
2. **Processing**: Backend processes distance data using trilateration
3. **Storage**: Results stored in SQLite database
4. **API**: Frontend fetches data via REST API
5. **Real-time**: WebSocket updates push changes to connected clients
6. **Stores**: Svelte stores manage state and trigger UI updates

## Key Patterns

### WebSocket Communication
- Main WebSocket endpoint at `/ws`
- JSON messages with `type` field: `deviceChanged`, `configChanged`, `nodeStateChanged`, `time`
- Frontend stores automatically handle WebSocket updates
- Backend uses `GlobalEventDispatcher` to trigger WebSocket messages

### Store Management
- Frontend uses derived stores that combine polling (reliability) + WebSockets (real-time)
- Map-based storage for efficient updates by ID
- Proper cleanup functions prevent memory leaks
- Store dependencies create reactive relationships

### API Patterns
- REST endpoints follow `/api/{controller}/{id}` pattern
- PUT for updates, GET for retrieval
- WebSocket automatically syncs changes after API calls
- Error handling and loading states in UI components

### Device Data Architecture
- **Device Model**: Operational device data with location, confidence, node connections
  - `RefRssi` (`"rssi@1m"`): Only shows manually configured calibration values
  - `MeasuredRefRssi` (`"measuredRssi@1m"`): Real-time average from active node measurements
  - `ConfiguredRefRssi`: Internal storage for manual calibration settings
- **DeviceSettings Model**: Configuration and calibration data stored persistently
  - Used for manual RSSI@1m calibration values
  - Separate from operational device state
- **StateController**: Populates Device.ConfiguredRefRssi from DeviceSettings during API calls
- **Calibration Status**: Determined only by configured values, not measured values

### Event-Driven Architecture
- `GlobalEventDispatcher` coordinates events between services
- Event types: `DeviceStateChanged`, `NodeStateChanged`, `CalibrationChanged`
- WebSocket clients automatically receive relevant events
- Services subscribe to events for reactive updates

## Testing

### Backend Tests (NUnit)
```bash
# Run all tests
dotnet test

# Run specific test file
dotnet test --filter "ClassName=DeviceTrackerTests"
```

### Frontend Tests (Playwright + Vitest)
```bash
cd src/ui

# Run Playwright tests
pnpm test

# Run unit tests
pnpm test:unit
```

## Key Files for Development

### Backend Entry Point
- `src/Program.cs`: Application startup, dependency injection, middleware configuration

### Frontend Entry Point
- `src/ui/src/routes/+layout.svelte`: Root layout component
- `src/ui/src/lib/stores.ts`: Global state management

### Configuration
- `src/config.example.yaml`: Example configuration file
- `src/appsettings.json`: Backend configuration
- `src/ui/vite.config.ts`: Frontend build configuration

### API Documentation
- Swagger UI available at `/api/swagger` when running in development
- Key endpoints documented in `ESPresense-companion-architecture.md`

## Development Tips

1. **Backend Changes**: `dotnet run --project src` serves both halves; `./watch` does the same with hot reload
2. **Frontend Changes**: Vite provides hot module reloading
3. **Database**: SQLite database stored in `~/.espresense/.storage/history.db`
4. **MQTT**: Use tools like MQTT Explorer to debug MQTT communication
5. **WebSocket**: Browser dev tools Network tab shows WebSocket messages
6. **Logging**: Serilog provides structured logging with configurable levels

## Common Tasks

- **Add new device property**: Update `Device` model, database schema, API endpoints, and frontend types
- **Create new locator**: Implement `ILocate` interface in `Locators/` directory
- **Add new UI component**: Create in `lib/` directory, subscribe to stores, handle WebSocket updates
- **Modify optimization**: Update relevant optimizer in `Optimizers/` directory
- **Add new API endpoint**: Create controller action, update frontend API calls
- **Working with device calibration**:
  - Configured values: Use `DeviceSettings` model and `ConfiguredRefRssi` property
  - Measured values: Access `MeasuredRefRssi` for operational/debugging data
  - Calibration UI: Should only show "Calibrated" status based on configured values

## Important Architecture Notes

- **Device vs DeviceSettings**: Device is operational state, DeviceSettings is persistent configuration
- **RSSI@1m Separation**: Configured values determine calibration status, measured values provide operational data
- **Store Updates**: Frontend stores combine polling + WebSocket for reliability and real-time updates
- **Service Injection**: StateController requires DeviceSettingsStore to populate calibration data
- **Event Propagation**: Changes trigger GlobalEventDispatcher events → WebSocket messages → Frontend updates