# Building

Git Clone, Open folder in VSCode, it'll ask to install dependencies, click yes, you'll need to install .NET SDK 8.0 as well then type into the terminal:

`dotnet watch --project src`

Browse to [http://localhost:5279/]

While developing you use vite as your webserver and it proxies /api and /ws to the dotnet webserver.

On deployment it's the opposite, the dotnet server runs and serves up precompiled html and js files.

Join us on discord for development talk: [https://discord.gg/jbqmn7V6n6]