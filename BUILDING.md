# Building

This project uses a C# backend and a SvelteKit frontend. To run it locally you need the **.NET 8 SDK** and **Node.js 20**.

1. Clone the repository and install dependencies:
   ```bash
   git clone https://github.com/ESPresense/ESPresense-companion.git
   cd ESPresense-companion
   dotnet restore
   npm install --prefix src/ui
   ```

2. Start the backend and frontend in watch mode:
   ```bash
   dotnet watch --project src
   npm run --prefix src/ui dev
   ```
   The UI will be available at [http://localhost:5279](http://localhost:5279).

3. To run the unit tests:
   ```bash
   dotnet test -c Release --no-build --verbosity normal --filter "Category!=LongRunning"
   ```

For development discussion join us on [Discord](https://discord.gg/jbqmn7V6n6).
