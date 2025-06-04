# Building

Git Clone, open the folder in VSCode and install dependencies when prompted. You will need to install the .NET SDK 8.0 and Node.js 20. Install pnpm globally using `npm install -g pnpm` then type into the terminal:

`dotnet watch --project src`

Browse to [http://localhost:5279/]

While developing you use vite as your webserver and it proxies /api and /ws to the dotnet webserver.

On deployment it's the opposite, the dotnet server runs and serves up precompiled html and js files.

Join us on discord for development talk: [https://discord.gg/jbqmn7V6n6]
