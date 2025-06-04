# Contributing

Thank you for taking the time to contribute to ESPresense-companion. This guide outlines the basic development workflow.

## Quick start

1. Install the **.NET 8 SDK** and **Node.js 20**.
2. Clone the repository and install dependencies:
   ```bash
   git clone https://github.com/ESPresense/ESPresense-companion.git
   cd ESPresense-companion
   dotnet restore
   npm install --prefix src/ui
   ```
3. Run the backend and frontend in watch mode:
   ```bash
   dotnet watch --project src
   npm run --prefix src/ui dev
   ```
   The UI will be available at [http://localhost:5279](http://localhost:5279).
4. Execute the unit tests:
   ```bash
   dotnet test -c Release --no-build --verbosity normal --filter "Category!=LongRunning"
   ```

For additional details see [BUILDING.md](BUILDING.md) or join the [Discord community](https://discord.gg/jbqmn7V6n6).
